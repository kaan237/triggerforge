using Tactile.Audio;

namespace Tactile.Tests;

public sealed class OpusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opus_encodes_a_single_10_ms_fullband_frame(bool tone)
    {
        using var encoder = new OpusEncoder();
        var pcm = new float[OpusEncoder.FrameSamples * OpusEncoder.Channels];
        if (tone)
        {
            for (int i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = pcm[i + 1] = MathF.Sin(2f * MathF.PI * 1000 * (i / 2) / OpusEncoder.SampleRate);
            }
        }

        var packet = new byte[OpusEncoder.FrameBytes + 1];
        packet[^1] = 0xA5;
        encoder.Encode(pcm, packet.AsSpan(0, 200));
        Assert.Equal(30, packet[0] >> 3); // CELT fullband, 10 ms.
        if ((packet[0] & 3) == 3)
        {
            Assert.Equal(1, packet[1] & 0x3F); // A padded packet carries one frame.
        }
        else
        {
            Assert.Equal(0, packet[0] & 3);
        }

        Assert.Equal(0xA5, packet[^1]);
        Assert.Throws<ArgumentException>(() => encoder.Encode(pcm, new byte[199]));
    }

    [Fact]
    public void Speaker_resamples_512_frames_and_mixes_both_channels_into_mono()
    {
        var input = new float[1024];
        for (int i = 0; i < 512; i++)
        {
            input[i * 2] = i / 512f;
            input[i * 2 + 1] = -0.25f;
        }

        var output = new float[960];
        SpeakerFeed.Resample(input, output, gain: 0.75f);
        for (int i = 0; i < 480; i++)
        {
            float expected = (i / 480f - 0.25f) * 0.5f * 0.75f;
            Assert.InRange(output[i * 2], expected - 0.000001f, expected + 0.000001f);
            Assert.Equal(output[i * 2], output[i * 2 + 1]);
        }
    }

    [Fact]
    public void Feed_keeps_four_latest_blocks_joins_partial_input_and_preserves_output_on_underrun()
    {
        using var feed = new SpeakerFeed { Gain = 2 };
        using var reference = new SpeakerFeed();
        var frame = new byte[200];
        var expected = new byte[200];
        var samples = new float[1024];
        for (int block = 0; block < 6; block++)
        {
            Array.Fill(samples, block / 6f);
            feed.Push(samples.AsSpan(0, 5));
            if (block == 0)
            {
                Array.Fill(frame, (byte)0xA5);
                Assert.False(feed.Read(frame));
                Assert.All(frame, value => Assert.Equal(0xA5, value));
            }

            feed.Push(samples.AsSpan(5));
            if (block >= 2)
            {
                reference.Push(samples);
            }
        }

        Assert.Equal(1f, feed.Gain);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(reference.Read(expected));
            Assert.True(feed.Read(frame));
            Assert.Equal(expected, frame);
        }

        Assert.False(feed.Read(frame));
        Assert.Equal(expected, frame);
        feed.Push(samples);
        feed.Push(samples.AsSpan(0, 5));
        feed.Clear();
        feed.Push(samples.AsSpan(5));
        Assert.False(feed.Read(frame));
    }

    [Fact]
    public void Feed_waits_for_two_blocks_before_playing_and_after_an_underrun()
    {
        using var feed = new SpeakerFeed();
        var frame = new byte[200];
        var block = new float[1024];
        Array.Fill(block, 0.25f);

        feed.Push(block);
        Assert.False(feed.Read(frame));
        feed.Push(block);
        Assert.True(feed.Read(frame));
        Assert.True(feed.Read(frame));
        Assert.False(feed.Read(frame));

        feed.Push(block);
        Assert.False(feed.Read(frame));
        feed.Push(block);
        Assert.True(feed.Read(frame));
    }
}
