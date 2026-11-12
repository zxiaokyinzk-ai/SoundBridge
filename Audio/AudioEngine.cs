using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioRouter.Audio;

/// <summary>
/// Explicitly started WASAPI router. Construction and configuration never start a microphone.
/// Process capture is a copy of the selected process tree, not a change to its original output.
/// </summary>
public sealed class AudioEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, SourceRuntime> _sources = new();
    private readonly ConcurrentDictionary<string, OutputRuntime> _outputs = new();
    private bool _running;
    private bool _stopping;
    public bool IsRunning => Volatile.Read(ref _running);
    internal bool ReceivedFrames(string id) => _sources.TryGetValue(id, out var s) && Volatile.Read(ref s.Controls.LastPacketTicks) != 0;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? SourceCompleted;

    public async Task StartAsync(IEnumerable<SourceConfig> sources, IEnumerable<OutputConfig> outputs)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            var sourceList = sources.ToList();
            var outputList = outputs.ToList();
            Validate(sourceList, outputList);
            _stopping = false;
            try
            {
                foreach (var config in outputList)
                {
                    var output = new OutputRuntime(config, ReportError);
                    _outputs[config.Id] = output;
                }
                foreach (var config in sourceList)
                {
                    var source = new SourceRuntime(config, ReportError, id => SourceCompleted?.Invoke(this, id));
                    _sources[config.Id] = source;
                    foreach (var destination in config.DestinationIds.Distinct())
                        source.Branches[destination] = _outputs[destination].Mixer.Add(config.Id, source.Controls);
                    await source.InitializeAsync().ConfigureAwait(false);
                }
                foreach (var output in _outputs.Values) output.Start();
                foreach (var source in _sources.Values) source.Start();
                Volatile.Write(ref _running, true);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private static void Validate(List<SourceConfig> sources, List<OutputConfig> outputs)
    {
        if (sources.Select(x => x.Id).Distinct().Count() != sources.Count || outputs.Select(x => x.Id).Distinct().Count() != outputs.Count)
            throw new ArgumentException("音频来源或输出的标识重复。");
        if (sources.Any(x => string.IsNullOrWhiteSpace(x.Id)) || outputs.Any(x => string.IsNullOrWhiteSpace(x.Id)))
            throw new ArgumentException("音频来源或输出的标识不能为空。");
        var ids = outputs.Select(x => x.Id).ToHashSet();
        foreach (var source in sources)
        {
            ValidateGain(source.Gain);
            if (source.DestinationIds.Any(id => !ids.Contains(id))) throw new ArgumentException($"{source.Name} 的输出通道不存在。");
            if (source.Kind == SourceKind.Process && (source.ProcessId == 0 || source.ProcessId == Environment.ProcessId))
                throw new ArgumentException("请选择有效的外部应用，不能捕获声桥自身。");
        }
        foreach (var output in outputs)
        {
            ValidateGain(output.Gain);
            if (string.IsNullOrWhiteSpace(output.DeviceId)) throw new ArgumentException($"请为 {output.Name} 选择输出设备。");
        }
        // Rendering the same mix twice to the same device creates an unexpected doubled signal.
        if (outputs.Select(x => x.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Count)
            throw new ArgumentException("两个输出通道选择了同一设备，请选择不同设备。");
    }

    // Output keys are endpoint IDs. A live routing edit touches only this source's branches.
    public async Task ConnectAsync(SourceConfig config, IReadOnlyList<OutputConfig> destinations)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Validate([config], destinations.ToList());
            _stopping = false;
            if (destinations.Count == 0)
            {
                await RemoveSourceCoreAsync(config.Id).ConfigureAwait(false);
                return;
            }
            foreach (var destination in destinations)
            {
                if (_outputs.ContainsKey(destination.Id)) continue;
                var output = new OutputRuntime(destination, ReportError);
                _outputs[destination.Id] = output;
                output.Start();
            }
            bool fresh = !_sources.TryGetValue(config.Id, out var source);
            if (fresh)
            {
                source = new SourceRuntime(config, ReportError, id => SourceCompleted?.Invoke(this, id));
                _sources[config.Id] = source;
                await source.InitializeAsync().ConfigureAwait(false);
            }
            foreach (var id in source!.Branches.Keys.Except(config.DestinationIds).ToArray()) SetRoute(config.Id, id, false);
            foreach (var id in config.DestinationIds) SetRoute(config.Id, id, true);
            SetSourceGain(config.Id, config.Gain);
            SetSourceMuted(config.Id, config.Muted);
            if (fresh) source.Start();
            Volatile.Write(ref _running, true);
        }
        catch
        {
            await RemoveSourceCoreAsync(config.Id).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try { await ReleaseUnusedOutputsAsync().ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        }
    }

    private async Task RemoveSourceCoreAsync(string id)
    {
        if (!_sources.TryRemove(id, out var source)) return;
        foreach (var output in _outputs.Values) output.Mixer.Remove(id);
        await source.DisposeAsync().ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string id)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await RemoveSourceCoreAsync(id).ConfigureAwait(false);
            await ReleaseUnusedOutputsAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ReleaseUnusedOutputsAsync()
    {
        foreach (var id in _outputs.Keys)
        {
            if (_sources.Values.Any(s => s.Branches.ContainsKey(id))) continue;
            if (_outputs.TryRemove(id, out var output)) await output.DisposeAsync().ConfigureAwait(false);
        }
        Volatile.Write(ref _running, !_sources.IsEmpty);
    }

    public void SetSourceGain(string id, float gain) { ValidateGain(gain); if (_sources.TryGetValue(id, out var s)) Volatile.Write(ref s.Controls.Gain, gain); }
    public void SetSourceMuted(string id, bool muted) { if (_sources.TryGetValue(id, out var s)) Volatile.Write(ref s.Controls.Muted, muted); }
    public void SetRouteGain(string sourceId, string outputId, float gain)
    {
        ValidateGain(gain);
        if (_sources.TryGetValue(sourceId, out var source) && source.Branches.TryGetValue(outputId, out var branch))
            Volatile.Write(ref branch.Gain, gain);
    }
    public void SetOutputGain(string id, float gain) { ValidateGain(gain); if (_outputs.TryGetValue(id, out var o)) Volatile.Write(ref o.Mixer.Gain, gain); }
    public void SetOutputMuted(string id, bool muted) { if (_outputs.TryGetValue(id, out var o)) Volatile.Write(ref o.Mixer.Muted, muted); }
    public void SetRoute(string sourceId, string outputId, bool enabled)
    {
        if (!_sources.TryGetValue(sourceId, out var source) || !_outputs.TryGetValue(outputId, out var output)) return;
        if (enabled) source.Branches[outputId] = output.Mixer.Add(sourceId, source.Controls);
        else { source.Branches.TryRemove(outputId, out _); output.Mixer.Remove(sourceId); }
    }

    public AudioMeters GetMeters()
    {
        var now = Stopwatch.GetTimestamp();
        return new AudioMeters(
            _sources.ToDictionary(x => x.Key, x => now - Volatile.Read(ref x.Value.Controls.LastPacketTicks) > Stopwatch.Frequency / 3
                ? 0 : Volatile.Read(ref x.Value.Controls.Peak) * (Volatile.Read(ref x.Value.Controls.Muted) ? 0 : Volatile.Read(ref x.Value.Controls.Gain))),
            _outputs.ToDictionary(x => x.Key, x => Volatile.Read(ref x.Value.Mixer.Peak)));
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        Volatile.Write(ref _running, false);
        var sources = _sources.Values.ToArray();
        _sources.Clear();
        foreach (var source in sources)
        {
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { ReportError($"停止音频来源时出错：{ex.Message}", true); }
        }
        var outputs = _outputs.Values.ToArray();
        _outputs.Clear();
        foreach (var output in outputs)
        {
            try { await output.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { ReportError($"释放输出设备时出错：{ex.Message}", true); }
        }
    }

    private void ReportError(string message) => ReportError(message, false);
    private void ReportError(string message, bool force)
    {
        if (!_stopping || force) Error?.Invoke(this, message);
    }

    private static void ValidateGain(float gain)
    {
        if (!float.IsFinite(gain) || gain < 0 || gain > 2) throw new ArgumentOutOfRangeException(nameof(gain), "音量需在 0–200% 之间。");
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); }

    private sealed class OutputRuntime : IAsyncDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiPlayer _player;
        internal RoutingMixer Mixer { get; } = new();

        internal OutputRuntime(OutputConfig config, Action<string> error)
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(config.DeviceId);
            Mixer.Gain = config.Gain;
            Mixer.Muted = config.Muted;
            try
            {
                try { _player = Open(device); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x8889000A) &&
                    device.FriendlyName.Equals("CABLE Input (VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase))
                {
                    // The two VB-CABLE render pins feed one recording endpoint. The driver
                    // can reject the stereo pin while another shared client holds the 16ch pin.
                    string? sibling = Windows.AudioDeviceService.GetDevices().FirstOrDefault(d => !d.IsInput &&
                        d.Name.Equals("CABLE In 16ch (VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase))?.Id;
                    if (sibling == null) throw;
                    device.Dispose();
                    device = enumerator.GetDevice(sibling);
                    _player = Open(device);
                }
                _device = device;
            }
            catch { device.Dispose(); throw; }

            WasapiPlayer Open(MMDevice output)
            {
                if (output.DataFlow != DataFlow.Render || output.State != DeviceState.Active)
                    throw new InvalidOperationException($"输出设备 {config.Name} 不可用。");
                var player = new WasapiPlayerBuilder().WithDevice(output).WithSharedMode()
                    .WithLatency(20).WithMmcssThreadPriority().Build();
                player.PlaybackStopped += (_, e) => { if (e.Exception != null) error($"{config.Name} 输出已停止：{e.Exception.Message}"); };
                try { player.Init(new SampleToWaveProvider(Mixer)); return player; }
                catch { player.Dispose(); throw; }
            }
        }
        internal void Start() => _player.Play();
        public async ValueTask DisposeAsync()
        {
            try { await _player.DisposeAsync().ConfigureAwait(false); }
            finally { _device.Dispose(); }
        }
    }

    private sealed class SourceRuntime(SourceConfig config, Action<string> error, Action<string> completed) : IAsyncDisposable
    {
        internal SourceControls Controls { get; } = new() { Gain = config.Gain, Muted = config.Muted };
        internal ConcurrentDictionary<string, MixBranch> Branches { get; } = new();
        private MMDevice? _device;
        private WasapiRecorder? _recorder;
        private AudioFileReader? _reader;
        private ISampleProvider? _fileProvider;
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _filePump;
        private bool _disposed;
        private float[] _silence = [];

        internal async Task InitializeAsync()
        {
            switch (config.Kind)
            {
                case SourceKind.File:
                    if (string.IsNullOrWhiteSpace(config.FilePath) || !System.IO.File.Exists(config.FilePath))
                        throw new System.IO.FileNotFoundException($"找不到音频文件：{config.FilePath}");
                    _reader = new AudioFileReader(config.FilePath);
                    _fileProvider = ToStereo48K(_reader);
                    break;
                case SourceKind.Process:
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                        throw new PlatformNotSupportedException("按应用捕获需要 Windows 11 或 Windows build 20348 及以上。");
                    using (var process = Process.GetProcessById(checked((int)config.ProcessId)))
                        if (process.HasExited) throw new InvalidOperationException($"{config.Name} 已关闭，请刷新应用列表。");
                    _recorder = await new WasapiRecorderBuilder()
                        .WithProcessLoopback(config.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
                        .WithBufferLength(30).WithMmcssThreadPriority().BuildAsync().ConfigureAwait(false);
                    ConnectRecorder();
                    break;
                case SourceKind.Microphone:
                    if (string.IsNullOrWhiteSpace(config.DeviceId)) throw new ArgumentException("请先选择麦克风。");
                    using (var enumerator = new MMDeviceEnumerator()) _device = enumerator.GetDevice(config.DeviceId);
                    if (_device.DataFlow != DataFlow.Capture || _device.State != DeviceState.Active)
                        throw new InvalidOperationException($"麦克风 {config.Name} 不可用。");
                    _recorder = new WasapiRecorderBuilder().WithDevice(_device)
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
                        .WithBufferLength(20).WithMmcssThreadPriority().Build();
                    ConnectRecorder();
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(config.Kind));
            }
        }

        private void ConnectRecorder()
        {
            _recorder!.DataAvailable += (data, flags, _, _) =>
            {
                if (_disposed) return;
                // Requested 48 kHz stereo float keeps every source and output in one mix format.
                var samples = MemoryMarshal.Cast<byte, float>(data);
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                {
                    // WASAPI defines silent packet contents as undefined.
                    if (_silence.Length < samples.Length) _silence = new float[samples.Length];
                    Publish(_silence.AsSpan(0, samples.Length));
                }
                else Publish(samples);
            };
            _recorder.RecordingStopped += (_, e) =>
            {
                Volatile.Write(ref Controls.Peak, 0);
                if (!_disposed && e.Exception != null) error($"{config.Name} 捕获已停止：{e.Exception.Message}");
            };
        }

        internal void Start()
        {
            if (_reader != null) _filePump = Task.Run(PumpFileAsync);
            else _recorder!.StartRecording();
        }

        private void Publish(ReadOnlySpan<float> data)
        {
            float peak = 0;
            foreach (var sample in data) peak = Math.Max(peak, Math.Abs(sample));
            Volatile.Write(ref Controls.Peak, peak);
            Volatile.Write(ref Controls.LastPacketTicks, Stopwatch.GetTimestamp());
            foreach (var branch in Branches.Values) branch.Buffer.Write(data);
        }

        private async Task PumpFileAsync()
        {
            var samples = new float[960]; // 10 ms of stereo audio.
            var clock = Stopwatch.StartNew();
            long framesSent = 0;
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var count = _fileProvider!.Read(samples);
                    if (count == 0)
                    {
                        if (config.Loop && _reader!.Length > 0)
                        {
                            _reader.Position = 0;
                            _fileProvider = ToStereo48K(_reader);
                            count = _fileProvider.Read(samples);
                        }
                        if (count == 0)
                        {
                            Volatile.Write(ref Controls.Peak, 0);
                            completed(config.Id);
                            break;
                        }
                    }
                    Publish(samples.AsSpan(0, count));
                    framesSent += count / 2;
                    var delay = framesSent * 1000d / 48_000 - clock.Elapsed.TotalMilliseconds;
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _cancellation.Token).ConfigureAwait(false);
                    // Avoid flooding queues after a suspended process or sleeping computer.
                    if (delay < -200) { clock.Restart(); framesSent = 0; }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            catch (Exception ex) { error($"{config.Name} 播放失败：{ex.Message}"); }
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            await _cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_filePump != null) await _filePump.ConfigureAwait(false);
                if (_recorder != null) await _recorder.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _reader?.Dispose();
                _device?.Dispose();
                _cancellation.Dispose();
                Branches.Clear();
                Controls.Peak = 0;
            }
        }
    }

    internal static ISampleProvider ToStereo48K(ISampleProvider source)
    {
        ISampleProvider stereo = source.WaveFormat.Channels == 2 ? source : new StereoAdapter(source);
        return stereo.WaveFormat.SampleRate == 48_000 ? stereo : new WdlResamplingSampleProvider(stereo, 48_000);
    }
}

/// <summary>Duplicates mono; for multichannel files averages alternating channels into L/R.</summary>
internal sealed class StereoAdapter(ISampleProvider source) : ISampleProvider
{
    private float[] _scratch = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    public int Read(Span<float> buffer)
    {
        var channels = source.WaveFormat.Channels;
        var requested = buffer.Length / 2 * channels;
        if (_scratch.Length < requested) _scratch = new float[requested];
        var count = source.Read(_scratch.AsSpan(0, requested));
        var frames = count / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * channels;
            if (channels == 1) buffer[frame * 2] = buffer[frame * 2 + 1] = _scratch[offset];
            else
            {
                float left = 0, right = 0;
                for (var c = 0; c < channels; c++)
                    if (c % 2 == 0) left += _scratch[offset + c]; else right += _scratch[offset + c];
                buffer[frame * 2] = left / ((channels + 1) / 2);
                buffer[frame * 2 + 1] = right / (channels / 2);
            }
        }
        return frames * 2;
    }
}
