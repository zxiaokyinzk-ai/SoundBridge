using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioRouter.Windows;

// Read the recording endpoint used by voice clients. Never send the samples to a speaker.
internal sealed class CableSignalMonitor : IAsyncDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private long _lastPacket;
    private int _peakBits;

    private CableSignalMonitor(MMDevice device)
    {
        _device = device;
        _recorder = new WasapiRecorderBuilder().WithDevice(device)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(40).Build();
        _recorder.DataAvailable += (data, flags, _, _) =>
        {
            float peak = 0;
            if (!flags.HasFlag(AudioClientBufferFlags.Silent))
                foreach (float sample in MemoryMarshal.Cast<byte, float>(data)) peak = Math.Max(peak, Math.Abs(sample));
            Interlocked.Exchange(ref _lastPacket, Environment.TickCount64);
            int bits = BitConverter.SingleToInt32Bits(peak);
            int previous = Volatile.Read(ref _peakBits);
            while (bits > previous)
            {
                int observed = Interlocked.CompareExchange(ref _peakBits, bits, previous);
                if (observed == previous) break;
                previous = observed;
            }
        };
    }

    internal static async Task<CableSignalMonitor> OpenAsync(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(id);
        CableSignalMonitor? meter = null;
        try
        {
            meter = new(device);
            meter._recorder.StartRecording();
            return meter;
        }
        catch
        {
            if (meter != null) await meter.DisposeAsync(); else device.Dispose();
            throw;
        }
    }

    internal string ReadLevel()
    {
        var last = Interlocked.Read(ref _lastPacket);
        if (last == 0 || Environment.TickCount64 - last > 1500) return "CABLE Output · 等待录音数据";
        float peak = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));
        return peak <= 0.00001f ? "CABLE Output · 当前静音" : $"CABLE Output · 收到混音  {20 * Math.Log10(peak):0} dBFS";
    }

    public async ValueTask DisposeAsync()
    {
        try { await _recorder.DisposeAsync(); }
        finally { _device.Dispose(); }
    }
}
