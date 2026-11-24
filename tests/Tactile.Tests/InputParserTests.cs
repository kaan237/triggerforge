using Tactile.Device.DualSense;

namespace Tactile.Tests;

/// <summary>Real Bluetooth reports captured from a DualSense (Fixtures/bt-sample.bin).</summary>
public class InputParserTests
{
    private static readonly List<CaptureRecord> Records = Load();

    private static List<CaptureRecord> Load()
    {
        using FileStream f = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bt-sample.bin"));
        return CaptureFile.Read(f).Records;
    }

    private static byte[] Input(int index) => Records.Where(r => r.Kind == CaptureKind.Input).ElementAt(index).Data;

    private static InputState Parse(int index)
    {
        Assert.Equal(ParseResult.Ok, InputParser.TryParse(Input(index), Link.Bluetooth, out InputState? s));
        return s!;
    }

    [Fact]
    public void Idle_report_has_no_buttons_and_centred_sticks()
    {
        InputState s = Parse(0);
        Assert.Equal(Buttons.None, s.Buttons);
        Assert.InRange(s.Left.X, 120, 136);
        Assert.Equal(0, s.L2);
        Assert.False(s.Touch2.Active);
        Assert.Equal(95, s.Battery.Percent);
        Assert.Equal(ChargeState.Discharging, s.Battery.Charge);
    }

    [Fact]
    public void Face_button_and_hat()
    {
        Assert.Equal(Buttons.Cross, Parse(1).Buttons);
        Assert.Equal(Buttons.Right, Parse(2).Buttons);
    }

    [Fact]
    public void Shoulders_and_analog_trigger()
    {
        InputState l2 = Parse(3);
        Assert.Equal(255, l2.L2);
        Assert.True(l2.IsDown(Buttons.L2));
        Assert.Equal(Buttons.R1, Parse(4).Buttons);
    }

    [Fact]
    public void Touchpad_click_and_two_fingers()
    {
        Assert.Equal(Buttons.Touchpad, Parse(5).Buttons);

        InputState t = Parse(6);
        Assert.Equal(new TouchPoint(true, 0x12, 474, 198), t.Touch1);
        Assert.Equal(new TouchPoint(true, 0x13, 106, 776), t.Touch2);
    }

    [Fact]
    public void Stick_extreme()
    {
        Assert.Equal(0, Parse(7).Left.X);
        Assert.Equal(-1f, Parse(7).Left.Normalized.X);
    }

    [Fact]
    public void Corrupted_report_is_rejected()
    {
        Assert.Equal(ParseResult.BadCrc, InputParser.TryParse(Input(8), Link.Bluetooth, out _));
    }

    [Fact]
    public void Reduced_report_is_flagged()
    {
        byte[] shortReport = new byte[78];
        shortReport[0] = 0x01;
        Assert.Equal(ParseResult.ReducedReport, InputParser.TryParse(shortReport, Link.Bluetooth, out _));
    }

    [Fact]
    public void Usb_layout_uses_offset_one()
    {
        byte[] bt = Input(1);
        byte[] usb = new byte[64];
        usb[0] = 0x01;
        bt.AsSpan(2, InputParser.StateLength).CopyTo(usb.AsSpan(1));
        Assert.Equal(ParseResult.Ok, InputParser.TryParse(usb, Link.Usb, out InputState? s));
        Assert.Equal(Buttons.Cross, s!.Buttons);
    }

    [Fact]
    public void Calibration_gives_one_g_at_rest()
    {
        byte[] feature = Records.First(r => r.Kind == CaptureKind.Feature && r.Data[0] == 0x05).Data;
        ImuCalibration cal = ImuCalibration.Parse(feature);
        Vector3 g = cal.AccelG(Parse(0).AccelRaw);
        float magnitude = MathF.Sqrt(g.X * g.X + g.Y * g.Y + g.Z * g.Z);
        Assert.InRange(magnitude, 0.85f, 1.15f);
    }

    [Fact]
    public void Firmware_info_parses_build_date()
    {
        byte[] feature = Records.First(r => r.Kind == CaptureKind.Feature && r.Data[0] == 0x20).Data;
        FirmwareInfo? fw = FirmwareInfo.Parse(feature);
        Assert.NotNull(fw);
        Assert.Equal("Jul  4 2025", fw.BuildDate);
        Assert.Equal("10:10:32", fw.BuildTime);
    }

    [Fact]
    public void Captured_state_reports_never_carry_the_mic_flag()
    {
        foreach (CaptureRecord r in Records.Where(r => r.Kind == CaptureKind.Input && r.Data.Length >= 78 && r.Data[0] == 0x31))
        {
            Assert.Equal(0, r.Data[1] & 0x02);
        }
    }

    [Fact]
    public void Bluetooth_mic_report_is_classified_before_crc_and_never_parsed_as_state()
    {
        foreach (byte flags in new byte[] { 0x02, 0x32 })
        {
            byte[] mic = new byte[78];
            mic[0] = 0x31;
            mic[1] = flags;
            mic[2] = 0x5A;
            for (int i = 0; i < 71; i++)
            {
                mic[3 + i] = (byte)(i + 1);
            }

            mic[74] = mic[75] = mic[76] = mic[77] = 0xEE;
            Assert.Equal(ParseResult.MicAudio, InputParser.TryParse(mic, Link.Bluetooth, out InputState? state, micStream: true));
            Assert.Null(state);
            Assert.True(InputParser.TryParseMic(mic, out byte seq, out ReadOnlySpan<byte> opus));
            Assert.Equal(0x5A, seq);
            Assert.True(mic.AsSpan(3, 71).SequenceEqual(opus));
        }

        byte[] normal = Input(0).ToArray();
        Assert.False(InputParser.TryParseMic(normal, out _, out _));
        Assert.Equal(ParseResult.Ok, InputParser.TryParse(normal, Link.Bluetooth, out _));
        normal[20] ^= 0xFF;
        Assert.Equal(ParseResult.BadCrc, InputParser.TryParse(normal, Link.Bluetooth, out _));
    }

    [Fact]
    public void Mic_bit_is_ignored_while_the_mic_stream_is_off_and_the_crc_path_is_unchanged()
    {
        byte[] mic = new byte[78];
        mic[0] = 0x31;
        mic[1] = 0x02;
        mic[2] = 0x5A;
        Assert.Equal(ParseResult.BadCrc, InputParser.TryParse(mic, Link.Bluetooth, out InputState? state));
        Assert.Null(state);

        // A valid state report that happens to carry the bit goes through the CRC as before.
        byte[] flagged = Input(0).ToArray();
        flagged[1] |= 0x02;
        Crc32.Stamp(Crc32.InputSeed, flagged);
        Assert.Equal(ParseResult.Ok, InputParser.TryParse(flagged, Link.Bluetooth, out state));
        Assert.NotNull(state);
        Assert.Equal(ParseResult.MicAudio, InputParser.TryParse(flagged, Link.Bluetooth, out _, micStream: true));
    }
}
