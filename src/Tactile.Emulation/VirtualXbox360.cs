using System.Buffers.Binary;
using Tactile.Device.DualSense;

namespace Tactile.Emulation;

/// <summary>Wired Xbox 360 controller, gamepad interface only (docs/protocol-emulation.md §4).</summary>
internal sealed class VirtualXbox360 : VirtualUsbDevice
{
    public const int InputLength = 20;

    private static readonly byte[] Device = Hex("12 01 00 02 FF FF FF 08 5E 04 8E 02 14 01 01 02 03 01");

    // The minimal configuration (interface 0 only) that is known to work with xusb22 over USB/IP.
    private static readonly byte[] Config = Hex(
        "09 02 31 00 01 01 00 A0 FA" +
        "09 04 00 00 02 FF 5D 01 00" +
        "11 21 00 01 01 25 81 14 00 00 00 00 13 01 08 00 00" +
        "07 05 81 03 20 00 04" +
        "07 05 01 03 20 00 08");

    private static readonly byte[] XenonMagic = [0x31, 0x3F, 0xCF, 0xDC];

    // Status packets a real pad sends on the IN pipe before the first input packet.
    private static readonly byte[][] Startup =
    [
        [0x01, 0x03, 0x0E],
        [0x02, 0x03, 0x00],
        [0x03, 0x03, 0x03],
        [0x08, 0x03, 0x00],
        [0x01, 0x03, 0x03],
    ];

    public VirtualXbox360(UsbIpServer server, int busNum, int slot = 0)
        : base(server, busNum)
    {
        Strings = ["©Microsoft Corporation", "Controller", slot == 0 ? $"{0x7AC70000 + busNum:X8}" : SecondaryPads.Serial(slot)];
    }

    public override VirtualKind Kind => VirtualKind.Xbox360;
    public override int Speed => SpeedFull;
    public override byte[] DeviceDescriptor => Device;
    public override byte[] ConfigDescriptor => Config;
    public override int InEndpoint => 1;
    public override int OutEndpoint => 1;
    public override int IntervalMs => 4;
    protected override string[] Strings { get; }
    protected override IEnumerable<byte[]> StartupPackets => Startup;

    protected override byte[] BuildInput(InputState state)
    {
        byte[] report = new byte[InputLength];
        WriteInput(state, report);
        return report;
    }

    internal static void WriteInput(InputState s, Span<byte> r)
    {
        r[..InputLength].Clear();
        r[0] = 0x00;
        r[1] = InputLength;
        ushort buttons = 0;
        Buttons k = s.Buttons;
        if ((k & Buttons.Up) != 0) buttons |= 0x0001;
        if ((k & Buttons.Down) != 0) buttons |= 0x0002;
        if ((k & Buttons.Left) != 0) buttons |= 0x0004;
        if ((k & Buttons.Right) != 0) buttons |= 0x0008;
        if ((k & Buttons.Options) != 0) buttons |= 0x0010;
        if ((k & Buttons.Create) != 0) buttons |= 0x0020;
        if ((k & Buttons.L3) != 0) buttons |= 0x0040;
        if ((k & Buttons.R3) != 0) buttons |= 0x0080;
        if ((k & Buttons.L1) != 0) buttons |= 0x0100;
        if ((k & Buttons.R1) != 0) buttons |= 0x0200;
        if ((k & Buttons.PS) != 0) buttons |= 0x0400;
        if ((k & Buttons.Cross) != 0) buttons |= 0x1000;
        if ((k & Buttons.Circle) != 0) buttons |= 0x2000;
        if ((k & Buttons.Square) != 0) buttons |= 0x4000;
        if ((k & Buttons.Triangle) != 0) buttons |= 0x8000;
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], buttons);
        r[4] = s.L2;
        r[5] = s.R2;
        BinaryPrimitives.WriteInt16LittleEndian(r[6..], Axis(s.Left.X));
        BinaryPrimitives.WriteInt16LittleEndian(r[8..], AxisInverted(s.Left.Y));
        BinaryPrimitives.WriteInt16LittleEndian(r[10..], Axis(s.Right.X));
        BinaryPrimitives.WriteInt16LittleEndian(r[12..], AxisInverted(s.Right.Y));
    }

    internal static short Axis(byte v) => ScaleAxis(v - 128, v > 128 ? 127 : 128);

    // DualSense Y grows downwards, XInput Y grows upwards.
    internal static short AxisInverted(byte v) => ScaleAxis(128 - v, v > 128 ? 127 : 128);

    private static short ScaleAxis(int delta, int range) =>
        (short)(delta * (delta < 0 ? 32768 : 32767) / range);

    public override void Out(ReadOnlySpan<byte> data, long timestamp)
    {
        if (data.Length >= 5 && data[0] == 0x00)
        {
            Raise(new RumbleFeedback(timestamp, data[3], data[4]));
        }
        else if (data.Length >= 3 && data[0] == 0x01)
        {
            Raise(new LedFeedback(timestamp, data[2]));
        }
    }

    // Vendor requests are told apart by length only; the exact requests are logged for the first hardware run.
    protected override ControlResult ClassOrVendor(SetupPacket s, ReadOnlySpan<byte> data, long timestamp) => s.Length switch
    {
        4 when s.In => ControlResult.Ok(XenonMagic),
        8 or 20 => ControlResult.Stall,
        _ => ControlResult.Ok(),
    };
}
