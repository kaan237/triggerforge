using System.Buffers.Binary;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class UsbIpCodecTests
{
    [Fact]
    public void Submit_header_round_trips_with_setup_in_usb_byte_order()
    {
        var setup = new SetupPacket(0x80, 0x06, 0x0100, 0x0000, 18);
        var h = new UrbHeader(UsbIpCodec.CmdSubmit, 0x0000_0003, 0x0001_0001, 1, 0, 0x200, 18, 0, UsbIpCodec.NonIso, 0, UsbIpCodec.PackSetup(setup));
        byte[] b = new byte[UsbIpCodec.HeaderSize];
        UsbIpCodec.WriteHeader(b, h);

        Assert.Equal(h, UsbIpCodec.ReadHeader(b));
        Assert.Equal(setup, UsbIpCodec.ReadHeader(b).SetupPacket);
        Assert.Equal([0x00, 0x00, 0x00, 0x01], b[0..4]);              // big-endian command
        Assert.Equal([0x00, 0x00, 0x00, 0x12], b[0x18..0x1C]);        // big-endian length
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], b[0x20..0x24]);        // non-ISO packets
        Assert.Equal([0x80, 0x06, 0x00, 0x01, 0x00, 0x00, 0x12, 0x00], b[0x28..0x30]);   // raw setup
    }

    [Fact]
    public void Ret_submit_echoes_seqnum_and_zeroes_devid_direction_ep()
    {
        byte[] b = new byte[UsbIpCodec.HeaderSize];
        UsbIpCodec.WriteHeader(b, UsbIpCodec.RetSubmitHeader(0xDEADBEEF, UsbIpCodec.EPipe, 0));
        UrbHeader h = UsbIpCodec.ReadHeader(b);

        Assert.Equal(UsbIpCodec.RetSubmit, h.Command);
        Assert.Equal(0xDEADBEEFu, h.SeqNum);
        Assert.Equal(0u, h.DevId);
        Assert.Equal(0u, h.Direction);
        Assert.Equal(0u, h.Endpoint);
        Assert.Equal(-32, h.Field14);
        Assert.Equal(UsbIpCodec.NonIso, h.Packets);
    }

    [Fact]
    public void Op_common_and_busid_round_trip()
    {
        byte[] b = new byte[40];
        UsbIpCodec.WriteOpCommon(b, UsbIpCodec.OpReqImport, 0);
        UsbIpCodec.WriteBusId(b.AsSpan(8), "2-1");

        Assert.Equal([0x01, 0x11, 0x80, 0x03, 0, 0, 0, 0], b[..8]);
        Assert.Equal((UsbIpCodec.Version, UsbIpCodec.OpReqImport, 0u), UsbIpCodec.ReadOpCommon(b));
        Assert.Equal("2-1", UsbIpCodec.ReadBusId(b.AsSpan(8)));
    }

    [Fact]
    public void Busid_without_terminator_is_rejected()
    {
        byte[] b = Enumerable.Repeat((byte)'a', 32).ToArray();
        Assert.Null(UsbIpCodec.ReadBusId(b));
    }

    [Fact]
    public void Device_record_takes_ids_from_descriptors()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var pad = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);
        byte[] r = new byte[UsbIpCodec.DeviceRecordSize];
        UsbIpCodec.WriteDeviceRecord(r, pad.BusId, pad.BusNum, 1, pad.Speed, pad.DeviceDescriptor, pad.ConfigDescriptor);

        Assert.All(r[..0x100], x => Assert.Equal(0, x));
        Assert.Equal("1-1", UsbIpCodec.ReadBusId(r.AsSpan(0x100)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(0x120)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(0x124)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(0x128)));
        Assert.Equal(0x054C, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(0x12C)));
        Assert.Equal(0x0CE6, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(0x12E)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(0x130)));
        Assert.Equal(1, r[0x135]);
        Assert.Equal(1, r[0x136]);
        Assert.Equal(1, r[0x137]);
    }

    [Fact]
    public void Descriptor_lengths_match_their_headers()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var ds = (VirtualUsbDevice)server.Create(VirtualKind.DualSense, null);
        var x = (VirtualUsbDevice)server.Create(VirtualKind.Xbox360, null);

        Assert.Equal(273, VirtualDualSense.ReportDescriptorLength);
        foreach (VirtualUsbDevice d in new[] { ds, x })
        {
            Assert.Equal(18, d.DeviceDescriptor.Length);
            Assert.Equal(d.ConfigDescriptor.Length, BinaryPrimitives.ReadUInt16LittleEndian(d.ConfigDescriptor.AsSpan(2)));
        }

        Assert.Equal(41, ds.ConfigDescriptor.Length);
        Assert.Equal(49, x.ConfigDescriptor.Length);
        Assert.Equal([(0xFF, 0x5D, 0x01)], UsbIpCodec.Interfaces(x.ConfigDescriptor).Select(i => ((int)i.Class, (int)i.SubClass, (int)i.Protocol)));
        Assert.Equal("2-1", x.BusId);
    }
}
