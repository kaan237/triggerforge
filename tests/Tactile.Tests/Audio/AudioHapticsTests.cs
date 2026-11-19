using Tactile.Audio;

namespace Tactile.Tests.Audio;

public sealed class AudioHapticsTests
{
    [Fact]
    public void SilenceProducesSilence()
    {
        var dsp = new AudioHaptics(HapticMode.Game);
        sbyte[] payload = new sbyte[64];
        Array.Fill(payload, (sbyte)127);

        dsp.Push(new float[1024]);

        Assert.True(dsp.Read(payload));
        Assert.All(payload, sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData(HapticMode.Game, 0)]
    [InlineData(HapticMode.Film, 1)]
    [InlineData(HapticMode.Music, 0)]
    public void BassRemainsOnItsOwnChannel(HapticMode mode, int channel)
    {
        var dsp = new AudioHaptics(mode);
        sbyte[] payload = new sbyte[64];
        float[] sine = Sine(60, 1, channel, blocks: 12);

        Process(dsp, sine, payload);

        Assert.Contains(payload, sample => Math.Abs((int)sample) >= 35);
        for (int i = 1 - channel; i < payload.Length; i += 2)
            Assert.Equal(0, payload[i]);
    }

    [Theory]
    [InlineData(HapticMode.Film)]
    [InlineData(HapticMode.Music)]
    public void HighToneIsRejected(HapticMode mode)
    {
        var dsp = new AudioHaptics(mode);
        sbyte[] payload = new sbyte[64];

        Process(dsp, Sine(2000, 1, 0, blocks: 12), payload);

        Assert.All(payload, sample => Assert.InRange(sample, -1, 1));
    }

    [Theory]
    [InlineData(HapticMode.Game)]
    [InlineData(HapticMode.Film)]
    [InlineData(HapticMode.Music)]
    public void GateKeepsQuietBassSilent(HapticMode mode)
    {
        var dsp = new AudioHaptics(mode) { Gain = 2 };
        sbyte[] payload = new sbyte[64];

        // At gain 2 this would round to a nonzero value without the gate.
        Process(dsp, Sine(60, 0.003f, 0, blocks: 12), payload);

        Assert.All(payload, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void ZeroGainProducesSilenceAndGainClamps()
    {
        var dsp = new AudioHaptics(HapticMode.Game) { Gain = 3 };
        Assert.Equal(2f, dsp.Gain);
        dsp.Gain = -1;
        Assert.Equal(0f, dsp.Gain);
        sbyte[] payload = new sbyte[64];

        Process(dsp, Sine(60, 1, 0, blocks: 4), payload);

        Assert.All(payload, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void QueueKeepsTheNewestFourBlocksInOrder()
    {
        var dsp = new AudioHaptics(HapticMode.Game);
        var reference = new AudioHaptics(HapticMode.Game);
        sbyte[] actual = new sbyte[64];
        sbyte[] expected = new sbyte[64];
        float[] samples = Sine(60, 1, 0, blocks: 7);
        for (int block = 0; block < 7; block++)
            samples[block * 1024 + 1] = block / 7f;

        dsp.Push(samples);
        reference.Push(samples.AsSpan(3 * 1024));

        Assert.Equal(4, dsp.BufferedBlocks);
        for (int remaining = 3; remaining >= 0; remaining--)
        {
            Assert.True(reference.Read(expected));
            Assert.True(dsp.Read(actual));
            Assert.Equal(expected, actual);
            Assert.Equal(remaining, dsp.BufferedBlocks);
        }
    }

    [Fact]
    public void ReadWithoutAWholeBlockReturnsFalseAndClearsPayload()
    {
        var dsp = new AudioHaptics(HapticMode.Game);
        sbyte[] payload = new sbyte[64];
        Array.Fill(payload, (sbyte)127);
        dsp.Push(new float[1023]);

        Assert.False(dsp.Read(payload));
        Assert.All(payload, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void OddPushLengthsPreserveSampleOrderAndChannelAlignment()
    {
        var dsp = new AudioHaptics(HapticMode.Game);
        var reference = new AudioHaptics(HapticMode.Game);
        float[] sine = Sine(60, 1, 1, blocks: 2);
        sbyte[] actual = new sbyte[64];
        sbyte[] expected = new sbyte[64];

        dsp.Push(sine.AsSpan(0, 13));
        dsp.Push(sine.AsSpan(13, 1023));
        Assert.Equal(1, dsp.BufferedBlocks);
        dsp.Push(sine.AsSpan(1036));
        reference.Push(sine);

        Assert.Equal(2, dsp.BufferedBlocks);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(reference.Read(expected));
            Assert.True(dsp.Read(actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ModeChangeAppliesToQueuedBlockAndRampsIn()
    {
        var dsp = new AudioHaptics(HapticMode.Game);
        var reference = new AudioHaptics(HapticMode.Film);
        float[] sine = Sine(60, 1, 0, blocks: 2);
        sbyte[] actual = new sbyte[64];
        sbyte[] expected = new sbyte[64];
        dsp.Push(sine);
        Assert.True(dsp.Read(actual));

        dsp.Mode = HapticMode.Film;
        reference.Push(sine.AsSpan(1024));

        Assert.True(dsp.Read(actual));
        Assert.True(reference.Read(expected));
        Assert.Equal(expected, actual);
        Assert.Equal(0, actual[0]);
        Assert.Equal(0, actual[1]);
    }

    [Fact]
    public void ClearDiscardsPartialAndWholeBlocksAndFilterHistory()
    {
        var dsp = new AudioHaptics(HapticMode.Game);
        sbyte[] payload = new sbyte[64];
        dsp.Push(Sine(60, 1, 0, blocks: 2));
        Assert.True(dsp.Read(payload));
        dsp.Push(new float[1023]);

        dsp.Clear();
        dsp.Push(new float[1]);

        Assert.False(dsp.Read(payload));
        dsp.Push(new float[1023]);
        Assert.True(dsp.Read(payload));
        Assert.All(payload, sample => Assert.Equal(0, sample));
    }

    private static float[] Sine(int frequency, float amplitude, int channel, int blocks)
    {
        float[] samples = new float[blocks * 1024];
        for (int frame = 0; frame < samples.Length / 2; frame++)
            samples[frame * 2 + channel] = amplitude * MathF.Sin(2 * MathF.PI * frequency * frame / 48_000);
        return samples;
    }

    private static void Process(AudioHaptics dsp, float[] sine, sbyte[] payload)
    {
        for (int start = 0; start < sine.Length; start += 1024)
        {
            dsp.Push(sine.AsSpan(start, 1024));
            Assert.True(dsp.Read(payload));
        }
    }
}
