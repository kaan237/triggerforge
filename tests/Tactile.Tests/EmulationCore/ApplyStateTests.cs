using Tactile.Device.DualSense;

namespace Tactile.Tests.EmulationCore;

public class ApplyStateTests
{
    private static readonly OutputState Current = OutputState.Default with
    {
        Lightbar = new Rgb(1, 2, 3),
        Brightness = LedBrightness.Low,
        PlayerLeds = 0b00100,
        MicLed = MicLed.Pulse,
        RumbleLeft = 9,
        RumbleRight = 8,
        LeftTrigger = TriggerEffect.Feedback(2, 3),
        RightTrigger = TriggerEffect.Weapon(2, 6, 5),
    };

    /// <summary>A block with every field set to something different from <see cref="Current"/>, and the given flags.</summary>
    private static byte[] Block(byte a = 0, byte b = 0, byte c = 0)
    {
        byte[] block = new byte[OutputReport.StateLength];
        block[0] = a;
        block[1] = b;
        block[38] = c;
        block[2] = 0x40;   // right motor
        block[3] = 0x80;   // left motor
        block[8] = (byte)MicLed.On;
        TriggerEffect.Vibration(1, 5, 30).Bytes.CopyTo(block.AsSpan(10));
        TriggerEffect.Bow(1, 4, 6, 3).Bytes.CopyTo(block.AsSpan(21));
        block[42] = (byte)LedBrightness.Medium;
        block[43] = 0b11111 | 0x20;
        block[44] = 0xAA;
        block[45] = 0xBB;
        block[46] = 0xCC;
        return block;
    }

    [Fact]
    public void No_flags_change_nothing()
    {
        Assert.Equal(Current, OutputReport.ApplyState(Block(), Current));
        Assert.Equal(OutputFields.None, OutputReport.ValidFields(Block()));
    }

    [Fact]
    public void Lightbar_flag_changes_only_the_colour()
    {
        OutputState s = OutputReport.ApplyState(Block(b: 0x04), Current);
        Assert.Equal(Current with { Lightbar = new Rgb(0xAA, 0xBB, 0xCC) }, s);
    }

    [Fact]
    public void Trigger_flags_take_the_raw_effect_blocks_separately()
    {
        OutputState right = OutputReport.ApplyState(Block(a: 0x04), Current);
        Assert.Equal(Current with { RightTrigger = TriggerEffect.Vibration(1, 5, 30) }, right);

        OutputState left = OutputReport.ApplyState(Block(a: 0x08), Current);
        Assert.Equal(Current with { LeftTrigger = TriggerEffect.Bow(1, 4, 6, 3) }, left);
    }

    [Theory]
    [InlineData(0x01, 0x00)]
    [InlineData(0x02, 0x00)]
    [InlineData(0x00, 0x04)]
    public void Any_rumble_flag_sets_both_motors(byte a, byte c)
    {
        OutputState s = OutputReport.ApplyState(Block(a: a, c: c), Current);
        Assert.Equal(Current with { RumbleLeft = 0x80, RumbleRight = 0x40 }, s);
    }

    [Fact]
    public void Player_leds_mic_led_and_brightness_follow_their_flags()
    {
        Assert.Equal(Current with { PlayerLeds = 0b11111 }, OutputReport.ApplyState(Block(b: 0x10), Current));
        Assert.Equal(Current with { MicLed = MicLed.On }, OutputReport.ApplyState(Block(b: 0x01), Current));
        Assert.Equal(Current with { Brightness = LedBrightness.Medium }, OutputReport.ApplyState(Block(c: 0x01), Current));
    }

    [Fact]
    public void All_flags_together_and_round_trip_with_write_state()
    {
        OutputFields all = OutputReport.ValidFields(Block(a: 0x0F, b: 0x15, c: 0x05));
        Assert.Equal(OutputFields.Rumble | OutputFields.LeftTrigger | OutputFields.RightTrigger | OutputFields.Lightbar |
            OutputFields.PlayerLeds | OutputFields.MicLed | OutputFields.Brightness, all);

        // Our own report applied to the default reproduces the state (speaker volume is not a game field).
        byte[] own = new byte[OutputReport.StateLength];
        OutputReport.WriteState(Current, own);
        Assert.Equal(Current, OutputReport.ApplyState(own, OutputState.Default));
    }

    [Fact]
    public void Short_block_is_ignored_and_out_of_range_values_fall_back()
    {
        Assert.Equal(Current, OutputReport.ApplyState(new byte[10], Current));

        byte[] block = Block(b: 0x01, c: 0x01);
        block[8] = 7;
        block[42] = 9;
        OutputState s = OutputReport.ApplyState(block, Current);
        Assert.Equal(MicLed.Off, s.MicLed);
        Assert.Equal(LedBrightness.High, s.Brightness);
    }
}

public class FeatureReportTests
{
    [Fact]
    public void Usb_reports_are_cut_to_their_length()
    {
        byte[] raw = new byte[64];
        raw[0] = FeatureReport.Calibration;
        raw[1] = 0x11;
        raw[40] = 0x22;
        raw[50] = 0x33;
        byte[]? usb = FeatureReport.ToUsb(raw, Link.Usb);
        Assert.NotNull(usb);
        Assert.Equal(41, usb.Length);
        Assert.Equal(0x11, usb[1]);
        Assert.Equal(0x22, usb[40]);
    }

    [Theory]
    [InlineData(FeatureReport.Calibration, 41)]
    [InlineData(FeatureReport.Pairing, 20)]
    [InlineData(FeatureReport.Firmware, 64)]
    public void Bluetooth_reports_lose_their_crc(byte id, int length)
    {
        // Windows hands over the full 547-byte feature buffer; the CRC sits in the last four USB bytes.
        byte[] raw = new byte[547];
        raw[0] = id;
        for (int i = 1; i < length - 4; i++)
        {
            raw[i] = (byte)i;
        }

        Crc32.Stamp(Crc32.FeatureSeed, raw.AsSpan(0, length));
        byte[]? usb = FeatureReport.ToUsb(raw, Link.Bluetooth);

        Assert.NotNull(usb);
        Assert.Equal(length, usb.Length);
        Assert.Equal(raw[..(length - 4)], usb[..(length - 4)]);
        Assert.All(usb[(length - 4)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Bad_crc_short_buffer_and_unknown_id_give_null()
    {
        byte[] raw = new byte[64];
        raw[0] = FeatureReport.Firmware;
        Assert.Null(FeatureReport.ToUsb(raw, Link.Bluetooth));
        Assert.Null(FeatureReport.ToUsb(raw.AsSpan(0, 30), Link.Usb));
        raw[0] = 0x42;
        Assert.Null(FeatureReport.ToUsb(raw, Link.Usb));
        Assert.Null(FeatureReport.ToUsb([], Link.Usb));
    }
}
