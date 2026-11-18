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
    [InlineData(128, 128, -129)]
    public void Xbox360_axes_cover_the_int16_range_with_y_up_positive(byte value, short x, short y)
    {
        Assert.Equal(x, VirtualXbox360.Axis(value));
        Assert.Equal(y, VirtualXbox360.AxisInverted(value));
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
