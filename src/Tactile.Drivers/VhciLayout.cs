using System.Buffers.Binary;
using System.Text;

namespace Tactile.Drivers;

internal sealed record ImportedDevice(int Port, string BusId, string Service, string Host, uint DevId, ushort Vendor, ushort Product);

/// <summary>usbip-win2 0.9.8.1 x64 VHCI structs (docs/protocol-emulation.md §2.2, §2.3). Pure.</summary>
internal static class VhciLayout
{
    public const string Host = "127.0.0.1";
    public static readonly Guid InterfaceGuid = new("B4030C06-DC5F-4FCC-87EB-E5515A0935C0");

    public const uint IoctlPluginHardware = 0x0022E000;
    public const uint IoctlPlugoutHardware = 0x0022E004;
    public const uint IoctlGetImportedDevices = 0x0022E008;
    public const uint IoctlStopAttachAttempts = 0x0022E014;

    public const int BusIdSize = 32;
    public const int ServiceSize = 32;
    public const int HostSize = 1025;

    // imported_device_location, relative to its own start.
    public const int LocationSize = 1100;
    public const int LocPort = 0;
    public const int LocHash = 4;
    public const int LocBusId = 8;
    public const int LocService = 40;
    public const int LocHost = 72;

    public const int PluginSize = 1124;
    public const int PluginLocation = 4;
    public const int PluginSerial = 1104;
    public const int SerialSize = 16;
    public const int PluginWskEvents = 1120;

    public const int PlugoutSize = 8;
    public const int PlugoutPort = 4;

    public const int StopSize = 1108;
    public const int StopLocation = 4;
    public const int StopCount = 1104;

    public const int ImportedDeviceSize = 1132;
    public const int ImportedHeader = 4;
    public const int ImportedRequestSize = ImportedHeader + ImportedDeviceSize;
    public const int ImportedDevId = 1100;
    public const int ImportedVendor = 1108;
    public const int ImportedProduct = 1110;

    public static byte[] Plugin(string host, string service, string busId)
    {
        var b = new byte[PluginSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, PluginSize);
        WriteLocationStrings(b.AsSpan(PluginLocation, LocationSize), host, service, busId);
        // serial (empty = keep the device's own) and wsk_events (0 = default) stay zero.
        return b;
    }

    public static int PluginPort(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b[(PluginLocation + LocPort)..]);

    public static byte[] Plugout(int hubPort)
    {
        var b = new byte[PlugoutSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, PlugoutSize);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(PlugoutPort), hubPort);
        return b;
    }

    /// <summary>All three strings must be set: empty ones would stop every pending attach on the machine.</summary>
    public static byte[] StopAttachAttempts(string host, string service, string busId)
    {
        if (host.Length == 0 || service.Length == 0 || busId.Length == 0)
            throw new ArgumentException("host, service and busid are all required.");
        var b = new byte[StopSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, StopSize);
        WriteLocationStrings(b.AsSpan(StopLocation, LocationSize), host, service, busId);
        return b;
    }

    public static int StopCountOf(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b[StopCount..]);

    /// <summary>Output buffer for GET_IMPORTED_DEVICES; its first 4 bytes are also the input.</summary>
    public static byte[] ImportedRequest(int capacity)
    {
        var b = new byte[ImportedHeader + (Math.Max(1, capacity) * ImportedDeviceSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(b, ImportedRequestSize);
        return b;
    }

    public static List<ImportedDevice> ParseImported(ReadOnlySpan<byte> b, int bytesReturned)
    {
        var list = new List<ImportedDevice>();
        int n = Math.Max(0, Math.Min(bytesReturned, b.Length) - ImportedHeader) / ImportedDeviceSize;
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> d = b.Slice(ImportedHeader + (i * ImportedDeviceSize), ImportedDeviceSize);
            list.Add(new ImportedDevice(
                BinaryPrimitives.ReadInt32LittleEndian(d[LocPort..]),
                ReadString(d.Slice(LocBusId, BusIdSize)),
                ReadString(d.Slice(LocService, ServiceSize)),
                ReadString(d.Slice(LocHost, HostSize)),
                BinaryPrimitives.ReadUInt32LittleEndian(d[ImportedDevId..]),
                BinaryPrimitives.ReadUInt16LittleEndian(d[ImportedVendor..]),
                BinaryPrimitives.ReadUInt16LittleEndian(d[ImportedProduct..])));
        }

        return list;
    }

    public static void WriteLocationStrings(Span<byte> location, string host, string service, string busId)
    {
        WriteString(location.Slice(LocBusId, BusIdSize), busId, nameof(busId));
        WriteString(location.Slice(LocService, ServiceSize), service, nameof(service));
        WriteString(location.Slice(LocHost, HostSize), host, nameof(host));
    }

    private static void WriteString(Span<byte> field, string value, string name)
    {
        int n = Encoding.UTF8.GetByteCount(value);
        if (n >= field.Length)
            throw new ArgumentException($"{name} is longer than {field.Length - 1} bytes.", name);
        field.Clear();
        Encoding.UTF8.GetBytes(value, field);
    }

    public static string ReadString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }
}
