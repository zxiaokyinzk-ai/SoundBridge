using NAudio.Wave;

namespace AudioRouter.Audio;

/// <summary>Deterministic DSP checks; never opens, records, or changes a hardware device.</summary>
public static class AudioEngineSelfTest
{
    public static IReadOnlyList<string> Run()
    {
        List<string> results = [];
        var firstOutput = new RoutingMixer();
        var secondOutput = new RoutingMixer();
        var source = new SourceControls { Gain = 0.5f };
        var firstBranch = firstOutput.Add("music", source);
        var secondBranch = secondOutput.Add("music", source);
        float[] block = [0.8f, -0.8f, 0.4f, -0.4f];
        firstBranch.Buffer.Write(block);
        secondBranch.Buffer.Write(block);
        var firstRead = new float[4];
        var secondRead = new float[4];
        firstOutput.Read(firstRead);
        secondOutput.Read(secondRead);
        Assert(firstRead.SequenceEqual(secondRead), "One destination consumed another destination's audio.");
        Assert(Near(firstRead[0], 0.4f) && Near(firstRead[2], 0.2f), "Source gain is incorrect.");
        results.Add("PASS independent fan-out and source gain");

        firstBranch.Gain = 0.25f;
        firstBranch.Buffer.Write(block);
        secondBranch.Buffer.Write(block);
        firstOutput.Read(firstRead);
        secondOutput.Read(secondRead);
        Assert(Near(firstRead[0], 0.1f) && Near(secondRead[0], 0.4f), "Monitor gain changed another route.");
        firstBranch.Gain = 1f;
        results.Add("PASS independent monitor and microphone-send gains");

        firstBranch.Buffer.Write(block);
        source.Muted = true;
        firstOutput.Read(firstRead);
        Assert(firstRead.All(x => x == 0), "A muted source emitted audio.");
        source.Muted = false;
        firstOutput.Read(firstRead);
        Assert(firstRead.All(x => x == 0), "Muted samples remained queued for replay.");
        firstBranch.Buffer.Write(block);
        firstOutput.Gain = 0.5f;
        firstOutput.Read(firstRead);
        Assert(Near(firstRead[0], 0.2f), "Output gain is incorrect.");
        firstBranch.Buffer.Write(block);
        firstOutput.Muted = true;
        firstOutput.Read(firstRead);
        Assert(firstRead.All(x => x == 0), "Muted output emitted audio.");
        results.Add("PASS source/output mute, gain and silence padding");

        var ring = new AudioRingBuffer(8);
        ring.Write([1, 1, 2, 2, 3, 3]);
        var partial = new float[4];
        ring.Read(partial);
        ring.Write([4, 4, 5, 5, 6, 6, 7, 7]);
        var bounded = new float[8];
        ring.Read(bounded);
        Assert(bounded.SequenceEqual(new float[] { 4, 4, 5, 5, 6, 6, 7, 7 }), "Overflow did not drop the oldest complete stereo frames.");
        Assert(ring.DroppedSamples == 2 && ring.BufferedSamples == 0, "Queue accounting is wrong.");
        ring.Write([1, 1, 2, 2, 3, 3, 4, 4, 5, 5]);
        ring.Read(bounded);
        Assert(bounded.SequenceEqual(new float[] { 2, 2, 3, 3, 4, 4, 5, 5 }), "Oversized packet did not preserve newest data.");
        results.Add("PASS bounded queue, wrap-around and overflow recovery");

        var mixed = new RoutingMixer();
        mixed.Add("a", new()).Buffer.Write([0.8f, -0.8f]);
        mixed.Add("b", new()).Buffer.Write([0.8f, -0.8f]);
        var clipped = new float[2];
        mixed.Read(clipped);
        Assert(clipped[0] == 1 && clipped[1] == -1, "Mixed output exceeds safe sample limits.");
        mixed.Remove("a");
        mixed.Remove("b");
        mixed.Read(clipped);
        Assert(clipped.All(x => x == 0), "Removed routes still emit audio.");
        results.Add("PASS mixing, clipping and route removal");

        var converted = AudioEngine.ToStereo48K(new ConstantSource(24_000, 1, 2400, 0.25f));
        Assert(converted.WaveFormat.SampleRate == 48_000 && converted.WaveFormat.Channels == 2, "Mix format conversion failed.");
        var resampled = new float[960];
        var count = 0;
        var nonzero = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var read = converted.Read(resampled);
            if (read == 0) break;
            Assert(read % 2 == 0, "Resampler returned an incomplete stereo frame.");
            count += read;
            for (var i = 0; i < read; i += 2)
            {
                Assert(Near(resampled[i], resampled[i + 1]), "Mono-to-stereo conversion changed channel balance.");
                Assert(float.IsFinite(resampled[i]), "Resampler emitted non-finite samples.");
                if (Math.Abs(resampled[i]) > 0.1f) nonzero = true;
            }
        }
        Assert(count >= 9300 && count <= 9900 && nonzero, $"Unexpected resampled duration: {count} samples.");
        results.Add("PASS mono/stereo conversion and 24 kHz to 48 kHz resampling");
        return results;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0001;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class ConstantSource(int sampleRate, int channels, int frames, float value) : ISampleProvider
    {
        private int _remaining = frames * channels;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, _remaining);
            buffer[..count].Fill(value);
            _remaining -= count;
            return count;
        }
    }
}
