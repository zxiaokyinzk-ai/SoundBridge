using System.Collections.Concurrent;
using NAudio.Wave;

namespace AudioRouter.Audio;

/// <summary>
/// A bounded, per-destination queue. Every destination owns a separate read cursor;
/// sharing one consuming provider between sound cards would split the audio stream.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _sync = new();
    private int _read, _write, _count;

    public AudioRingBuffer(int capacitySamples = 19_200)
    {
        if (capacitySamples < 2 || capacitySamples % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _buffer = new float[capacitySamples];
    }

    public int BufferedSamples { get { lock (_sync) return _count; } }
    public long DroppedSamples { get; private set; }

    public void Write(ReadOnlySpan<float> input)
    {
        if (input.Length % 2 != 0) throw new ArgumentException("Stereo data must contain whole frames.");
        lock (_sync)
        {
            if (input.Length > _buffer.Length)
            {
                DroppedSamples += input.Length - _buffer.Length;
                input = input[^_buffer.Length..];
            }
            var drop = Math.Max(0, _count + input.Length - _buffer.Length);
            _read = (_read + drop) % _buffer.Length;
            _count -= drop;
            DroppedSamples += drop;
            var first = Math.Min(input.Length, _buffer.Length - _write);
            input[..first].CopyTo(_buffer.AsSpan(_write));
            input[first..].CopyTo(_buffer);
            _write = (_write + input.Length) % _buffer.Length;
            _count += input.Length;
        }
    }

    public int Read(Span<float> output)
    {
        lock (_sync)
        {
            var take = Math.Min(_count, output.Length);
            take -= take % 2;
            var first = Math.Min(take, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(output);
            _buffer.AsSpan(0, take - first).CopyTo(output[first..]);
            _read = (_read + take) % _buffer.Length;
            _count -= take;
            output[take..].Clear();
            return take;
        }
    }

    public void Clear()
    {
        lock (_sync) { _read = _write = _count = 0; }
    }
}

internal sealed class SourceControls
{
    public float Gain = 1f;
    public bool Muted;
    public float Peak;
    public long LastPacketTicks;
}

internal sealed class MixBranch(SourceControls controls)
{
    internal float Gain = 1f;
    internal AudioRingBuffer Buffer { get; } = new();
    internal SourceControls Controls { get; } = controls;
}

internal sealed class RoutingMixer : ISampleProvider
{
    private readonly ConcurrentDictionary<string, MixBranch> _branches = new();
    private float[] _scratch = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    public float Gain = 1f;
    public bool Muted;
    public float Peak;

    internal MixBranch Add(string id, SourceControls controls) => _branches.GetOrAdd(id, _ => new(controls));
    internal void Remove(string id) => _branches.TryRemove(id, out _);
    internal void Clear() { foreach (var branch in _branches.Values) branch.Buffer.Clear(); }

    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        if (_scratch.Length < buffer.Length) _scratch = new float[buffer.Length];
        var scratch = _scratch.AsSpan(0, buffer.Length);
        foreach (var branch in _branches.Values)
        {
            branch.Buffer.Read(scratch);
            var gain = Volatile.Read(ref branch.Controls.Muted) ? 0f :
                Volatile.Read(ref branch.Controls.Gain) * Volatile.Read(ref branch.Gain);
            for (var i = 0; i < buffer.Length; i++) buffer[i] += scratch[i] * gain;
        }
        var master = Volatile.Read(ref Muted) ? 0f : Volatile.Read(ref Gain);
        float peak = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            // A safety clipper protects the output when multiple sources add up.
            var value = Math.Clamp(buffer[i] * master, -1f, 1f);
            buffer[i] = value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        Volatile.Write(ref Peak, peak);
        return buffer.Length;
    }
}
