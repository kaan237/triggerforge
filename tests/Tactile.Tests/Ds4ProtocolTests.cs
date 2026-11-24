using System.Buffers.Binary;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Tests;

public class Ds4ProtocolTests
{
    private static byte[] UsbReport()
    {
        byte[] r = new byte[64];
        r[0] = 0x01;
        r[1] = 10; r[2] = 20; r[3] = 30; r[4] = 40;
        r[5] = 0x20 | 0x80 | 0x02;           // cross + triangle + hat right
        r[6] = 0x10 | 0x01 | 0x20;           // share + L1 + options
        r[7] = 0x03 | (5 << 2);              // PS + touchpad + counter
        r[8] = 11; r[9] = 22;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(10), 0x1234);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(13), -5);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(15), 6);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(17), 7);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(19), 100);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(21), -200);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(23), 8192);
        r[30] = 0x07 | 0x20 | 0x40;          // battery 7, no cable, headphone + mic
        r[33] = 2;
        // packet 0 (older), packet 1 (newest)
        WriteTouch(r.AsSpan(34 + 1), active: true, id: 1, x: 10, y: 20);
        WriteTouch(r.AsSpan(34 + 9 + 1), active: true, id: 2, x: 1900, y: 940);
        WriteTouch(r.AsSpan(34 + 9 + 5), active: false, id: 3, x: 0, y: 0);
        return r;
    }

    private static void WriteTouch(Span<byte> t, bool active, int id, int x, int y)
    {
        t[0] = (byte)((active ? 0 : 0x80) | id);
        t[1] = (byte)x;
        t[2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        t[3] = (byte)(y >> 4);
    }

    private static byte[] BtReport(byte[] usb)
    {
        byte[] r = new byte[78];
        r[0] = 0x11;
        r[1] = 0xC0;
        usb.AsSpan(1, 63).CopyTo(r.AsSpan(3, 63));
        // USB offset k lives at k + 2; byte 0 of the state (the id) is r[2].
        r[2] = usb[0];
        Crc32.Stamp(Crc32.InputSeed, r);
        return r;
    }

    private static void Check(InputState s)
    {
        Assert.Equal(new Stick(10, 20), s.Left);
        Assert.Equal(new Stick(30, 40), s.Right);
        Assert.Equal(Buttons.Cross | Buttons.Triangle | Buttons.Right | Buttons.Create | Buttons.L1 | Buttons.Options
            | Buttons.PS | Buttons.Touchpad, s.Buttons);
        Assert.Equal((byte)11, s.L2);
        Assert.Equal((byte)22, s.R2);
        Assert.Equal(new Vector3(-5, 6, 7), s.GyroRaw);
        Assert.Equal(new Vector3(100, -200, 8192), s.AccelRaw);
        Assert.Equal(new Battery(75, ChargeState.Discharging), s.Battery);
        Assert.True(s.HeadphoneConnected);
        Assert.True(s.MicConnected);
        Assert.False(s.MicMuted);
        Assert.Equal(new TouchPoint(true, 2, 1900, 940), s.Touch1);
        Assert.False(s.Touch2.Active);
    }

    [Fact]
    public void Parses_usb_and_bt_ds4_input_reports_and_verifies_crc()
    {
        byte[] usb = UsbReport();
        var clock = default(Ds4Timestamp);
        Assert.Equal(ParseResult.Ok, Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out InputState? u));
        Check(u!);

        byte[] bt = BtReport(usb);
        var clock2 = default(Ds4Timestamp);
        Assert.Equal(ParseResult.Ok, Ds4InputParser.TryParse(bt, Link.Bluetooth, ref clock2, out InputState? b));
        Check(b!);
        Assert.Equal(u!.SensorTimestamp, b!.SensorTimestamp);

        Assert.Equal(ParseResult.ReducedReport, Ds4InputParser.TryParse(new byte[10] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Link.Bluetooth, ref clock2, out _));

        byte[] bad = (byte[])bt.Clone();
        bad[10] ^= 0xFF;
        Assert.Equal(ParseResult.BadCrc, Ds4InputParser.TryParse(bad, Link.Bluetooth, ref clock2, out InputState? none));
        Assert.Null(none);

        Assert.Equal(ParseResult.Unknown, Ds4InputParser.TryParse(new byte[64], Link.Usb, ref clock2, out _));
    }

    [Fact]
    public void Touch_count_is_bounded_and_empty_when_zero()
    {
        byte[] usb = UsbReport();
        usb[33] = 0;
        var clock = default(Ds4Timestamp);
        Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out InputState? s);
        Assert.False(s!.Touch1.Active);
        Assert.False(s.Touch2.Active);

        usb[33] = 200;
        Assert.Equal(ParseResult.Ok, Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out _));
    }

    [Fact]
    public void Dongle_unpaired_reports_are_dropped()
    {
        byte[] usb = UsbReport();
        usb[31] |= 0x04;
        var clock = default(Ds4Timestamp);
        Assert.Equal(ParseResult.Unknown, Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out InputState? s, dongle: true));
        Assert.Null(s);
        Assert.Equal(ParseResult.Ok, Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out _));
    }

    [Fact]
    public void Battery_follows_cable_and_level()
    {
        var clock = default(Ds4Timestamp);
        Battery Read(byte status)
        {
            byte[] usb = UsbReport();
            usb[30] = status;
            Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out InputState? s);
            return s!.Battery;
        }

        Assert.Equal(new Battery(45, ChargeState.Charging), Read(0x14));
        Assert.Equal(new Battery(100, ChargeState.Charging), Read(0x1A));
        Assert.Equal(new Battery(100, ChargeState.Full), Read(0x1B));
        Assert.Equal(new Battery(0, ChargeState.Error), Read(0x1C));
        Assert.Equal(new Battery(0, ChargeState.Error), Read(0x1D));
        Assert.Equal(new Battery(0, ChargeState.Error), Read(0x1F));
        Assert.Equal(new Battery(100, ChargeState.Discharging), Read(0x0B));
    }

    [Fact]
    public void Unwraps_16bit_sensor_timestamp_across_wrap_for_dsu()
    {
        var clock = default(Ds4Timestamp);
        uint first = clock.Next(0xFFF0);
        uint second = clock.Next(0x0010);
        Assert.Equal(512u, second - first);
        Assert.Equal(512u + 0x20 * 16, clock.Next(0x0030) - first);
    }

    private static byte[] Feature(byte id, int length, bool grouped, bool crc)
    {
        byte[] f = new byte[length];
        f[0] = id;
        void W(int offset, short v) => BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(offset), v);
        W(1, 0); W(3, 0); W(5, 0);
        // pitch range 8000, yaw 9000, roll 10000 (each +/-); speed plus + minus = 1080
        short[] plus = [8000, 9000, 10000];
        short[] minus = [-8000, -9000, -10000];
        for (int i = 0; i < 3; i++)
        {
            if (grouped)
            {
                W(7 + i * 2, plus[i]);
                W(13 + i * 2, minus[i]);
            }
            else
            {
                W(7 + i * 4, plus[i]);
                W(9 + i * 4, minus[i]);
            }
        }

        W(19, 540); W(21, 540);
        W(23, 8192); W(25, -8192); W(27, 8192); W(29, -8192); W(31, 8192); W(33, -8192);
        if (crc)
        {
            Crc32.Stamp(Crc32.FeatureSeed, f);
        }

        return f;
    }

    [Fact]
    public void Parses_ds4_calibration_gyro_pair_order_and_rejects_bad_bt_crc()
    {
        var raw = new Vector3(8000, 9000, 10000);
        foreach ((byte id, int len, bool grouped, bool crc) in new[] { ((byte)0x02, 37, false, false), ((byte)0x05, 41, true, true), ((byte)0x02, 37, true, false) })
        {
            ImuCalibration cal = ImuCalibration.ParseDualShock4(Feature(id, len, grouped, crc), grouped);
            Vector3 deg = cal.GyroDegPerSec(raw);
            Assert.Equal(540f, deg.X, 0.5f);
            Assert.Equal(540f, deg.Y, 0.5f);
            Assert.Equal(540f, deg.Z, 0.5f);
            Assert.Equal(1f, cal.AccelG(new Vector3(8192, 8192, 8192)).X, 0.01f);
        }

        byte[] bad = Feature(0x05, 41, true, true);
        bad[10] ^= 0x01;
        Assert.Same(ImuCalibration.DualShock4Nominal, ImuCalibration.ParseDualShock4(bad, true));
        Assert.Same(ImuCalibration.DualShock4Nominal, ImuCalibration.ParseDualShock4(new byte[10], false));
    }

    [Fact]
    public void Implausible_axis_falls_back_to_nominal_values()
    {
        byte[] f = Feature(0x02, 37, false, false);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(11), 0);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(13), 0);
        ImuCalibration cal = ImuCalibration.ParseDualShock4(f, false);
        Assert.Equal(540f, cal.GyroDegPerSec(new Vector3(8000, 0, 0)).X, 0.5f);
        Assert.Equal(100f / 16f, cal.GyroDegPerSec(new Vector3(0, 100, 0)).Y, 0.001f);
        Assert.True(ImuCalibration.IsBlank(new byte[37]));
    }

    [Fact]
    public void Builds_usb_and_bt_ds4_output_reports_and_deduplicates_unsupported_fields()
    {
        var state = new OutputState { Lightbar = new Rgb(200, 100, 50), RumbleLeft = 7, RumbleRight = 9, Brightness = LedBrightness.Medium };
        byte[] usb = Ds4OutputReport.BuildUsb(state);
        Assert.Equal(32, usb.Length);
        Assert.Equal(0x05, usb[0]);
        Assert.Equal(0x07, usb[1]);
        Assert.Equal(9, usb[4]);
        Assert.Equal(7, usb[5]);
        Assert.Equal(new byte[] { 124, 62, 31 }, usb[6..9]);
        Assert.All(usb[9..], b => Assert.Equal(0, b));

        byte[] bt = Ds4OutputReport.BuildBluetooth(state with { Brightness = LedBrightness.High });
        Assert.Equal(78, bt.Length);
        Assert.Equal(0x11, bt[0]);
        Assert.Equal(0xC0, bt[1]);
        Assert.Equal(0x00, bt[2]);
        Assert.Equal(0x07, bt[3]);
        Assert.Equal(9, bt[6]);
        Assert.Equal(7, bt[7]);
        Assert.Equal(new byte[] { 200, 100, 50 }, bt[8..11]);
        Assert.True(Crc32.Verify(Crc32.OutputSeed, bt));

        Assert.Equal(new Rgb(60, 30, 15), Ds4OutputReport.ScaleBrightness(new Rgb(200, 100, 50), LedBrightness.Low));

        Assert.True(Ds4OutputReport.SameEffect(state, state with
        {
            PlayerLeds = 3,
            MicLed = MicLed.On,
            SpeakerVolume = 1,
            LeftTrigger = TriggerEffect.Off,
        }));
        Assert.False(Ds4OutputReport.SameEffect(state, state with { RumbleLeft = 8 }));
        Assert.False(Ds4OutputReport.SameEffect(state, null));
    }

    [Fact]
    public void Dongle_is_usb_only_and_ds4_ids_are_classified()
    {
        Assert.True(DualSenseIds.IsDualShock4(ControllerModel.DualShock4));
        Assert.False(DualSenseIds.IsDualShock4(ControllerModel.DualSense));
    }

    [Theory]
    [InlineData(DualSenseIds.DualSenseProductId, 64, ControllerModel.DualSense, Link.Usb)]
    [InlineData(DualSenseIds.DualSenseProductId, 78, ControllerModel.DualSense, Link.Bluetooth)]
    [InlineData(DualSenseIds.DualSenseEdgeProductId, 78, ControllerModel.DualSenseEdge, Link.Bluetooth)]
    [InlineData(DualSenseIds.DualShock4V1ProductId, 64, ControllerModel.DualShock4, Link.Usb)]
    [InlineData(DualSenseIds.DualShock4V2ProductId, 78, ControllerModel.DualShock4, Link.Bluetooth)]
    [InlineData(DualSenseIds.DualShock4V2ProductId, 547, ControllerModel.DualShock4, Link.Bluetooth)]
    [InlineData(DualSenseIds.DualShock4DongleProductId, 64, ControllerModel.DualShock4, Link.Usb)]
    public void Classifies_model_and_link_from_product_id_and_report_length(ushort productId, int length, ControllerModel model, Link link)
    {
        Assert.Equal((model, link), DualSenseIds.Classify(productId, length));
    }

    [Theory]
    [InlineData(DualSenseIds.DualSenseProductId, 547)]
    [InlineData(DualSenseIds.DualSenseProductId, 65)]
    [InlineData(DualSenseIds.DualSenseEdgeProductId, 547)]
    [InlineData(DualSenseIds.DualShock4V1ProductId, 77)]
    [InlineData(DualSenseIds.DualShock4V2ProductId, 0)]
    [InlineData(DualSenseIds.DualShock4DongleProductId, 78)]
    [InlineData(DualSenseIds.DualShock4DongleProductId, 547)]
    [InlineData((ushort)0x1234, 64)]
    public void Rejects_unknown_products_and_lengths(ushort productId, int length)
    {
        Assert.Null(DualSenseIds.Classify(productId, length));
    }

    [Fact]
    public void Bluetooth_ds4_report_read_into_a_547_byte_buffer_parses()
    {
        byte[] padded = new byte[547];
        BtReport(UsbReport()).CopyTo(padded, 0);
        var clock = default(Ds4Timestamp);
        Assert.Equal(ParseResult.Ok, Ds4InputParser.TryParse(padded.AsSpan(0, 547), Link.Bluetooth, ref clock, out InputState? s));
        Check(s!);
    }

    [Fact]
    public void Gyro_bias_is_not_subtracted_but_still_centres_the_span()
    {
        byte[] f = Feature(0x02, 37, false, false);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(1), 100);
        ImuCalibration cal = ImuCalibration.ParseDualShock4(f, false);
        Assert.Equal(0f, cal.GyroDegPerSec(new Vector3(0, 0, 0)).X, 0.001f);
        Assert.Equal(540f, cal.GyroDegPerSec(new Vector3(8000, 0, 0)).X, 0.5f);

        // A bias beyond +/-1024 is rejected; the axis keeps the nominal scale.
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(1), 2000);
        Assert.Equal(160f / 16f, ImuCalibration.ParseDualShock4(f, false).GyroDegPerSec(new Vector3(160, 0, 0)).X, 0.001f);
    }

    [Fact]
    public void Missing_ds4_calibration_uses_ds4_scales_and_identity_is_unchanged()
    {
        var raw = new Vector3(160, 160, 160);
        Assert.Equal(10f, ImuCalibration.DualShock4Nominal.GyroDegPerSec(raw).Y, 0.001f);
        Assert.Equal(1f, ImuCalibration.DualShock4Nominal.AccelG(new Vector3(8192, 0, 0)).X, 0.001f);
        Assert.Same(ImuCalibration.DualShock4Nominal, ImuCalibration.ParseDualShock4(default, false));

        byte[] shortUsb = Feature(0x02, 37, false, false)[..30];
        Assert.Same(ImuCalibration.DualShock4Nominal, ImuCalibration.ParseDualShock4(shortUsb, false));
        Assert.Equal(160f / 1024f, ImuCalibration.Identity.GyroDegPerSec(raw).Y, 0.001f);
    }

    [Fact]
    public void Dongle_pairing_resumes_on_every_pair_and_releases_once_on_unpair()
    {
        var p = new DonglePairing();
        Assert.Equal(DongleStep.Resume, p.Next(false));
        Assert.Equal(DongleStep.Pass, p.Next(false));
        Assert.Equal(DongleStep.DropAndRelease, p.Next(true));
        Assert.Equal(DongleStep.Drop, p.Next(true));
        Assert.Equal(DongleStep.Drop, p.Next(true));
        Assert.Equal(DongleStep.Resume, p.Next(false));
        Assert.Equal(DongleStep.Pass, p.Next(false));
        Assert.Equal(DongleStep.DropAndRelease, p.Next(true));

        // Unpaired from the start: nothing was held, so nothing to release.
        var fresh = new DonglePairing();
        Assert.Equal(DongleStep.Drop, fresh.Next(true));
        Assert.Equal(DongleStep.Resume, fresh.Next(false));
    }

    [Fact]
    public void Neutral_state_centres_sticks_releases_everything_and_keeps_battery()
    {
        var clock = default(Ds4Timestamp);
        Ds4InputParser.TryParse(UsbReport(), Link.Usb, ref clock, out InputState? held);
        InputState neutral = Ds4InputParser.Neutral(held);
        Assert.Equal(Buttons.None, neutral.Buttons);
        Assert.Equal(new Stick(128, 128), neutral.Left);
        Assert.Equal(new Stick(128, 128), neutral.Right);
        Assert.Equal((byte)0, neutral.L2);
        Assert.Equal((byte)0, neutral.R2);
        Assert.False(neutral.Touch1.Active);
        Assert.False(neutral.Touch2.Active);
        Assert.Equal(held!.Battery, neutral.Battery);
        Assert.Equal(held.SensorTimestamp, neutral.SensorTimestamp);
        Assert.Equal(ChargeState.Discharging, Ds4InputParser.Neutral(null).Battery.Charge);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("a0b1c2d3e4f5", "A0:B1:C2:D3:E4:F5")]
    [InlineData(" 00:11:22:33:44:55 ", "00:11:22:33:44:55")]
    [InlineData("000000000000", null)]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("AA:BB:CC:DD:EE", null)]
    [InlineData("AA:BB:CC:DD:EE:GG", null)]
    [InlineData("AABBCCDDEEFF00", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_the_bluetooth_address_from_the_hid_serial(string? serial, string? expected)
    {
        Assert.Equal(expected, DualSenseController.MacFromSerial(serial));
    }

    [Fact]
    public void Battery_error_is_passed_on_to_the_virtual_ds4_as_a_level_above_eleven()
    {
        var clock = default(Ds4Timestamp);
        byte[] usb = UsbReport();
        usb[30] = 0x1D;
        Ds4InputParser.TryParse(usb, Link.Usb, ref clock, out InputState? s);
        byte[] report = new byte[64];
        VirtualDualShock4.WriteInput(s!, 0, report);
        Assert.Equal(0x0F, report[30] & 0x0F);
        Assert.Equal(0x10, report[30] & 0x10);
    }

    [Fact]
    public void HapticStreamer_rejects_dualshock4()
    {
        var details = new ControllerDetails(ControllerModel.DualShock4, Link.Bluetooth, null, null, ImuCalibration.Identity);
        var controller = new DualSenseController("ds4", details, _ => { });
        Assert.Throws<InvalidOperationException>(() => new HapticStreamer(controller, (_, _) => { }));
    }
}
