using System.Text;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public sealed class SecondaryPadsTests
{
    [Theory]
    [InlineData(VirtualKind.DualSense)]
    [InlineData(VirtualKind.Xbox360)]
    public void Secondary_slots_export_distinct_serials_and_gamepad_only_interfaces(VirtualKind kind)
    {
        using IUsbIpServer server = UsbIpServerFactory.Start(0);
        using IVirtualPad primary = server.CreateDualSense(null, audio: true);
        using IVirtualPad first = server.CreateSecondaryPad(1, kind, null);
        using IVirtualPad second = server.CreateSecondaryPad(2, kind, null);
        using var primaryClient = new FakeUsbIpClient(server.Port);
        using var firstClient = new FakeUsbIpClient(server.Port);
        using var secondClient = new FakeUsbIpClient(server.Port);

        Assert.Equal(3, new[] { primary.BusId, first.BusId, second.BusId }.Distinct().Count());
        Assert.Equal(320, primaryClient.Import(primary.BusId).Length);
        Assert.Equal(320, firstClient.Import(first.BusId).Length);
        Assert.Equal(320, secondClient.Import(second.BusId).Length);
        Assert.IsAssignableFrom<IVirtualAudio>(primary);
        Assert.Equal(4, InterfaceCount(primaryClient));
        Assert.False(first is IVirtualAudio);
        Assert.False(second is IVirtualAudio);
        Assert.Equal(1, InterfaceCount(firstClient));
        Assert.Equal(1, InterfaceCount(secondClient));
        string serial = ReadSerial(firstClient);
        Assert.NotEqual(serial, ReadSerial(secondClient));

        server.Remove(first);
        using IVirtualPad replacement = server.CreateSecondaryPad(1, kind, null);
        using var replacementClient = new FakeUsbIpClient(server.Port);
        using var staleClient = new FakeUsbIpClient(server.Port);
        Assert.NotEqual(first.BusId, replacement.BusId);
        Assert.Equal((uint)UsbIpCodec.StatusNoSuchBusId, UsbIpCodec.ReadOpCommon(staleClient.Import(first.BusId)).Status);
        Assert.Equal(320, replacementClient.Import(replacement.BusId).Length);
        Assert.Equal(serial, ReadSerial(replacementClient));
        Assert.True(primary.Imported && second.Imported);
    }

    private static byte InterfaceCount(FakeUsbIpClient client)
    {
        var (header, config) = client.Control(new SetupPacket(0x80, 0x06, 0x0200, 0, 255));
        Assert.Equal(0, header.Field14);
        return config[4];
    }

    private static string ReadSerial(FakeUsbIpClient client)
    {
        var (header, device) = client.Control(new SetupPacket(0x80, 0x06, 0x0100, 0, 18));
        Assert.Equal(0, header.Field14);
        Assert.NotEqual(0, device[16]);
        var (serialHeader, serial) = client.Control(new SetupPacket(0x80, 0x06, (ushort)(0x0300 | device[16]), 0x0409, 64));
        Assert.Equal(0, serialHeader.Field14);
        return Encoding.Unicode.GetString(serial.AsSpan(2));
    }
}
