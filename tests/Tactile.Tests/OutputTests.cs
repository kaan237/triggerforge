using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class OutputTests
{
    [Fact]
    public void Bluetooth_report_has_header_state_and_valid_crc()
    {
        var state = OutputState.Default with { Lightbar = new Rgb(1, 2, 3), PlayerLeds = 0b00100, RumbleLeft = 200, RumbleRight = 50 };
        byte[] r = OutputReport.BuildBluetooth(state, sequence: 3);

        Assert.Equal(78, r.Length);
        Assert.Equal(0x31, r[0]);
        Assert.Equal(0x30, r[1]);
        Assert.Equal(0x10, r[2]);
        Assert.True(Crc32.Verify(Crc32.OutputSeed, r));

        byte[] s = r.AsSpan(3, OutputReport.StateLength).ToArray();
        Assert.Equal(50, s[2]);
        Assert.Equal(200, s[3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, s[44..47]);
        Assert.Equal(0b00100, s[43]);
        Assert.Equal(0x05, s[10]);
        Assert.Equal(0x05, s[21]);
    }

    [Fact]
    public void Usb_report_shares_state_block()
    {
        var state = OutputState.Default with { RightTrigger = TriggerEffect.Weapon(2, 5, 8) };
        byte[] usb = OutputReport.BuildUsb(state);
        byte[] bt = OutputReport.BuildBluetooth(state, 0);
        Assert.Equal(63, usb.Length);
        Assert.Equal(0x02, usb[0]);
        Assert.Equal(bt.AsSpan(3, 47).ToArray(), usb.AsSpan(1, 47).ToArray());
    }

    [Fact]
    public void Rumble_flags_follow_options()
    {
        var state = OutputState.Default with { RumbleLeft = 100 };
        byte[] v2 = new byte[47];
        OutputReport.WriteState(state, v2, new OutputOptions(RumbleV2: true));
        Assert.Equal(0x02, v2[0] & 0x03);
        Assert.Equal(0x04, v2[38] & 0x04);

        byte[] v1 = new byte[47];
        OutputReport.WriteState(state, v1, new OutputOptions(RumbleV2: false));
        Assert.Equal(0x03, v1[0] & 0x03);

        byte[] off = new byte[47];
        OutputReport.WriteState(state, off, new OutputOptions(Rumble: false));
        Assert.Equal(0, off[0] & 0x03);
        Assert.Equal(0, off[3]);
    }

    [Fact]
    public void Default_options_enable_rumble()
    {
        byte[] s = new byte[47];
        OutputReport.WriteState(OutputState.Default with { RumbleLeft = 9 }, s);
        Assert.Equal(9, s[3]);
    }

    [Fact]
    public void Feedback_packs_zone_mask_and_levels()
    {
        // Zones 3-9 at strength 5: mask 0x3F8, stored level 4 per zone.
        byte[] b = TriggerEffect.Feedback(3, 5).Bytes.ToArray();
        Assert.Equal(0x21, b[0]);
        Assert.Equal(0xF8, b[1]);
        Assert.Equal(0x03, b[2]);
        uint packed = (uint)(b[3] | b[4] << 8 | b[5] << 16 | b[6] << 24);
        for (int z = 0; z < 10; z++)
        {
            Assert.Equal(z >= 3 ? 4u : 0u, (packed >> (3 * z)) & 7);
        }
    }

    [Fact]
    public void Weapon_sets_start_end_bits()
    {
        byte[] b = TriggerEffect.Weapon(2, 6, 8).Bytes.ToArray();
        Assert.Equal(new byte[] { 0x25, 0x44, 0x00, 0x07, 0, 0, 0, 0, 0, 0, 0 }, b);
    }

    [Fact]
    public void Vibration_puts_frequency_in_byte_nine()
    {
        byte[] b = TriggerEffect.Vibration(0, 8, 40).Bytes.ToArray();
        Assert.Equal(0x26, b[0]);
        Assert.Equal(0xFF, b[1]);
        Assert.Equal(0x03, b[2]);
        Assert.Equal(40, b[9]);
    }

    [Fact]
    public void Zero_strength_is_off()
    {
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Feedback(0, 0));
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Vibration(0, 5, 0));
        Assert.Equal(TriggerEffect.Off, default(TriggerEffect));
    }

    [Fact]
    public void Slope_ramps_between_strengths()
    {
        byte[] b = TriggerEffect.Slope(0, 9, 1, 8).Bytes.ToArray();
        uint packed = (uint)(b[3] | b[4] << 8 | b[5] << 16 | b[6] << 24);
        Assert.Equal(0u, packed & 7);
        Assert.Equal(7u, (packed >> 27) & 7);
    }

    [Fact]
    public void Out_of_range_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffect.Weapon(1, 5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffect.Feedback(0, 9));
    }

    [Fact]
    public void Haptic_report_layout()
    {
        var builder = new HapticReport();
        var samples = new sbyte[64];
        samples[0] = -1;
        samples[63] = 100;
        byte[] r = builder.Build(samples).ToArray();

        Assert.Equal(142, r.Length);
        Assert.Equal(0x32, r[0]);
        Assert.Equal(0x91, r[2]);
        Assert.Equal(0x92, r[8]);
        Assert.Equal(64, r[9]);
        Assert.Equal(0xFF, r[10]);
        Assert.Equal(100, r[73]);
        Assert.True(Crc32.Verify(Crc32.OutputSeed, r));

        byte[] next = builder.Build(samples);
        Assert.Equal(0x10, next[1]);
        Assert.Equal(1, next[7]);
    }

    [Fact]
    public void Downsample_averages_windows()
    {
        var input = new float[1024];
        for (int i = 0; i < 1024; i += 2)
        {
            input[i] = 1f;
            input[i + 1] = -0.5f;
        }

        var payload = new sbyte[64];
        HapticReport.Downsample(input, payload);
        Assert.All(Enumerable.Range(0, 32), f => Assert.Equal(127, payload[f * 2]));
        Assert.All(Enumerable.Range(0, 32), f => Assert.Equal(-64, payload[f * 2 + 1]));
    }
}
