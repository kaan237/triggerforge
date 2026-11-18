using System.Buffers.Binary;
using Tactile.Device.DualSense;

namespace Tactile.Emulation;

/// <summary>Wired DualSense with only the HID interface (docs/protocol-emulation.md §3.2).</summary>
internal sealed class VirtualDualSense : VirtualUsbDevice
{
    public const int InputLength = 64;
    public const int OutputStateLength = 47;

    private static readonly byte[] Device = Hex("12 01 00 02 00 00 00 40 4C 05 E6 0C 00 01 01 02 00 01");

    // Interrupt bInterval 6 at high speed = 4 ms, as on the real pad.
    private static readonly byte[] Config = Hex(
        "09 02 29 00 01 01 00 C0 FA" +
        "09 04 00 00 02 03 00 00 00" +
        "09 21 11 01 00 01 22 11 01" +
        "07 05 84 03 40 00 06" +
        "07 05 03 03 40 00 06");

    private static readonly byte[] ReportDescriptor = Hex(
        "05 01 09 05 A1 01 85 01 09 30 09 31 09 32 09 35" +
        "09 33 09 34 15 00 26 FF 00 75 08 95 06 81 02 06" +
        "00 FF 09 20 95 01 81 02 05 01 09 39 15 00 25 07" +
        "35 00 46 3B 01 65 14 75 04 95 01 81 42 65 00 05" +
        "09 19 01 29 0F 15 00 25 01 75 01 95 0F 81 02 06" +
        "00 FF 09 21 95 0D 81 02 06 00 FF 09 22 15 00 26" +
        "FF 00 75 08 95 34 81 02 85 02 09 23 95 2F 91 02" +
        "85 05 09 33 95 28 B1 02 85 08 09 34 95 2F B1 02" +
        "85 09 09 24 95 13 B1 02 85 0A 09 25 95 1A B1 02" +
        "85 20 09 26 95 3F B1 02 85 21 09 27 95 04 B1 02" +
        "85 22 09 40 95 3F B1 02 85 80 09 28 95 3F B1 02" +
        "85 81 09 29 95 3F B1 02 85 82 09 2A 95 09 B1 02" +
        "85 83 09 2B 95 3F B1 02 85 84 09 2C 95 3F B1 02" +
        "85 85 09 2D 95 02 B1 02 85 A0 09 2E 95 01 B1 02" +
        "85 E0 09 2F 95 3F B1 02 85 F0 09 30 95 3F B1 02" +
        "85 F1 09 31 95 3F B1 02 85 F2 09 32 95 0F B1 02" +
        "85 F4 09 35 95 3F B1 02 85 F5 09 36 95 03 B1 02" +
        "C0");

    // Feature report sizes including the id byte, from the report descriptor.
    private static readonly Dictionary<byte, int> FeatureSizes = new()
    {
        [0x05] = 41, [0x08] = 48, [0x09] = 20, [0x0A] = 27, [0x20] = 64, [0x21] = 5, [0x22] = 64,
        [0x80] = 64, [0x81] = 64, [0x82] = 10, [0x83] = 64, [0x84] = 64, [0x85] = 3, [0xA0] = 2,
        [0xE0] = 64, [0xF0] = 64, [0xF1] = 64, [0xF2] = 16, [0xF4] = 64, [0xF5] = 4,
    };

    private readonly Dictionary<byte, byte[]> _features = [];
    private byte _sequence;

    public VirtualDualSense(UsbIpServer server, int busNum, DualSenseIdentity? identity)
        : base(server, busNum)
    {
        foreach ((byte id, int size) in FeatureSizes)
        {
            _features[id] = Feature(id, size, null);
        }

        _features[0x05] = Feature(0x05, 41, identity?.Calibration ?? DefaultCalibration());
        _features[0x09] = Feature(0x09, 20, identity?.Pairing ?? DefaultPairing());
        _features[0x20] = Feature(0x20, 64, identity?.Firmware);
    }

