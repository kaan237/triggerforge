using System.Buffers.Binary;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class VirtualPadTests
{
    private static readonly InputState Sample = new(
        Buttons.Cross | Buttons.Triangle | Buttons.L1 | Buttons.R3 | Buttons.Options | Buttons.PS | Buttons.Touchpad | Buttons.Down | Buttons.Left,
        new Stick(10, 200), new Stick(255, 0), 77, 255,
        new Vector3(-100, 2000, -32768), new Vector3(8192, -8192, 300), 0x12345678,
        new TouchPoint(true, 5, 1919, 1000), new TouchPoint(false, 0x7F, 0, 0),
        new Battery(55, ChargeState.Charging), true, false, true);

    [Fact]
    public void DualSense_input_report_has_known_bytes()
    {
        byte[] r = new byte[64];
        VirtualDualSense.WriteInput(Sample, 9, r);

        Assert.Equal(0x01, r[0]);
        Assert.Equal([10, 200, 255, 0, 77, 255, 9], r[1..8]);
        Assert.Equal(0x05 | 0x20 | 0x80, r[8]);                 // hat down-left, cross, triangle
        Assert.Equal(0x01 | 0x20 | 0x80, r[9]);                 // L1, Options, R3
        Assert.Equal(0x03, r[10]);                              // PS, touchpad
        Assert.Equal(-100, BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(16)));
        Assert.Equal(-32768, BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(20)));
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(22)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(28)));
        Assert.Equal([0x05, 0x7F, 0x87, 0x3E], r[33..37]);      // id 5, x 1919, y 1000
        Assert.Equal(0x80 | 0x7F, r[37]);
        Assert.Equal(0x15, r[53]);                              // charging, level 5
        Assert.Equal(0x05, r[54]);                              // headphone, mic muted
    }

    [Fact]
    public void DualSense_input_report_parses_back_to_the_same_state()
    {
        byte[] r = new byte[64];
        VirtualDualSense.WriteInput(Sample, 0, r);

        Assert.Equal(ParseResult.Ok, InputParser.TryParse(r, Link.Usb, out InputState? back));
        Assert.Equal(Sample, back);
    }

    [Fact]
    public void Opposing_dpad_directions_cancel()
    {
        byte[] r = new byte[64];
        VirtualDualSense.WriteInput(Sample with { Buttons = Buttons.Up | Buttons.Down | Buttons.Right }, 0, r);
        Assert.Equal(2, r[8] & 0x0F);
    }

    [Fact]
    public void Xbox360_input_maps_buttons_triggers_and_sticks()
    {
        byte[] r = new byte[20];
        VirtualXbox360.WriteInput(Sample, r);

        Assert.Equal([0x00, 0x14], r[..2]);
        ushort expected = 0x0002 | 0x0004 | 0x0010 | 0x0080 | 0x0100 | 0x0400 | 0x1000 | 0x8000;
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2)));
        Assert.Equal(77, r[4]);
        Assert.Equal(255, r[5]);
        Assert.Equal(VirtualXbox360.Axis(10), BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(6)));
        Assert.Equal(VirtualXbox360.AxisInverted(200), BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(8)));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(10)));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(12)));   // DualSense Y 0 = full up
        Assert.All(r[14..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Xbox360_create_maps_to_back()
    {
        byte[] r = new byte[20];
        VirtualXbox360.WriteInput(Sample with { Buttons = Buttons.Create | Buttons.Square | Buttons.Circle | Buttons.R1 | Buttons.L3 | Buttons.Up | Buttons.Right }, r);
        Assert.Equal(0x0020 | 0x4000 | 0x2000 | 0x0200 | 0x0040 | 0x0001 | 0x0008, BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2)));
    }

    [Theory]
    [InlineData(0, -32768, 32767)]
    [InlineData(255, 32767, -32768)]
    [InlineData(128, 0, 0)]
    public void Xbox360_axes_cover_the_int16_range_with_y_up_positive(byte value, short x, short y)
    {
        var stick = new Stick(value, value);
        byte[] report = new byte[VirtualXbox360.InputLength];
        VirtualXbox360.WriteInput(Sample with { Left = stick, Right = stick }, report);
        Assert.Equal(x, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(6)));
        Assert.Equal(y, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(8)));
        Assert.Equal(x, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(10)));
        Assert.Equal(y, BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(12)));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(63)]
    public void DualSense_output_report_raises_the_47_byte_block(int length)
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);
        var got = new List<PadFeedback>();
        pad.Feedback += got.Add;

        byte[] report = new byte[length];
        report[0] = 0x02;
        for (int i = 1; i < length; i++)
        {
            report[i] = (byte)i;
        }

        pad.Out(report, 42);

        var f = Assert.IsType<DualSenseOutputFeedback>(Assert.Single(got));
        Assert.Equal(42, f.Timestamp);
        Assert.Equal(report[1..48], f.State);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(65)]
    public void DualSense_output_with_bad_length_or_id_is_ignored(int length)
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);
        var got = new List<PadFeedback>();
        pad.Feedback += got.Add;

        byte[] report = new byte[length];
        report[0] = 0x02;
        pad.Out(report, 0);
        byte[] wrongId = new byte[48];
        wrongId[0] = 0x05;
        pad.Out(wrongId, 0);

        Assert.Empty(got);
    }

    [Fact]
    public void DualSense_set_report_output_on_ep0_raises_feedback()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);
        var got = new List<PadFeedback>();
        pad.Feedback += got.Add;

        byte[] report = new byte[48];
        report[0] = 0x02;
        report[1] = 0xAB;
        ControlResult r = pad.Control(new SetupPacket(0x21, 0x09, 0x0202, 0, 48), report, 7);

        Assert.Equal(0, r.Status);
        Assert.Equal(0xAB, Assert.IsType<DualSenseOutputFeedback>(Assert.Single(got)).State[0]);
    }

    [Fact]
    public void DualSense_feature_reports_come_from_identity_with_exact_length()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        byte[] calibration = new byte[41];
        calibration[0] = 0x05;
        calibration[1] = 0x11;
        byte[] pairing = [0x09, 1, 2, 3, 4, 5, 6];
        byte[] firmware = new byte[100];
        firmware[0] = 0x20;
        firmware[63] = 0x63;
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, new DualSenseIdentity(calibration, pairing, firmware));

        byte[] Get(byte id) => pad.Control(new SetupPacket(0xA1, 0x01, (ushort)(0x0300 | id), 0, 64), [], 0).Data;

        Assert.Equal(calibration, Get(0x05));
        Assert.Equal(20, Get(0x09).Length);
        Assert.Equal(pairing, Get(0x09)[..7]);
        Assert.Equal(64, Get(0x20).Length);
        Assert.Equal(0x63, Get(0x20)[63]);
        Assert.Equal([0x22, .. new byte[63]], Get(0x22));
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xA1, 0x01, 0x0377, 0, 64), [], 0).Status);
    }

    [Fact]
    public void DualSense_default_identity_gives_usable_calibration()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);
        byte[] f = pad.Control(new SetupPacket(0xA1, 0x01, 0x0305, 0, 41), [], 0).Data;

        ImuCalibration cal = ImuCalibration.Parse(f);
        Assert.InRange(cal.AccelG(new Vector3(8192, 0, 0)).X, 0.99f, 1.01f);
        Assert.InRange(cal.GyroDegPerSec(new Vector3(1640, 0, 0)).X, 90f, 110f);
    }

    [Fact]
    public void Standard_requests_answer_and_unknown_ones_stall()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);

        Assert.Equal([4, 3, 0x09, 0x04], pad.Control(new SetupPacket(0x80, 0x06, 0x0300, 0, 255), [], 0).Data);
        byte[] product = pad.Control(new SetupPacket(0x80, 0x06, 0x0302, 0x0409, 255), [], 0).Data;
        Assert.Equal("DualSense Wireless Controller", System.Text.Encoding.Unicode.GetString(product, 2, product.Length - 2));
        Assert.Equal(product.Length, product[0]);
        Assert.Equal(273, pad.Control(new SetupPacket(0x81, 0x06, 0x2200, 0, 273), [], 0).Data.Length);
        Assert.Equal(0, pad.Control(new SetupPacket(0x00, 0x09, 0x0001, 0, 0), [], 0).Status);
        Assert.Equal([1], pad.Control(new SetupPacket(0x80, 0x08, 0, 0, 1), [], 0).Data);
        Assert.Equal(0, pad.Control(new SetupPacket(0x21, 0x0A, 0, 0, 0), [], 0).Status);
        Assert.Equal([0, 0], pad.Control(new SetupPacket(0x80, 0x00, 0, 0, 2), [], 0).Data);

        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x80, 0x06, 0x0600, 0, 10), [], 0).Status);   // qualifier
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x80, 0x06, 0x03EE, 0, 18), [], 0).Status);   // MS OS string
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xC0, 0x01, 0, 0, 4), [], 0).Status);         // vendor
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x01, 0x0B, 1, 0, 0), [], 0).Status);         // alt 1
    }

    [Fact]
    public void DualShock4_descriptors_and_feature_reports_match_declared_sizes()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = Assert.IsType<VirtualDualShock4>(server.Create(VirtualKind.DualShock4, null));
        Assert.Equal(Convert.FromHexString("12010002000000404C05CC09000101020001"), pad.DeviceDescriptor);
        Assert.Equal(VirtualUsbDevice.SpeedHigh, pad.Speed);
        Assert.Equal(4, pad.IntervalMs);
        Assert.Equal(4, pad.InEndpoint);
        Assert.Equal(3, pad.OutEndpoint);

        byte[] report = pad.Control(new SetupPacket(0x81, 0x06, 0x2200, 0, 512), [], 0).Data;
        byte[] config = pad.ConfigDescriptor;
        Assert.Equal(41, config.Length);
        Assert.Equal(Convert.FromHexString("09022900010100C0FA09040000020300000009211101000122"), config[..25]);
        Assert.Equal(report.Length, BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(25)));
        Assert.Equal(Convert.FromHexString("0705840340000607050303400006"), config[27..]);
        Assert.Equal(config[18..27], pad.Control(new SetupPacket(0x81, 0x06, 0x2100, 0, 9), [], 0).Data);

        var bits = new Dictionary<(byte Kind, byte Id), int>();
        byte id = 0;
        int count = 0, size = 0;
        for (int offset = 0; offset < report.Length;)
        {
            byte item = report[offset];
            switch (item)
            {
                case 0x85: id = report[offset + 1]; break;
                case 0x75: size = report[offset + 1]; break;
                case 0x95: count = report[offset + 1]; break;
                case 0x81 or 0x91 or 0xB1:
                    bits[(item, id)] = bits.GetValueOrDefault((item, id)) + size * count;
                    break;
            }

            int itemSize = item & 0x03;
            offset += 1 + (itemSize == 3 ? 4 : itemSize);
        }

        Assert.Equal(63 * 8, bits[(0x81, 0x01)]);
        Assert.Equal(31 * 8, bits[(0x91, 0x05)]);
        (byte Id, int Bytes)[] required =
        [
            (0x02, 37), (0x04, 42), (0x08, 48), (0x10, 5), (0x11, 3), (0x12, 16), (0x13, 24),
            (0x14, 21), (0x15, 45), (0x80, 7), (0x81, 7), (0x82, 6), (0x83, 2), (0xA3, 49),
        ];
        foreach (var feature in required)
        {
            Assert.Equal((feature.Bytes - 1) * 8, bits[(0xB1, feature.Id)]);
        }

        foreach (((byte kind, byte reportId), int bitCount) in bits)
        {
            if (kind != 0xB1) continue;
            byte[] data = GetFeature(pad, reportId);
            Assert.Equal(0, bitCount % 8);
            Assert.Equal(bitCount / 8 + 1, data.Length);
            Assert.Equal(reportId, data[0]);
            if (reportId is not (0x02 or 0x12 or 0x81 or 0xA3))
            {
                Assert.All(data[1..], value => Assert.Equal(0, value));
            }
        }

        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xA1, 0x01, 0x0377, 0, 64), [], 0).Status);
    }

    [Fact]
    public void DualShock4_default_and_identity_calibration_and_version_are_valid()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = Assert.IsType<VirtualDualShock4>(server.Create(VirtualKind.DualShock4, null));
        byte[] calibration = GetFeature(pad, 0x02);
        for (int axis = 0; axis < 3; axis++)
        {
            short plus = BinaryPrimitives.ReadInt16LittleEndian(calibration.AsSpan(7 + axis * 2));
            short minus = BinaryPrimitives.ReadInt16LittleEndian(calibration.AsSpan(13 + axis * 2));
            Assert.True(plus > minus);
        }

        Assert.True(BinaryPrimitives.ReadInt16LittleEndian(calibration.AsSpan(19)) > 0);
        for (int i = 23; i <= 31; i += 4)
        {
            Assert.True(BinaryPrimitives.ReadInt16LittleEndian(calibration.AsSpan(i))
                > BinaryPrimitives.ReadInt16LittleEndian(calibration.AsSpan(i + 2)));
        }

        Assert.Equal([0, 0], calibration[35..]);
        Assert.Equal([0x81, 1, 0x54, 0x43, 0x41, 0x54, 0x02], GetFeature(pad, 0x81));
        Assert.Equal([.. GetFeature(pad, 0x81)[1..], 8, 0x25, 0, .. new byte[6]], GetFeature(pad, 0x12)[1..]);

        byte[] realCalibration = [0x05, .. calibration[1..35], 0xAA, 0xBB];
        realCalibration[1] = 0x11;
        byte[] pairing = [0x09, 6, 5, 4, 3, 2, 1, 0, 0, 0, 7, 8, 9, 10, 11, 12];
        var real = Assert.IsType<VirtualDualShock4>(server.Create(VirtualKind.DualShock4, new(realCalibration, pairing, [])));
        byte[] expectedReal = new byte[37];
        expectedReal[0] = 0x02;
        VirtualDualShock4.ConvertCalibration(realCalibration, expectedReal);
        Assert.Equal(expectedReal, GetFeature(real, 0x02));
        Assert.Equal([0x12, 6, 5, 4, 3, 2, 1, 8, 0x25, 0, 7, 8, 9, 10, 11, 12], GetFeature(real, 0x12));
        Assert.Equal([0x81, 6, 5, 4, 3, 2, 1], GetFeature(real, 0x81));

        byte[] version = GetFeature(real, 0xA3);
        Assert.Equal("Jun  2 2018\0\0\0\0\0"u8.ToArray(), version[1..17]);
        Assert.Equal("12:00:00\0\0\0\0\0\0\0\0"u8.ToArray(), version[17..33]);
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16LittleEndian(version.AsSpan(35)));
        Assert.Equal(0x00000100u, BinaryPrimitives.ReadUInt32LittleEndian(version.AsSpan(41)));
    }

    [Fact]
    public void DualShock4_calibration_reorders_gyro_ranges_to_usb_layout()
    {
        byte[] dualSense = new byte[41];
        dualSense[0] = 0x05;
        for (int i = 1; i < 35; i++)
        {
            dualSense[i] = (byte)i;
        }

        byte[] ds4 = new byte[37];
        VirtualDualShock4.ConvertCalibration(dualSense, ds4);

        Assert.Equal(dualSense[1..7], ds4[1..7]);
        Assert.Equal([7, 8, 11, 12, 15, 16, 9, 10, 13, 14, 17, 18], ds4[7..19]);
        Assert.Equal(dualSense[19..35], ds4[19..35]);
    }

    [Fact]
    public void DualShock4_secondary_slots_have_unique_serial_and_default_mac()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        for (int slot = 1; slot <= 3; slot++)
        {
            var pad = Assert.IsType<VirtualDualShock4>(server.CreateSecondaryPad(slot, VirtualKind.DualShock4, null));
            Assert.Equal(3, pad.DeviceDescriptor[16]);
            byte[] serial = pad.Control(new SetupPacket(0x80, 0x06, 0x0303, 0x0409, 255), [], 0).Data;
            Assert.Equal($"TACTILE{slot}", System.Text.Encoding.Unicode.GetString(serial[2..]));
            Assert.Equal(slot + 1, GetFeature(pad, 0x81)[1]);
        }
    }

    [Fact]
    public void DualShock4_input_report_packs_buttons_sticks_triggers_imu_touch_and_battery()
    {
        byte[] report = new byte[64];
        Array.Fill(report, (byte)0xFF);
        VirtualDualShock4.WriteInput(Sample with
        {
            Buttons = Sample.Buttons | Buttons.Create | Buttons.R2 | Buttons.L3 | Buttons.Mic,
            Touch2 = new TouchPoint(true, 0x82, 2048, 255),
            MicConnected = true,
        }, 0xC9, report);

        Assert.Equal(new byte[]
        {
            1, 10, 200, 255, 0, 0xA5, 0xF9, 0x27, 77, 255, 0x67, 0x45, 0x14,
            0x9C, 0xFF, 0xD0, 0x07, 0x00, 0x80, 0x00, 0x20, 0x00, 0xE0, 0x2C, 0x01,
            0, 0, 0, 0, 0, 0x75, 0, 0, 1, 0xC9, 5, 0x7F, 0xD7, 0x3A, 2, 0x7F, 0xF7, 0x0F,
            0, 0x80, 0, 0, 0, 0x80, 0, 0, 0,
            0, 0x80, 0, 0, 0, 0x80, 0, 0, 0,
            0, 0, 0,
        }, report);

        VirtualDualShock4.WriteInput(Sample with
        {
            Buttons = Buttons.Up | Buttons.Down | Buttons.Left | Buttons.Right,
            Battery = new Battery(100, ChargeState.Full),
        }, 0x40, report);
        Assert.Equal(8, report[5]);
        Assert.Equal(0, report[7]);
        Assert.Equal(0x3B, report[30]);
        Assert.Equal([0x80, 0, 0, 0], report[39..43]);
    }

    [Fact]
    public void DualShock4_output_report_raises_rumble_and_lightbar_feedback()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = Assert.IsType<VirtualDualShock4>(server.Create(VirtualKind.DualShock4, null));
        var got = new List<PadFeedback>();
        pad.Feedback += got.Add;
        byte[] report = [0x05, 1, 0, 0, 0x23, 0x45, 0x67, 0x89, 0xAB, .. new byte[23]];
        var rgb = new Rgb(0x67, 0x89, 0xAB);

        pad.Out(report, 1);
        report[1] = 2;
        Assert.Equal(0, pad.Control(new SetupPacket(0x21, 0x09, 0x0205, 0, 32), report, 2).Status);
        report[1] = 3;
        Assert.Equal(0, pad.Control(new SetupPacket(0x21, 0x09, 0x0205, 0, 31), report.AsSpan(1), 3).Status);
        pad.Out([.. report, .. new byte[32]], 4);
        pad.Out(report.AsSpan(1), 5);
        pad.Out([.. report, .. new byte[33]], 5);
        report[0] = 0x02;
        pad.Out(report, 5);
        pad.Control(new SetupPacket(0x21, 0x09, 0x0205, 0, 32), report, 5);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x21, 0x09, 0x0202, 0, 32), report, 5).Status);

        Assert.Equal(new PadFeedback[]
        {
            new DualShock4Feedback(1, true, 0x45, 0x23, false, rgb),
            new DualShock4Feedback(2, false, 0x45, 0x23, true, rgb),
            new DualShock4Feedback(3, true, 0x45, 0x23, true, rgb),
            new DualShock4Feedback(4, true, 0x45, 0x23, true, rgb),
        }, got);
    }

    private static byte[] GetFeature(VirtualUsbDevice pad, byte id)
    {
        ControlResult result = pad.Control(new SetupPacket(0xA1, 0x01, (ushort)(0x0300 | id), 0, 64), [], 0);
        Assert.Equal(0, result.Status);
        return result.Data;
    }

    [Fact]
    public void Xbox360_vendor_requests_follow_length_rules_and_output_raises_feedback()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.Xbox360, null);
        var got = new List<PadFeedback>();
        pad.Feedback += got.Add;

        Assert.Equal([0x31, 0x3F, 0xCF, 0xDC], pad.Control(new SetupPacket(0xC1, 0x01, 0x0100, 0, 4), [], 0).Data);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xC1, 0x01, 0x0100, 0, 20), [], 0).Status);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xC0, 0x01, 0, 0, 8), [], 0).Status);
        Assert.Equal(0, pad.Control(new SetupPacket(0x41, 0x00, 0, 0, 0), [], 0).Status);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x80, 0x06, 0x2200, 0, 64), [], 0).Status);

        pad.Out([0x00, 0x08, 0x00, 0xC8, 0x40, 0x00, 0x00, 0x00], 5);
        pad.Out([0x01, 0x03, 0x02], 6);
        Assert.Equal(new RumbleFeedback(5, 0xC8, 0x40), got[0]);
        Assert.Equal(new LedFeedback(6, 0x02), got[1]);
    }

    [Fact]
    public void Xbox360_sends_startup_packets_before_input_after_import()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.Xbox360, null);
        Assert.True(pad.TryAttach());

        Assert.Equal([0x01, 0x03, 0x0E], pad.NextInput(false));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(3, pad.NextInput(false)!.Length);
        }

        Assert.Equal(20, pad.NextInput(false)!.Length);
        Assert.Null(pad.NextInput(false));
        Assert.Equal(20, pad.NextInput(true)!.Length);
        Assert.False(pad.TryAttach());
    }
}
