using System.Buffers.Binary;
using System.Text;
using Tactile.Drivers;

namespace Tactile.Tests.Drivers;

public class VhciLayoutTests
{
    private static uint CtlCode(uint deviceType, uint function, uint access) => (deviceType << 16) | (access << 14) | (function << 2);

    [Fact]
    public void Ioctl_codes_follow_ctl_code()
    {
        Assert.Equal(VhciLayout.IoctlPluginHardware, CtlCode(0x22, 0x800, 3));
        Assert.Equal(VhciLayout.IoctlPlugoutHardware, CtlCode(0x22, 0x801, 3));
        Assert.Equal(VhciLayout.IoctlGetImportedDevices, CtlCode(0x22, 0x802, 3));
        Assert.Equal(VhciLayout.IoctlStopAttachAttempts, CtlCode(0x22, 0x805, 3));
        Assert.Equal(0x0022E000u, VhciLayout.IoctlPluginHardware);
        Assert.Equal(0x0022E014u, VhciLayout.IoctlStopAttachAttempts);
    }

    [Fact]
    public void Plugin_hardware_is_1124_bytes_with_0981_offsets()
    {
        byte[] b = VhciLayout.Plugin("127.0.0.1", "50123", "1-1");

        Assert.Equal(1124, b.Length);
        Assert.Equal(1124u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)));   // port, out
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8))); // location_hash, out
        AssertString(b, 12, 32, "1-1");
        AssertString(b, 44, 32, "50123");
        AssertString(b, 76, 1025, "127.0.0.1");
        Assert.All(b[1101..1104], x => Assert.Equal(0, x)); // padding
        Assert.All(b[1104..1120], x => Assert.Equal(0, x)); // serial: keep the device's own
        Assert.Equal(0, b[1120]);                              // wsk_events default
        Assert.All(b[1121..], x => Assert.Equal(0, x));
    }

    [Fact]
    public void Plugin_port_is_read_from_offset_4()
    {
        byte[] b = VhciLayout.Plugin("127.0.0.1", "1", "1-1");
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 31);
        Assert.Equal(31, VhciLayout.PluginPort(b));
    }

    [Fact]
    public void Plugout_is_8_bytes()
    {
        byte[] b = VhciLayout.Plugout(7);
        Assert.Equal([8, 0, 0, 0, 7, 0, 0, 0], b);
    }

    [Fact]
    public void Stop_attach_attempts_is_1108_bytes_with_location_at_4()
    {
        byte[] b = VhciLayout.StopAttachAttempts("127.0.0.1", "50123", "2-1");

        Assert.Equal(1108, b.Length);
        Assert.Equal(1108u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        AssertString(b, 12, 32, "2-1");
        AssertString(b, 44, 32, "50123");
        AssertString(b, 76, 1025, "127.0.0.1");
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1104), 3);
        Assert.Equal(3, VhciLayout.StopCountOf(b));
    }

    [Fact]
    public void Stop_attach_attempts_never_builds_the_stop_all_form()
    {
        Assert.Throws<ArgumentException>(() => VhciLayout.StopAttachAttempts("", "", ""));
        Assert.Throws<ArgumentException>(() => VhciLayout.StopAttachAttempts("127.0.0.1", "1", ""));
    }

    [Fact]
    public void Strings_must_leave_room_for_the_terminator()
    {
        Assert.Throws<ArgumentException>(() => VhciLayout.Plugin("127.0.0.1", "1", new string('a', 32)));
        Assert.Equal(1124, VhciLayout.Plugin("127.0.0.1", "1", new string('a', 31)).Length);
    }

    [Fact]
    public void Imported_devices_request_and_parse()
    {
        byte[] b = VhciLayout.ImportedRequest(2);
        Assert.Equal(4 + (2 * 1132), b.Length);
        Assert.Equal(1136u, BinaryPrimitives.ReadUInt32LittleEndian(b));

        WriteDevice(b, 0, port: 3, busId: "1-1", service: "50123", host: "127.0.0.1", devid: 0x10001, vendor: 0x054C, product: 0x0CE6);
        WriteDevice(b, 1, port: 31, busId: "3-2", service: "3240", host: "10.0.0.2", devid: 0x30002, vendor: 0x045E, product: 0x028E);

        List<ImportedDevice> list = VhciLayout.ParseImported(b, b.Length);
        Assert.Equal(2, list.Count);
        Assert.Equal(new ImportedDevice(3, "1-1", "50123", "127.0.0.1", 0x10001, 0x054C, 0x0CE6), list[0]);
        Assert.Equal(new ImportedDevice(31, "3-2", "3240", "10.0.0.2", 0x30002, 0x045E, 0x028E), list[1]);

        Assert.Single(VhciLayout.ParseImported(b, 4 + 1132));
        Assert.Empty(VhciLayout.ParseImported(b, 4));
    }

    private static void WriteDevice(byte[] b, int index, int port, string busId, string service, string host, uint devid, ushort vendor, ushort product)
    {
        Span<byte> d = b.AsSpan(4 + (index * 1132), 1132);
        BinaryPrimitives.WriteInt32LittleEndian(d, port);
        Encoding.UTF8.GetBytes(busId, d[8..]);
        Encoding.UTF8.GetBytes(service, d[40..]);
        Encoding.UTF8.GetBytes(host, d[72..]);
        BinaryPrimitives.WriteUInt32LittleEndian(d[1100..], devid);
        BinaryPrimitives.WriteUInt16LittleEndian(d[1108..], vendor);
        BinaryPrimitives.WriteUInt16LittleEndian(d[1110..], product);
    }

    private static void AssertString(byte[] b, int offset, int size, string expected)
    {
        byte[] want = Encoding.UTF8.GetBytes(expected);
        Assert.Equal(want, b[offset..(offset + want.Length)]);
        Assert.All(b[(offset + want.Length)..(offset + size)], x => Assert.Equal(0, x));
    }
}
