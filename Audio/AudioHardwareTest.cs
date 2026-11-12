using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AudioRouter.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioRouter.Audio;

/// <summary>Explicit CLI-only integration test using controlled playback and a zero-gain physical mic.</summary>
public static class AudioHardwareTest
{
    public static async Task RunSourceAsync(string directory)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var signal = new SignalGenerator(48000,2) { Gain=0.003,Frequency=440,Type=SignalGeneratorType.Sin };
        using var devices = new MMDeviceEnumerator();
        var chosen = Path.Combine(directory, "source-endpoint");
        using var headphone = File.Exists(chosen)
            ? devices.GetDevice(File.ReadAllText(chosen)) : devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var player = new WasapiPlayerBuilder().WithDevice(headphone).WithSharedMode().WithLatency(40).Build();
        player.Init(new SampleToWaveProvider(signal));
        File.WriteAllText(Path.Combine(directory,"child-ready"),Environment.ProcessId.ToString());
        while(!File.Exists(Path.Combine(directory,"child-go"))) await Task.Delay(30,stop.Token);
        player.Play();
        while(!File.Exists(Path.Combine(directory,"child-stop"))) await Task.Delay(30,stop.Token);
    }

    public static async Task<IReadOnlyList<string>> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        List<string> results=[];
        var devices=AudioDeviceService.GetDevices().Where(d=>!d.IsInput&&!d.IsVirtualCable).ToList();
        var headphone=devices.FirstOrDefault(d=>d.Name.Contains("HECATE",StringComparison.OrdinalIgnoreCase))??devices.First();
        var speaker=devices.FirstOrDefault(d=>d.Name.Contains("Philips",StringComparison.OrdinalIgnoreCase)&&d.Id!=headphone.Id)
            ??devices.FirstOrDefault(d=>d.Id!=headphone.Id)??throw new InvalidOperationException("双输出实测需要两个播放设备。");
        File.WriteAllText(Path.Combine(directory, "source-endpoint"), headphone.Id);
        // Use an isolated child as the sole process-loopback source.
        var exe=Environment.ProcessPath!;
        var start=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
        if(Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(AudioHardwareTest).Assembly.Location);
        start.ArgumentList.Add("--test-source"); start.ArgumentList.Add(directory);
        using var child=Process.Start(start)??throw new InvalidOperationException("无法启动测试音源。");
        ApplicationRouteSnapshot? snapshot=null;
        try
        {
            var watch=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(directory,"child-ready")))
            {
                if(child.HasExited||watch.Elapsed>TimeSpan.FromSeconds(8)) throw new Exception("测试音源未就绪。");
                await Task.Delay(40);
            }
            snapshot=AudioPolicyRouter.Capture(child.Id);
            if(!AudioPolicyRouter.TrySetApplicationOutput(child.Id,headphone.Id,out var failure)) throw new Exception(failure);
            if(!AudioPolicyRouter.TryGetApplicationOutput(child.Id,out var persisted,out failure)||persisted!=headphone.Id)
                throw new Exception("应用输出策略读回不匹配："+failure);
            if(!AudioPolicyRouter.TryRestore(snapshot,out failure)) throw new Exception("输出策略恢复失败："+failure);
            if(!AudioPolicyRouter.TryGetApplicationOutput(child.Id,out persisted,out failure)||persisted!=snapshot.MultimediaDeviceId)
                throw new Exception("恢复后应用输出策略不匹配："+failure);
            results.Add("PASS per-application native output set/get and exact restoration on synthetic child");

            var wave=Path.Combine(directory,"quiet-tone.wav");
            using(var writer=new WaveFileWriter(wave,WaveFormat.CreateIeeeFloatWaveFormat(24000,1)))
            {
                var data=Enumerable.Range(0,24000).Select(i=>(float)(0.003*Math.Sin(2*Math.PI*330*i/24000))).ToArray();
                writer.WriteSamples(data,0,data.Length);
            }
            await using var engine=new AudioEngine();
            var errors=new ConcurrentQueue<string>(); engine.Error+=(_,message)=>errors.Enqueue(message);
            var outputs=new[] { new OutputConfig { Id="h",Name="Headphone",DeviceId=headphone.Id },new OutputConfig { Id="s",Name="Speaker",DeviceId=speaker.Id } };
            await engine.StartAsync([new() { Id="file",Name="Test",Kind=SourceKind.File,FilePath=wave,Loop=true,DestinationIds=["h","s"] }],outputs);
            await CheckMeters(engine,"file",["h","s"],errors);
            await engine.StopAsync();
            results.Add("PASS mono 24 kHz file resampled and simultaneously rendered to "+headphone.Name+" + "+speaker.Name);
            var router = new LiveRouter();
            File.WriteAllText(Path.Combine(directory, "child-go"), "go");
            try
            {
                AudioApplicationInfo? playing = null;
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    playing = AudioDeviceService.GetApplications().FirstOrDefault(a => a.ProcessId == child.Id && a.IsActive);
                    if (playing != null && playing.Peak > 0.00001f) break;
                    await Task.Delay(60);
                }
                if (playing == null || playing.Peak <= 0.00001f || string.IsNullOrWhiteSpace(playing.Name))
                    throw new InvalidOperationException("未自动发现正在出声的测试应用。");
                results.Add("PASS discovery of a currently playing named application and nonzero session peak");
                var directDevice = playing.ActiveDeviceIds.Single();
                if (directDevice != headphone.Id) throw new Exception("测试源没有在指定的实体耳机播放。");
                var otherDevice = directDevice == headphone.Id ? speaker : headphone;
                var original = new OutputConfig { Id = directDevice, DeviceId = directDevice, Name = playing.DeviceName! };
                var copy = new OutputConfig { Id = otherDevice.Id, DeviceId = otherDevice.Id, Name = otherDevice.Name };
                var source = new SourceConfig { Id = "process", Kind = SourceKind.Process, Name = playing.Name, ProcessId = (uint)child.Id };
                await router.ConnectAsync(source, playing, [original, copy]);
                await CheckMeters(router.Engine, source.Id, [copy.Id], errors);
                if (router.Engine.GetMeters().OutputPeaks.ContainsKey(original.Id)) throw new Exception("原声设备收到重复副本。");
                await CheckSessionSignal(child.Id, directDevice);
                await CheckSessionSignal(Environment.ProcessId, copy.Id);
                results.Add("PASS already-playing audio observed in Windows render sessions on BOTH physical devices without a duplicate at the original");
                await router.ConnectAsync(source, playing, [original]);
                if (router.Engine.GetMeters().OutputPeaks.ContainsKey(copy.Id)) throw new Exception("取消的输出仍在接收副本。");
                await CheckSessionSignal(child.Id, directDevice);
                results.Add("PASS disconnecting one output keeps the original application's playback active");
                await router.ConnectAsync(source, playing, []);
                var silent = AudioDeviceService.GetApplications().Single(a => a.ProcessId == child.Id);
                if (silent.AudibleDeviceIds.Length != 0) throw new Exception("取消所有输出后仍有未静音的原声会话。");
                await router.ConnectAsync(source, silent, [original, copy]);
                await CheckMeters(router.Engine, source.Id, [copy.Id], errors);
                await CheckSessionSignal(child.Id, directDevice);
                results.Add("PASS all outputs off and reconnecting the same running application");
                var pending = await router.ConnectAsync(source, AudioDeviceService.GetApplications().Single(a => a.ProcessId == child.Id), [copy]);
                var migrated = AudioDeviceService.GetApplications().Single(a => a.ProcessId == child.Id);
                if (migrated.AudibleDeviceIds.Contains(original.Id))
                {
                    if (pending == null) throw new Exception("固定设备播放器未切换，必须显示仍在出声的原设备。");
                    await CheckSessionSignal(Environment.ProcessId, copy.Id);
                    results.Add("PASS fixed-device stream: selected output receives live audio and original endpoint is explicitly reported as pending");
                }
                else
                {
                    if (pending != null) throw new Exception("已迁移仍显示等待。");
                    await CheckSessionSignal(child.Id, copy.Id);
                    results.Add("PASS native live output migration observed on the selected endpoint");
                }
                await router.ConnectAsync(source, migrated, [original, copy]);
                var forwarded = migrated.ActiveDeviceIds.Contains(original.Id) ? copy.Id : original.Id;
                await CheckMeters(router.Engine, source.Id, [forwarded], errors);
                router.Engine.SetSourceMuted(source.Id, true);
                await Task.Delay(250);
                if (router.Engine.GetMeters().OutputPeaks.Values.Any(x => x > 0.00001)) throw new Exception("转发静音失败。");
                await router.ReleaseAsync(source.Id);
                await CheckSessionSignal(child.Id, directDevice);
                results.Add("PASS releasing the managed app restores ordinary playback");
                var devicesNow = AudioDeviceService.GetDevices();
                var cableIn = devicesNow.FirstOrDefault(d => !d.IsInput && d.Name.Equals("CABLE Input (VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase));
                var cableOut = devicesNow.FirstOrDefault(d => d.IsInput && d.Name.Equals("CABLE Output (VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase));
                if (cableIn != null && cableOut != null)
                {
                    var cable = new OutputConfig { Id = cableIn.Id, DeviceId = cableIn.Id, Name = cableIn.Name };
                    var mic = devicesNow.Where(d => d.IsInput && !d.IsVirtualCable)
                        .OrderByDescending(d => d.Name.Contains("HECATE", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(d => d.IsDefault).First();
                    await router.ConnectAsync(source, AudioDeviceService.GetApplications().Single(a => a.ProcessId == child.Id), [original, cable]);
                    await router.ConnectAsync(new SourceConfig { Id="marker", Kind=SourceKind.File, FilePath=wave, Loop=true }, null, [cable]);
                    // Exercise the actual mic capture path while the deterministic second source supplies the audible marker.
                    await router.ConnectAsync(new SourceConfig { Id="microphone", Kind=SourceKind.Microphone, DeviceId=mic.Id, Gain=0 }, null, [cable]);
                    var tones = await ReadCableTonesAsync(cableOut.Id);
                    if (!router.Engine.ReceivedFrames("microphone")) throw new Exception("耳机麦克风未提供数据。");
                    if (!GameMicrophoneCheck.Read().ActiveSessions.Any(s => s.ProcessId == Environment.ProcessId && s.Device == mic.Name))
                        throw new Exception("游戏麦检查未识别正在使用实体话筒的测试进程。");
                    results.Add("PASS game microphone check identified the active capture process and its physical recording endpoint");
                    if (tones.Process < 0.00015 || tones.File < 0.00015)
                        throw new Exception($"虚拟麦克风未收到两个独立测试音：440 Hz={tones.Process:G3}，330 Hz={tones.File:G3}");
                    results.Add($"PASS virtual recording endpoint {cableOut.Name} contains live app (440 Hz, {tones.Process:G3}) + second source (330 Hz, {tones.File:G3})");
                    results.Add($"PASS physical mic {mic.Name} produced packets on the same live mix (zero gain during automated test)");
                    await router.ConnectAsync(source, AudioDeviceService.GetApplications().Single(a => a.ProcessId == child.Id), [original]);
                    var disconnected = await ReadCableTonesAsync(cableOut.Id);
                    if (disconnected.Process > tones.Process * 0.15 || disconnected.File < tones.File * 0.65)
                        throw new Exception("断开应用后虚拟麦仍有该音源，或另一音源中断。");
                    await CheckSessionSignal(child.Id, headphone.Id);
                    results.Add("PASS disconnect removed only the app tone from the virtual mic while the app kept playing on headphones");
                }
                else results.Add("SKIP virtual microphone verification: the standard VB-CABLE pair was not present");
            }
            finally
            {
                await router.ReleaseAsync("process");
                await router.Engine.DisposeAsync();
            }
            if(errors.TryPeek(out var error)) throw new Exception(error);
            return results;
        }
        finally
        {
            if(snapshot!=null&&!child.HasExited&&!AudioPolicyRouter.TryRestore(snapshot,out var error))
                File.WriteAllText(Path.Combine(directory,"restore-error.txt"),error);
            File.WriteAllText(Path.Combine(directory,"child-stop"),"stop");
            if(!child.HasExited)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await child.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { child.Kill(); }
            }
        }
    }

    private static async Task CheckSessionSignal(int pid, string endpoint)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            bool signal = false;
            AudioDeviceService.VisitSessions((session, device) =>
            {
                if (device.ID == endpoint && session.GetProcessID == pid && !session.SimpleAudioVolume.Mute &&
                    session.AudioMeterInformation.MasterPeakValue > 0.00001f) signal = true;
            });
            if (signal) return;
            await Task.Delay(40);
        }
        throw new Exception($"Windows 未在指定输出口测到 PID {pid} 的测试音。");
    }

    private static async Task<(double Process, double File)> ReadCableTonesAsync(string recordingDevice)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(recordingDevice);
        await using var recorder = new WasapiRecorderBuilder().WithDevice(device)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(30).Build();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new float[24000];
        int position = 0;
        int skip = 9600;
        recorder.DataAvailable += (data, flags, _, _) =>
        {
            if (position == left.Length) return;
            var frames = MemoryMarshal.Cast<byte, float>(data);
            for (int i = 0; i + 1 < frames.Length && position < left.Length; i += 2)
            {
                if (skip > 0) { skip--; continue; }
                left[position++] = flags.HasFlag(AudioClientBufferFlags.Silent) ? 0 : frames[i];
            }
            if (position == left.Length) done.TrySetResult(true);
        };
        recorder.StartRecording();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(4));
        double MedianAmplitude(int hz)
        {
            // Analyze consecutive 100 ms windows. A buffer discontinuity between
            // windows must not phase-cancel the measurements across the whole read.
            const int samplesPerWindow = 4800;
            var amplitudes = new double[left.Length / samplesPerWindow];
            for (int window = 0; window < amplitudes.Length; window++)
            {
                double real = 0, imag = 0;
                for (int i = 0; i < samplesPerWindow; i++)
                {
                    double phase = 2 * Math.PI * hz * i / 48000;
                    var sample = left[window * samplesPerWindow + i];
                    real += sample * Math.Cos(phase);
                    imag += sample * Math.Sin(phase);
                }
                amplitudes[window] = 2 * Math.Sqrt(real * real + imag * imag) / samplesPerWindow;
            }
            Array.Sort(amplitudes);
            return amplitudes[amplitudes.Length / 2];
        }
        return (MedianAmplitude(440), MedianAmplitude(330));
    }

    private static async Task CheckMeters(AudioEngine engine,string source,string[] outputs,ConcurrentQueue<string> errors)
    {
        var seen=new HashSet<string>(); bool hasSource=false;
        for(var i=0;i<80;i++)
        {
            await Task.Delay(35);
            if(errors.TryPeek(out var error)) throw new Exception(error);
            var meters=engine.GetMeters();
            hasSource|=meters.SourcePeaks.GetValueOrDefault(source)>0.0001;
            foreach(var id in outputs) if(meters.OutputPeaks.GetValueOrDefault(id)>0.0001) seen.Add(id);
            if(hasSource&&outputs.All(seen.Contains)) return;
        }
        throw new Exception($"未检测到有效测试信号。source={hasSource}, outputs={string.Join(',',seen)}");
    }
}
