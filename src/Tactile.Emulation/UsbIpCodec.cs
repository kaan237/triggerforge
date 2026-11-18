using System.Buffers.Binary;
using System.Text;

namespace Tactile.Emulation;

/// <summary>USB setup packet (raw USB byte order, little-endian fields).</summary>
internal readonly record struct SetupPacket(byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    public bool In => (RequestType & 0x80) != 0;
    public int Type => (RequestType >> 5) & 3;        // 0 standard, 1 class, 2 vendor
    public int Recipient => RequestType & 0x1F;       // 0 device, 1 interface, 2 endpoint

    public static SetupPacket Parse(ReadOnlySpan<byte> b) => new(
        b[0], b[1],
        BinaryPrimitives.ReadUInt16LittleEndian(b[2..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[6..]));

    public void Write(Span<byte> b)
    {
        b[0] = RequestType;
        b[1] = Request;
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], Value);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], Index);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], Length);
    }

    public override string ToString() => $"{RequestType:X2} {Request:X2} {Value:X4} {Index:X4} {Length}";
}

/// <summary>
/// The 48-byte URB header in both directions. For CMD_UNLINK, <see cref="Field14"/> is the seqnum to cancel;
/// for RET_SUBMIT/RET_UNLINK it is the status. Layout: docs/protocol-emulation.md §1.4.
/// </summary>
internal readonly record struct UrbHeader(
    uint Command,
    uint SeqNum,
    uint DevId,
    uint Direction,
    uint Endpoint,
    int Field14,
    int Length,
    int StartFrame,
    int Packets,
    int Field24,
    ulong Setup)
{
    public SetupPacket SetupPacket
    {
        get
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, Setup);
            return SetupPacket.Parse(b);
        }
    }
}

internal static class UsbIpCodec
{
    public const ushort Version = 0x0111;
    public const ushort OpReqDevList = 0x8005;
    public const ushort OpRepDevList = 0x0005;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;

    public const uint CmdSubmit = 1;
    public const uint CmdUnlink = 2;
    public const uint RetSubmit = 3;
    public const uint RetUnlink = 4;

    public const int OpCommonSize = 8;
    public const int BusIdSize = 32;
    public const int DeviceRecordSize = 312;
    public const int HeaderSize = 48;
    public const int IsoDescriptorSize = 16;
    public const int NonIso = -1;   // 0xFFFFFFFF

    public const int StatusOk = 0;
    public const int StatusBusy = 2;
    public const int StatusNoSuchBusId = 4;

    public const int EPipe = -32;
    public const int EInval = -22;
    public const int EConnReset = -104;

    public static void WriteOpCommon(Span<byte> b, ushort code, uint status)
    {
        BinaryPrimitives.WriteUInt16BigEndian(b, Version);
        BinaryPrimitives.WriteUInt16BigEndian(b[2..], code);
        BinaryPrimitives.WriteUInt32BigEndian(b[4..], status);
    }

    public static (ushort Version, ushort Code, uint Status) ReadOpCommon(ReadOnlySpan<byte> b) => (
        BinaryPrimitives.ReadUInt16BigEndian(b),
        BinaryPrimitives.ReadUInt16BigEndian(b[2..]),
        BinaryPrimitives.ReadUInt32BigEndian(b[4..]));

    /// <summary>NUL-terminated busid, or null if it has no terminator within 32 bytes.</summary>
    public static string? ReadBusId(ReadOnlySpan<byte> b)
    {
        int end = b[..BusIdSize].IndexOf((byte)0);
        return end < 0 ? null : Encoding.ASCII.GetString(b[..end]);
    }

    public static void WriteBusId(Span<byte> b, string busId)
    {
        b[..BusIdSize].Clear();
        Encoding.ASCII.GetBytes(busId.AsSpan(0, Math.Min(busId.Length, BusIdSize - 1)), b);
    }

