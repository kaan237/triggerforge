using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public sealed class HapticMixerTests
{
    [Theory]
    [InlineData(true, 50)]
    [InlineData(false, 175)]
    public void Rumble_follows_motor_amplitude_on_the_matching_side_and_ramps(bool left, int frequency)
    {
        OutputState output = OutputState.Default;
        var mixer = new HapticMixer(() => output);
        int channel = left ? 0 : 1;
        int other = 1 - channel;
        Block(mixer);
        output = left ? output with { RumbleLeft = 64 } : output with { RumbleRight = 64 };
        Block(mixer);
        sbyte[] low = Block(mixer);
        Assert.InRange(Amplitude(low, channel), 28, 32);
        Assert.Equal(0, Amplitude(low, other));

        output = left ? output with { RumbleLeft = 255 } : output with { RumbleRight = 255 };
        sbyte[] rise = Block(mixer);
        sbyte[] high = Block(mixer);
        Assert.InRange(Amplitude(high, channel), 115, 127);
        Assert.Equal(0, Amplitude(high, other));
        Assert.InRange(Math.Abs((int)rise[channel]), 0, 39);

        output = OutputState.Default;
        sbyte[] fall = Block(mixer);
        int bound = (int)Math.Ceiling(254 * Math.Sin(Math.PI * frequency / HapticReport.SampleRate) + 127.0 / 30) + 1;
        int[] samples = low.Concat(rise).Concat(high).Concat(fall).Where((_, i) => i % 2 == channel).Select(s => (int)s).ToArray();
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, bound));
        Assert.Equal(0, Amplitude(Block(mixer), channel));
    }

    [Theory]
    [InlineData(Buttons.Up | Buttons.Left | Buttons.Down | Buttons.Right | Buttons.L1 | Buttons.L3 | Buttons.Create, true, false)]
    [InlineData(Buttons.Cross | Buttons.Square | Buttons.Triangle | Buttons.Circle | Buttons.R1 | Buttons.R3 | Buttons.Options, false, true)]
    [InlineData(Buttons.PS | Buttons.Touchpad, true, true)]
    [InlineData(Buttons.Mic | Buttons.L2 | Buttons.R2, false, false)]
    public void Button_clicks_follow_press_edges_and_controller_sides(Buttons buttons, bool left, bool right)
    {
        var mixer = new HapticMixer(() => OutputState.Default);
        mixer.ConfigureClicks(true, true);
        mixer.Observe(Inputs.Make(buttons));
        sbyte[] press = Block(mixer);
        Assert.Equal(left, Amplitude(press, 0) > 0);
        Assert.Equal(right, Amplitude(press, 1) > 0);

        mixer.Observe(Inputs.Make(buttons));
        sbyte[] tail = Block(mixer);
        Assert.All(tail.Skip(8), sample => Assert.Equal(0, sample));
        Assert.Equal(left || right, tail.Take(8).Any(sample => sample != 0));

        mixer.Observe(Inputs.Make(buttons));
        Assert.All(Block(mixer), sample => Assert.Equal(0, sample));
        mixer.Observe(Inputs.Make());
        Assert.All(Block(mixer), sample => Assert.Equal(0, sample));
        mixer.Observe(Inputs.Make(buttons));
        Assert.Equal(left || right, Block(mixer).Any(sample => sample != 0));
    }

    [Fact]
    public void Triggers_click_only_when_crossing_half_pressure_downwards()
    {
        var mixer = new HapticMixer(() => OutputState.Default);
        mixer.ConfigureClicks(true, true);
        mixer.Observe(Inputs.Make(l2: 127, r2: 127));
        Assert.All(Block(mixer), sample => Assert.Equal(0, sample));

        mixer.Observe(Inputs.Make(Buttons.L2, l2: 128, r2: 127));
        sbyte[] left = Block(mixer);
        Assert.True(Amplitude(left, 0) > 0);
        Assert.Equal(0, Amplitude(left, 1));
        Block(mixer);
        mixer.Observe(Inputs.Make(Buttons.L2, l2: 255, r2: 127));
        Assert.All(Block(mixer), sample => Assert.Equal(0, sample));

        mixer.Observe(Inputs.Make(Buttons.R2, l2: 127, r2: 128));
        sbyte[] right = Block(mixer);
        Assert.Equal(0, Amplitude(right, 0));
        Assert.True(Amplitude(right, 1) > 0);
    }

    [Fact]
    public void Simultaneous_clicks_add_and_final_output_clamps_on_both_signs()
    {
        var mixer = new HapticMixer(() => OutputState.Default);
        mixer.ConfigureClicks(true, true);
        mixer.Observe(Inputs.Make(Buttons.Up));
        int single = Amplitude(Block(mixer, clickStrength: 0.2f), 0);
        Block(mixer);
        mixer.Observe(Inputs.Make());
        mixer.Observe(Inputs.Make(Buttons.Up | Buttons.L1));
        int doubleClick = Amplitude(Block(mixer, clickStrength: 0.2f), 0);
        Assert.InRange(doubleClick, 2 * single - 1, 2 * single + 1);

        mixer.Observe(Inputs.Make(Buttons.PS | Buttons.Touchpad));
        sbyte[] clipped = Block(mixer);
        Assert.Contains((sbyte)127, clipped);
        Assert.Contains((sbyte)-127, clipped);
        Assert.DoesNotContain(sbyte.MinValue, clipped);
    }

    [Fact]
    public void Test_tone_fills_reused_buffer_and_silences_it_after_burst()
    {
        HapticBlockSource tone = HapticStreamer.Tone(60, 1, left: false, blocks: 1);
        var samples = new sbyte[HapticReport.PayloadLength];
        tone(0, samples);
        Assert.Equal(0, Amplitude(samples, 0));
        Assert.True(Amplitude(samples, 1) > 0);

        tone(1, samples);
        Assert.All(samples, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Confirmation_is_a_stereo_double_tap_independent_of_clicks()
    {
        var mixer = new HapticMixer(() => OutputState.Default);
        mixer.ConfigureClicks(connected: true, enabled: false);
        mixer.PulseConfirm();

        var frames = Enumerable.Range(0, 5).SelectMany(_ => Block(mixer, clickStrength: 0)).ToArray();
        Assert.Contains(frames[..72], sample => sample != 0);
        Assert.All(frames[72..216], sample => Assert.Equal(0, sample));
        Assert.Contains(frames[216..288], sample => sample != 0);
        Assert.All(frames[288..], sample => Assert.Equal(0, sample));
        Assert.All(frames.Where((_, i) => i % 2 == 0).Zip(frames.Where((_, i) => i % 2 != 0)),
            pair => Assert.Equal(pair.First, pair.Second));
    }

    private static int Amplitude(sbyte[] samples, int channel) =>
        samples.Where((_, i) => i % 2 == channel).Max(s => Math.Abs((int)s));

    private static sbyte[] Block(HapticMixer mixer, float clickStrength = 1)
    {
        var result = new sbyte[HapticReport.PayloadLength];
        mixer.Mix(result, 1, clickStrength);
        return result;
    }
}
