using System.Buffers.Binary;
using Tactile.Device.DualSense;

namespace Tactile.Emulation;

/// <summary>Wired DualShock 4 v2, HID-only.</summary>
internal sealed class VirtualDualShock4 : VirtualUsbDevice
{
    private const int InputLength = 64;
    private const int OutputLength = 32;

    private static readonly byte[] Device = Hex("12 01 00 02 00 00 00 40 4C 05 CC 09 00 01 01 02 00 01");
    private static readonly byte[] DeviceWithSerial = [.. Device[..16], 3, Device[17]];

    private static readonly byte[] ReportDescriptor = Hex(
        "05 01 09 05 A1 01 85 01" +
        "09 30 09 31 09 32 09 35 15 00 26 FF 00 75 08 95 04 81 02" +
        "09 39 15 00 25 07 35 00 46 3B 01 65 14 75 04 95 01 81 42 65 00 46 00 00" +
        "05 09 19 01 29 0E 15 00 25 01 75 01 95 0E 81 02" +
        "75 06 95 01 81 03" +
        "05 01 09 33 09 34 15 00 26 FF 00 75 08 95 02 81 02" +
        "06 00 FF 09 21 95 36 81 02 85 05 09 22 95 1F 91 02" +
        "85 04 09 23 95 29 B1 02 85 02 09 24 95 24 B1 02" +
        "85 08 09 25 95 2F B1 02 85 10 09 26 95 04 B1 02 85 11 09 27 95 02 B1 02" +
        "06 02 FF 85 12 09 21 95 0F B1 02 85 13 09 22 95 17 B1 02" +
        "06 05 FF 85 14 09 20 95 14 B1 02 85 15 09 21 95 2C B1 02" +
        "06 80 FF 85 80 09 20 95 06 B1 02 85 81 09 21 95 06 B1 02" +
        "85 82 09 22 95 05 B1 02 85 83 09 23 95 01 B1 02 85 A3 09 43 95 30 B1 02" +
        "C0");

    private static readonly byte[] Config =
    [
        .. Hex("09 02 29 00 01 01 00 C0 FA 09 04 00 00 02 03 00 00 00 09 21 11 01 00 01 22"),
        (byte)(ReportDescriptor.Length & 0xFF), (byte)(ReportDescriptor.Length >> 8),
        .. Hex("07 05 84 03 40 00 06 07 05 03 03 40 00 06"),
    ];

    private static readonly Dictionary<byte, int> FeatureSizes = ReadFeatureSizes();
    private readonly Dictionary<byte, byte[]> _features = [];
    private byte _sequence;