    public override VirtualKind Kind => VirtualKind.DualSense;
    public override int Speed => SpeedHigh;
    public override byte[] DeviceDescriptor => Device;
    public override byte[] ConfigDescriptor => Config;
    public override int InEndpoint => 4;
    public override int OutEndpoint => 3;
    public override int IntervalMs => 4;
    protected override string[] Strings { get; } = ["Sony Interactive Entertainment", "DualSense Wireless Controller"];

    internal static int ReportDescriptorLength => ReportDescriptor.Length;

    protected override byte[] BuildInput(InputState state)
    {
        byte[] report = new byte[InputLength];
        WriteInput(state, unchecked(_sequence++), report);
        return report;
    }

    /// <summary>USB input report 0x01: id byte, then the 63-byte state block (docs/protocol.md, Input).</summary>
    internal static void WriteInput(InputState s, byte sequence, Span<byte> report)
    {
        report[..InputLength].Clear();
        report[0] = 0x01;
        Span<byte> b = report[1..];
        b[0] = s.Left.X;
        b[1] = s.Left.Y;
        b[2] = s.Right.X;
        b[3] = s.Right.Y;
        b[4] = s.L2;
        b[5] = s.R2;
        b[6] = sequence;

        Buttons k = s.Buttons;
        b[7] = Hat(k);
        if ((k & Buttons.Square) != 0) b[7] |= 0x10;
        if ((k & Buttons.Cross) != 0) b[7] |= 0x20;
        if ((k & Buttons.Circle) != 0) b[7] |= 0x40;
        if ((k & Buttons.Triangle) != 0) b[7] |= 0x80;
        b[8] = (byte)((uint)k >> 4);
        if ((k & Buttons.PS) != 0) b[9] |= 0x01;
        if ((k & Buttons.Touchpad) != 0) b[9] |= 0x02;
        if ((k & Buttons.Mic) != 0) b[9] |= 0x04;

        WriteVector(b[15..], s.GyroRaw);
        WriteVector(b[21..], s.AccelRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(b[27..], s.SensorTimestamp);
        WriteTouch(b.Slice(32, 4), s.Touch1);
        WriteTouch(b.Slice(36, 4), s.Touch2);

        int level = s.Battery.Charge == ChargeState.Full ? 10 : Math.Clamp(s.Battery.Percent / 10, 0, 10);
        b[52] = (byte)(((byte)s.Battery.Charge << 4) | level);
        b[53] = (byte)((s.HeadphoneConnected ? 0x01 : 0) | (s.MicConnected ? 0x02 : 0) | (s.MicMuted ? 0x04 : 0));
    }

    public override void Out(ReadOnlySpan<byte> data, long timestamp) => Output(data, timestamp);

    protected override byte[]? ClassDescriptor(SetupPacket s) => (s.Value >> 8) switch
    {
        0x21 => Config[18..27],
        0x22 => ReportDescriptor,
        _ => null,
    };

    protected override ControlResult ClassOrVendor(SetupPacket s, ReadOnlySpan<byte> data, long timestamp)
    {
        if (s.Type != 1)
        {
            return ControlResult.Stall;
        }

        int reportType = s.Value >> 8;
        byte id = (byte)s.Value;
        switch (s.Request)
        {
            case 0x01: // GET_REPORT
                if (reportType == 3 && _features.TryGetValue(id, out byte[]? feature))
                {
                    return ControlResult.Ok(feature);
                }

                return reportType == 1 && id == 0x01 ? ControlResult.Ok(CurrentInput()) : ControlResult.Stall;
            case 0x09: // SET_REPORT
                if (reportType == 2 && id == 0x02)
                {
                    Output(data, timestamp);
                    return ControlResult.Ok();
                }

                // Feature writes (for example the 0x80 factory command) are accepted and ignored.
                return reportType == 3 && FeatureSizes.ContainsKey(id) ? ControlResult.Ok() : ControlResult.Stall;
            case 0x02: // GET_IDLE
                return ControlResult.Ok([0]);
            case 0x03: // GET_PROTOCOL: report protocol
                return ControlResult.Ok([1]);
            case 0x0A: // SET_IDLE
            case 0x0B: // SET_PROTOCOL
                return ControlResult.Ok();
            default:
                return ControlResult.Stall;
        }
    }

    /// <summary>Report 0x02 arrives as 48 bytes (descriptor size) or 63 (protocol.md); bytes 1..47 are the state block.</summary>
    private void Output(ReadOnlySpan<byte> data, long timestamp)
    {
        ReadOnlySpan<byte> block;
        if (data.Length >= 1 + OutputStateLength && data.Length <= 64 && data[0] == 0x02)
        {
            block = data.Slice(1, OutputStateLength);
        }
        else if (data.Length == OutputStateLength)
        {
            // SET_REPORT data stage without the id byte (Linux hosts).
            block = data;
        }
        else
        {
            return;
        }

        Raise(new DualSenseOutputFeedback(timestamp, block.ToArray()));
    }

    private static byte Hat(Buttons k)
    {
        bool up = (k & Buttons.Up) != 0, down = (k & Buttons.Down) != 0;
        bool left = (k & Buttons.Left) != 0, right = (k & Buttons.Right) != 0;
        if (up && down) up = down = false;
        if (left && right) left = right = false;
        return (up, right, down, left) switch
        {
            (true, false, false, false) => 0,
            (true, true, false, false) => 1,
            (false, true, false, false) => 2,
            (false, true, true, false) => 3,
            (false, false, true, false) => 4,
            (false, false, true, true) => 5,
            (false, false, false, true) => 6,
            (true, false, false, true) => 7,
            _ => 8,
        };
    }

    private static void WriteVector(Span<byte> b, Vector3 v)
    {
        BinaryPrimitives.WriteInt16LittleEndian(b, ToShort(v.X));
        BinaryPrimitives.WriteInt16LittleEndian(b[2..], ToShort(v.Y));
        BinaryPrimitives.WriteInt16LittleEndian(b[4..], ToShort(v.Z));
    }

    private static short ToShort(float f) => (short)Math.Clamp(MathF.Round(f), short.MinValue, short.MaxValue);

    private static void WriteTouch(Span<byte> t, TouchPoint p)
    {
        t[0] = (byte)((p.Active ? 0 : 0x80) | (p.Id & 0x7F));
        t[1] = (byte)p.X;
        t[2] = (byte)(((p.X >> 8) & 0x0F) | ((p.Y & 0x0F) << 4));
        t[3] = (byte)(p.Y >> 4);
    }

    /// <summary>Exact-length report with the id byte set, copied from the real pad's bytes where given.</summary>
    private static byte[] Feature(byte id, int size, byte[]? source)
    {
        byte[] f = new byte[size];
        source?.AsSpan(0, Math.Min(source.Length, size)).CopyTo(f);
        f[0] = id;
        return f;
    }

    // Zero bias, about ±2000 deg/s over the int16 range for the gyro and 1 g = 8192 counts for the accelerometer.
    private static byte[] DefaultCalibration()
    {
        byte[] f = new byte[41];
        short[] values =
        [
            0, 0, 0,
            8856, -8856, 8856, -8856, 8856, -8856,
            540, 540,
            8192, -8192, 8192, -8192, 8192, -8192,
        ];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(1 + i * 2), values[i]);
        }

        return f;
    }

    // Stable, locally administered fake MAC (stored reversed), then the two bytes seen after it.
    private static byte[] DefaultPairing()
    {
        byte[] f = new byte[20];
        byte[] mac = [0x02, 0x54, 0x41, 0x43, 0x54, 0x01];
        for (int i = 0; i < 6; i++)
        {
            f[1 + i] = mac[5 - i];
        }

        f[7] = 0x08;
        f[8] = 0x25;
        return f;
    }
}