    /// <summary>The 312-byte usbip_usb_device record, fields taken from the descriptors.</summary>
    public static void WriteDeviceRecord(Span<byte> b, string busId, int busNum, int devNum, int speed, ReadOnlySpan<byte> device, ReadOnlySpan<byte> config)
    {
        b[..DeviceRecordSize].Clear();
        WriteBusId(b[0x100..], busId);
        BinaryPrimitives.WriteInt32BigEndian(b[0x120..], busNum);
        BinaryPrimitives.WriteInt32BigEndian(b[0x124..], devNum);
        BinaryPrimitives.WriteInt32BigEndian(b[0x128..], speed);
        BinaryPrimitives.WriteUInt16BigEndian(b[0x12C..], BinaryPrimitives.ReadUInt16LittleEndian(device[8..]));
        BinaryPrimitives.WriteUInt16BigEndian(b[0x12E..], BinaryPrimitives.ReadUInt16LittleEndian(device[10..]));
        BinaryPrimitives.WriteUInt16BigEndian(b[0x130..], BinaryPrimitives.ReadUInt16LittleEndian(device[12..]));
        b[0x132] = device[4];
        b[0x133] = device[5];
        b[0x134] = device[6];
        b[0x135] = config[5];
        b[0x136] = device[17];
        b[0x137] = config[4];
    }

    /// <summary>Class, subclass, protocol of every alternate-0 interface in a configuration descriptor.</summary>
    public static List<(byte Class, byte SubClass, byte Protocol)> Interfaces(ReadOnlySpan<byte> config)
    {
        var list = new List<(byte, byte, byte)>();
        for (int i = 0; i + 1 < config.Length && config[i] > 0; i += config[i])
        {
            if (config[i + 1] == 4 && i + 8 < config.Length && config[i + 3] == 0)
            {
                list.Add((config[i + 5], config[i + 6], config[i + 7]));
            }
        }

        return list;
    }

    public static UrbHeader ReadHeader(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt32BigEndian(b),
        BinaryPrimitives.ReadUInt32BigEndian(b[0x04..]),
        BinaryPrimitives.ReadUInt32BigEndian(b[0x08..]),
        BinaryPrimitives.ReadUInt32BigEndian(b[0x0C..]),
        BinaryPrimitives.ReadUInt32BigEndian(b[0x10..]),
        BinaryPrimitives.ReadInt32BigEndian(b[0x14..]),
        BinaryPrimitives.ReadInt32BigEndian(b[0x18..]),
        BinaryPrimitives.ReadInt32BigEndian(b[0x1C..]),
        BinaryPrimitives.ReadInt32BigEndian(b[0x20..]),
        BinaryPrimitives.ReadInt32BigEndian(b[0x24..]),
        BinaryPrimitives.ReadUInt64BigEndian(b[0x28..]));

    public static void WriteHeader(Span<byte> b, in UrbHeader h)
    {
        BinaryPrimitives.WriteUInt32BigEndian(b, h.Command);
        BinaryPrimitives.WriteUInt32BigEndian(b[0x04..], h.SeqNum);
        BinaryPrimitives.WriteUInt32BigEndian(b[0x08..], h.DevId);
        BinaryPrimitives.WriteUInt32BigEndian(b[0x0C..], h.Direction);
        BinaryPrimitives.WriteUInt32BigEndian(b[0x10..], h.Endpoint);
        BinaryPrimitives.WriteInt32BigEndian(b[0x14..], h.Field14);
        BinaryPrimitives.WriteInt32BigEndian(b[0x18..], h.Length);
        BinaryPrimitives.WriteInt32BigEndian(b[0x1C..], h.StartFrame);
        BinaryPrimitives.WriteInt32BigEndian(b[0x20..], h.Packets);
        BinaryPrimitives.WriteInt32BigEndian(b[0x24..], h.Field24);
        BinaryPrimitives.WriteUInt64BigEndian(b[0x28..], h.Setup);
    }

    public static UrbHeader RetSubmitHeader(uint seqNum, int status, int actualLength) =>
        new(RetSubmit, seqNum, 0, 0, 0, status, actualLength, 0, NonIso, 0, 0);

    public static UrbHeader RetUnlinkHeader(uint seqNum, int status) =>
        new(RetUnlink, seqNum, 0, 0, 0, status, 0, 0, 0, 0, 0);

    public static ulong PackSetup(SetupPacket s)
    {
        Span<byte> b = stackalloc byte[8];
        s.Write(b);
        return BinaryPrimitives.ReadUInt64BigEndian(b);
    }
}