    public VirtualDualShock4(UsbIpServer server, int busNum, DualSenseIdentity? identity, int slot)
        : base(server, busNum)
    {
        if (slot != 0)
        {
            Strings = [.. Strings, SecondaryPads.Serial(slot)];
        }

        foreach ((byte id, int size) in FeatureSizes)
        {
            _features[id] = new byte[size];
            _features[id][0] = id;
        }

        byte[] calibration = identity?.Calibration is { Length: >= 35 } suppliedCalibration
            ? suppliedCalibration : VirtualDualSense.DefaultCalibration();
        // Over USB the DS4 0x02 gyro ranges are axis-paired like DualSense 0x05, so the bytes copy straight.
        calibration.AsSpan(1, 34).CopyTo(_features[0x02].AsSpan(1));

        byte[] pairing = identity?.Pairing is { Length: >= 7 } suppliedPairing
            ? suppliedPairing : VirtualDualSense.DefaultPairing(slot);
        // The DualSense USB pairing report already stores MAC bytes in reverse order.
        pairing.AsSpan(1, 6).CopyTo(_features[0x12].AsSpan(1));
        pairing.AsSpan(1, 6).CopyTo(_features[0x81].AsSpan(1));
        _features[0x12][7] = 0x08;
        _features[0x12][8] = 0x25;
        if (pairing.Length >= 16)
        {
            pairing.AsSpan(10, 6).CopyTo(_features[0x12].AsSpan(10));
        }

        byte[] version = _features[0xA3];
        "Jun  2 2018"u8.CopyTo(version.AsSpan(1));
        "12:00:00"u8.CopyTo(version.AsSpan(17));
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(35), 0x0100);
        BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(41), 0x00000100);
    }

    public override VirtualKind Kind => VirtualKind.DualShock4;
    public override int Speed => SpeedHigh;
    public override byte[] DeviceDescriptor => Strings.Length == 3 ? DeviceWithSerial : Device;
    public override byte[] ConfigDescriptor => Config;
    public override int InEndpoint => 4;
    public override int OutEndpoint => 3;
    public override int IntervalMs => 4;
    protected override string[] Strings { get; } = ["Sony Interactive Entertainment", "Wireless Controller"];

    protected override byte[] BuildInput(InputState state)
    {
        byte[] report = new byte[InputLength];
        WriteInput(state, 0, report);
        return report;
    }

    protected override byte[] ReadInput(byte[] report)
    {
        byte[] frame = (byte[])report.Clone();
        WriteSequence(frame, unchecked(_sequence++));
        return frame;
    }

    internal static void WriteInput(InputState s, byte sequence, Span<byte> report)
    {
        report[..InputLength].Clear();
        report[0] = 0x01;
        report[1] = s.Left.X;
        report[2] = s.Left.Y;
        report[3] = s.Right.X;
        report[4] = s.Right.Y;
        // The button enum's first 14 bits match the three DS4 button fields.
        uint buttons = (uint)s.Buttons;
        report[5] = (byte)(((buttons & 0x0F) << 4) | VirtualDualSense.Hat(s.Buttons));
        report[6] = (byte)(buttons >> 4);
        report[7] = (byte)((buttons >> 12) & 0x03);
        report[8] = s.L2;
        report[9] = s.R2;
        BinaryPrimitives.WriteUInt16LittleEndian(report[10..], (ushort)(s.SensorTimestamp >> 4));
        report[12] = 0x14;
        VirtualDualSense.WriteVector(report[13..], s.GyroRaw);
        VirtualDualSense.WriteVector(report[19..], s.AccelRaw);

        // With the cable bit a level of 12..15 is the DualShock 4's own error indication.
        int level = s.Battery.Charge switch
        {
            ChargeState.Full => 11,
            ChargeState.Error => 15,
            _ => Math.Clamp(s.Battery.Percent / 10, 0, 10),
        };
        report[30] = (byte)(level | 0x10 | (s.HeadphoneConnected ? 0x20 : 0) | (s.MicConnected ? 0x40 : 0));
        report[33] = 1;
        WriteSequence(report, sequence);
        WriteTouch(report[35..39], s.Touch1);
        WriteTouch(report[39..43], s.Touch2.Active ? s.Touch2 : default);
        report[44] = report[48] = report[53] = report[57] = 0x80;
    }

    private static void WriteSequence(Span<byte> report, byte sequence)
    {
        report[7] = (byte)((report[7] & 0x03) | ((sequence & 0x3F) << 2));
        report[34] = sequence;
    }

    private static void WriteTouch(Span<byte> report, TouchPoint touch) =>
        VirtualDualSense.WriteTouch(report, touch with
        {
            X = Math.Min(touch.X, (ushort)1919),
            Y = Math.Min(touch.Y, (ushort)941),
        });

    public override void Out(ReadOnlySpan<byte> data, long timestamp)
    {
        if (data.Length is >= OutputLength and <= 64 && data[0] == 0x05)
        {
            Output(data[1..OutputLength], timestamp);
        }
    }

    protected override byte[]? ClassDescriptor(SetupPacket s) => (s.Value >> 8, s.Index) switch
    {
        (0x21, 0) => Config[18..27],
        (0x22, 0) => ReportDescriptor,
        _ => null,
    };

    protected override ControlResult ClassOrVendor(SetupPacket s, ReadOnlySpan<byte> data, long timestamp)
    {
        if (s.Type != 1 || s.Index != 0)
        {
            return ControlResult.Stall;
        }

        switch (s.Request, s.Value >> 8, (byte)s.Value)
        {
            case (0x01, 3, byte id) when _features.TryGetValue(id, out byte[]? feature):
                return ControlResult.Ok(feature);
            case (0x01, 1, 0x01):
                return ControlResult.Ok(CurrentInput());
            case (0x09, 2, 0x05):
                if (data.Length == OutputLength - 1)
                {
                    Output(data, timestamp);
                }
                else
                {
                    Out(data, timestamp);
                }

                return ControlResult.Ok();
            case (0x09, 3, byte id) when FeatureSizes.ContainsKey(id):
                return ControlResult.Ok();
            case (0x02, _, _):
                return ControlResult.Ok([0]);
            case (0x03, _, _):
                return ControlResult.Ok([1]);
            case (0x0A or 0x0B, _, _):
                return ControlResult.Ok();
            default:
                return ControlResult.Stall;
        }
    }

    private void Output(ReadOnlySpan<byte> block, long timestamp)
    {
        byte flags = block[0];
        Raise(new DualShock4Feedback(timestamp, (flags & 0x01) != 0, block[4], block[3],
            (flags & 0x02) != 0, new Rgb(block[5], block[6], block[7])));
    }

    private static Dictionary<byte, int> ReadFeatureSizes()
    {
        var sizes = new Dictionary<byte, int>();
        byte id = 0;
        int count = 0;
        for (int offset = 0; offset < ReportDescriptor.Length;)
        {
            byte item = ReportDescriptor[offset];
            switch (item)
            {
                case 0x85: id = ReportDescriptor[offset + 1]; break;
                case 0x95: count = ReportDescriptor[offset + 1]; break;
                case 0xB1: sizes.Add(id, count + 1); break;
            }

            int size = item & 0x03;
            offset += 1 + (size == 3 ? 4 : size);
        }

        return sizes;
    }
}
