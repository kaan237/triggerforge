using Tactile.Audio;

namespace Tactile.Tests;

public sealed class GameAudioTests
{
    [Fact]
    public void Averages_each_window_across_packets_then_scales_and_clamps()
    {
        var audio = new GameAudio { HapticStrength = 2 };
        short[] frames = Block(0, 0, short.MaxValue, short.MinValue);
        for (int i = 0; i < 16; i++)
        {
            frames[i * 4 + 2] = i < 8 ? (short)0 : (short)4096;
            frames[i * 4 + 3] = -2048;
        }

        var payload = new sbyte[64];
        audio.Push(frames.AsSpan(0, 11 * 4));
        Assert.False(audio.ReadHaptics(payload));
        audio.Push(frames.AsSpan(11 * 4));
        Assert.True(audio.ReadHaptics(payload));
        Assert.Equal((sbyte)16, payload[0]);
        Assert.Equal((sbyte)-16, payload[1]);
        Assert.Equal(sbyte.MaxValue, payload[2]);
        Assert.Equal(sbyte.MinValue, payload[3]);

        audio.HapticStrength = -1;
        audio.Push(frames);
        Assert.True(audio.ReadHaptics(payload));
        Assert.All(payload, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Queue_keeps_newest_three_blocks_and_live_expires_after_100_ms()
    {
        var time = new GameAudioClock();
        var audio = new GameAudio(time);
        var payload = new sbyte[64];
        audio.Push(Block(2000, -2000, 0, 0));
        Assert.False(audio.HapticsLive);
        for (short i = 1; i <= 4; i++)
        {
            audio.Push(Block(0, 0, (short)(i * 256), 0));
        }

        for (sbyte i = 2; i <= 4; i++)
        {
            Assert.True(audio.ReadHaptics(payload));
            Assert.Equal(i, payload[0]);
        }

        Assert.False(audio.ReadHaptics(payload));
        Assert.All(payload, sample => Assert.Equal(0, sample));
        time.Milliseconds = 99;
        audio.Push(Block(0, 0, 0, 0));
        Assert.True(audio.HapticsLive);
        time.Milliseconds = 100;
        Assert.False(audio.HapticsLive);
        audio.Push(Block(0, 0, 256, 0));
        Assert.True(audio.HapticsLive);
        audio.Clear();
        Assert.False(audio.HapticsLive);
        Assert.False(audio.ReadHaptics(payload));
    }

    [Fact]
    public void Speaker_uses_first_pair_and_smoothing_only_changes_haptics()
    {
        var dry = new GameAudio();
        var smooth = new GameAudio { LowPass = true };
        var stereo = new List<float>();
        smooth.Speaker += samples => stereo.AddRange(samples.ToArray());
        short[] frames = Block(short.MinValue, 16384, 8192, 0);
        dry.Push(frames);
        smooth.Push(frames);
        smooth.Push([1, 2, 3]);

        Assert.Equal(1024, stereo.Count);
        for (int i = 0; i < stereo.Count; i += 2)
        {
            Assert.Equal(-1f, stereo[i]);
            Assert.Equal(0.5f, stereo[i + 1]);
        }

        var payload = new sbyte[64];
        Assert.True(dry.ReadHaptics(payload));
        Assert.Equal(32, payload[0]);
        Assert.True(smooth.ReadHaptics(payload));
        Assert.InRange(payload[0], (sbyte)1, (sbyte)31);
        Assert.Equal(0, payload[1]);
        Assert.Equal(32, payload[^2]);
    }

    internal static short[] Block(short frontLeft, short frontRight, short rearLeft, short rearRight)
    {
        var frames = new short[512 * 4];
        for (int i = 0; i < frames.Length; i += 4)
        {
            frames[i] = frontLeft;
            frames[i + 1] = frontRight;
            frames[i + 2] = rearLeft;
            frames[i + 3] = rearRight;
        }

        return frames;
    }
}

internal sealed class GameAudioClock : TimeProvider
{
    public long Milliseconds { get; set; }
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Milliseconds;
}
