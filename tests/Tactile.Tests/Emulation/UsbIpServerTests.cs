using System.Buffers.Binary;
using System.Diagnostics;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class UsbIpServerTests
{
    private static readonly InputState State = new(
        Buttons.Circle, new Stick(1, 2), new Stick(3, 4), 5, 6, default, default, 0,
        new TouchPoint(false, 0, 0, 0), new TouchPoint(false, 0, 0, 0), new Battery(100, ChargeState.Full), false, false, false);

    private static void WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "timed out");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Server_listens_on_loopback_with_ephemeral_port()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        Assert.NotEqual(0, server.Port);
        using var client = new FakeUsbIpClient(server.Port);
    }

    [Fact]
    public void Full_dualsense_session_over_loopback()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        var log = new List<string>();
        ((UsbIpServer)server).Log = s => { lock (log) log.Add(s); };
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);
        PadFeedback? feedback = null;
        pad.Feedback += f => Volatile.Write(ref feedback, f);
        using var client = new FakeUsbIpClient(server.Port);

        byte[] import = client.Import(pad.BusId);
        Assert.Equal(320, import.Length);
        Assert.Equal(pad.BusId, UsbIpCodec.ReadBusId(import.AsSpan(8 + 0x100)));
        WaitFor(() => pad.Imported);

        var (h, device) = client.Control(new SetupPacket(0x80, 0x06, 0x0100, 0, 64));
        Assert.Equal(0, h.Field14);
        Assert.Equal(18, h.Length);
        Assert.Equal([0x12, 0x01], device[..2]);

        (h, byte[] config) = client.Control(new SetupPacket(0x80, 0x06, 0x0200, 0, 9));
        Assert.Equal(9, config.Length);
        Assert.Equal(41, BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(2)));

        (h, _) = client.Control(new SetupPacket(0x00, 0x09, 0x0001, 0, 0));
        Assert.Equal(0, h.Field14);

        (h, _) = client.Control(new SetupPacket(0x80, 0x06, 0x0F00, 0, 5));
        Assert.Equal(UsbIpCodec.EPipe, h.Field14);
        Assert.Equal(0, h.Length);

        pad.Submit(State);
        uint inSeq = client.SendSubmit(4, true, 64);
        var (ret, input) = client.ReadReply(true);
        Assert.Equal(inSeq, ret.SeqNum);
        Assert.Equal(0, ret.Field14);
        Assert.Equal(64, input.Length);
        Assert.Equal([0x01, 1, 2, 3, 4, 5, 6], input[..7]);
        Assert.Equal(0x48, input[8]);   // hat released, circle

        byte[] output = new byte[48];
        output[0] = 0x02;
        output[47] = 0x99;
        uint outSeq = client.SendSubmit(3, false, output.Length, payload: output);
        (ret, _) = client.ReadReply(false);
        Assert.Equal(outSeq, ret.SeqNum);
        Assert.Equal(48, ret.Length);
        WaitFor(() => Volatile.Read(ref feedback) is not null);
        var f = Assert.IsType<DualSenseOutputFeedback>(feedback);
        Assert.Equal(0x99, f.State[46]);
        Assert.InRange(f.Timestamp, 1, Stopwatch.GetTimestamp());

        // The pacer may answer the IN URB before the unlink arrives; either outcome must be consistent.
        uint pending = client.SendSubmit(4, true, 64);
        uint unlinkSeq = client.SendUnlink(pending);
        var first = client.ReadReply(true);
        if (first.Header.Command == UsbIpCodec.RetUnlink)
        {
            Assert.Equal(unlinkSeq, first.Header.SeqNum);
            Assert.Equal(UsbIpCodec.EConnReset, first.Header.Field14);
        }
        else
        {
            Assert.Equal(pending, first.Header.SeqNum);
            var second = client.ReadReply(true);
            Assert.Equal(UsbIpCodec.RetUnlink, second.Header.Command);
            Assert.Equal(unlinkSeq, second.Header.SeqNum);
            Assert.Equal(0, second.Header.Field14);
        }

        lock (log)
        {
            Assert.Contains(log, l => l.Contains("setup 80 06 0100"));
        }
    }

    [Fact]
    public void Full_dualshock4_session_over_loopback()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualShock4, null);
        PadFeedback? feedback = null;
        pad.Feedback += f => Volatile.Write(ref feedback, f);
        using var client = new FakeUsbIpClient(server.Port);

        byte[] import = client.Import(pad.BusId);
        Assert.Equal(320, import.Length);
        Assert.Equal([0x05, 0x4C, 0x09, 0xCC], import[(8 + 0x12C)..(8 + 0x130)]);
        WaitFor(() => pad.Imported);

        var (header, device) = client.Control(new SetupPacket(0x80, 0x06, 0x0100, 0, 18));
        Assert.Equal(0, header.Field14);
        Assert.Equal([0x4C, 0x05, 0xCC, 0x09], device[8..12]);
        (header, byte[] config) = client.Control(new SetupPacket(0x80, 0x06, 0x0200, 0, 41));
        Assert.Equal(0, header.Field14);
        Assert.Equal(41, config.Length);
        ushort reportLength = BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(25));
        (header, byte[] descriptor) = client.Control(new SetupPacket(0x81, 0x06, 0x2200, 0, reportLength));
        Assert.Equal(0, header.Field14);
        Assert.Equal(reportLength, descriptor.Length);
        (header, _) = client.Control(new SetupPacket(0x00, 0x09, 1, 0, 0));
        Assert.Equal(0, header.Field14);
        (header, byte[] calibration) = client.Control(new SetupPacket(0xA1, 0x01, 0x0302, 0, 37));
        Assert.Equal(0, header.Field14);
        Assert.Equal(37, calibration.Length);
        Assert.Equal(0x02, calibration[0]);
        Assert.NotEqual(0, BinaryPrimitives.ReadInt16LittleEndian(calibration.AsSpan(7)));

        pad.Submit(State);
        uint inputSequence = client.SendSubmit(4, true, 64);
        (header, byte[] input) = client.ReadReply(true);
        Assert.Equal(inputSequence, header.SeqNum);
        Assert.Equal(0, header.Field14);
        Assert.Equal(64, input.Length);
        Assert.Equal([1, 1, 2, 3, 4, 0x48, 0, 0, 5, 6], input[..10]);
        Assert.Equal(0, input[34]);
        client.SendSubmit(4, true, 64);
        (_, byte[] repeat) = client.ReadReply(true);
        Assert.Equal(4, repeat[7]);
        Assert.Equal(1, repeat[34]);

        byte[] output = [0x05, 3, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, .. new byte[23]];
        uint outputSequence = client.SendSubmit(3, false, 32, payload: output);
        (header, _) = client.ReadReply(false);
        Assert.Equal(outputSequence, header.SeqNum);
        Assert.Equal(0, header.Field14);
        Assert.Equal(32, header.Length);
        WaitFor(() => Volatile.Read(ref feedback) is not null);
        var actual = Assert.IsType<DualShock4Feedback>(feedback);
        Assert.Equal(new DualShock4Feedback(actual.Timestamp, true, 0x20, 0x10, true, new Rgb(0x30, 0x40, 0x50)), actual);
    }

    [Fact]
    public void Interval_repeats_current_state_without_new_input()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);
        using var client = new FakeUsbIpClient(server.Port);
        client.Import(pad.BusId);
        pad.Submit(State);

        client.SendSubmit(4, true, 64);
        client.ReadReply(true);
        uint seq = client.SendSubmit(4, true, 64);
        var (h, data) = client.ReadReply(true);

        Assert.Equal(seq, h.SeqNum);
        Assert.Equal(1, data[1]);
    }

    [Fact]
    public void Xbox360_session_raises_rumble_from_interrupt_out()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.Xbox360, null);
        PadFeedback? feedback = null;
        pad.Feedback += f => Volatile.Write(ref feedback, f);
        using var client = new FakeUsbIpClient(server.Port);
        client.Import(pad.BusId);

        client.SendSubmit(1, false, 8, payload: [0x00, 0x08, 0x00, 0xFF, 0x10, 0x00, 0x00, 0x00]);
        var (h, _) = client.ReadReply(false);
        Assert.Equal(0, h.Field14);
        WaitFor(() => Volatile.Read(ref feedback) is not null);
        Assert.Equal(new RumbleFeedback(feedback!.Timestamp, 0xFF, 0x10), feedback);

        client.SendSubmit(1, true, 32);
        (h, byte[] first) = client.ReadReply(true);
        Assert.Equal([0x01, 0x03, 0x0E], first);
    }

    [Fact]
    public void Unknown_busid_and_double_import_are_refused()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);

        using (var c = new FakeUsbIpClient(server.Port))
        {
            byte[] r = c.Import("9-9");
            Assert.Equal(8, r.Length);
            Assert.Equal(4u, UsbIpCodec.ReadOpCommon(r).Status);
            Assert.True(c.ReadsEof());
        }

        using var first = new FakeUsbIpClient(server.Port);
        Assert.Equal(320, first.Import(pad.BusId).Length);
        using var second = new FakeUsbIpClient(server.Port);
        Assert.Equal(2u, UsbIpCodec.ReadOpCommon(second.Import(pad.BusId)).Status);
    }

    [Fact]
    public void Devlist_reports_every_pad_and_closes()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        server.Create(VirtualKind.DualSense, null);
        server.Create(VirtualKind.Xbox360, null);
        using var c = new FakeUsbIpClient(server.Port);
        byte[] req = new byte[8];
        UsbIpCodec.WriteOpCommon(req, UsbIpCodec.OpReqDevList, 0);
        c.Stream.Write(req);

        byte[] head = c.Read(12);
        Assert.Equal(UsbIpCodec.OpRepDevList, UsbIpCodec.ReadOpCommon(head).Code);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8)));
        byte[] ds = c.Read(312 + 4);
        Assert.Equal("1-1", UsbIpCodec.ReadBusId(ds.AsSpan(0x100)));
        Assert.Equal([0x03, 0x00, 0x00, 0x00], ds[312..]);
        byte[] x = c.Read(312 + 4);
        Assert.Equal("2-1", UsbIpCodec.ReadBusId(x.AsSpan(0x100)));
        Assert.Equal(0x045E, BinaryPrimitives.ReadUInt16BigEndian(x.AsSpan(0x12C)));
        Assert.True(c.ReadsEof());
    }

    [Theory]
    [InlineData(0x7FFFFFFF)]
    [InlineData(-5)]
    public void Oversized_or_negative_length_closes_only_that_connection(int length)
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);
        using (var c = new FakeUsbIpClient(server.Port))
        {
            c.Import(pad.BusId);
            c.SendSubmit(3, false, length);
            Assert.True(c.ReadsEof());
        }

        WaitFor(() => !pad.Imported);
        using var again = new FakeUsbIpClient(server.Port);
        Assert.Equal(320, again.Import(pad.BusId).Length);
    }

    [Fact]
    public void Unknown_command_and_bad_version_close_the_connection()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);
        using (var c = new FakeUsbIpClient(server.Port))
        {
            c.Import(pad.BusId);
            byte[] junk = new byte[48];
            junk[3] = 0x99;
            c.Stream.Write(junk);
            Assert.True(c.ReadsEof());
        }

        using (var c = new FakeUsbIpClient(server.Port))
        {
            c.Stream.Write([0x01, 0x06, 0x80, 0x03, 0, 0, 0, 0]);
            Assert.True(c.ReadsEof());
        }

        using (var c = new FakeUsbIpClient(server.Port))
        {
            c.Stream.Write([0x01, 0x11, 0x12, 0x34, 0, 0, 0, 0]);
            Assert.True(c.ReadsEof());
        }

        WaitFor(() => !pad.Imported);
    }

    [Fact]
    public void Isochronous_and_unknown_endpoints_get_an_error_status()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);
        using var c = new FakeUsbIpClient(server.Port);
        c.Import(pad.BusId);

        uint seq = c.SendSubmit(1, false, 4, payload: [1, 2, 3, 4, .. new byte[2 * 16]], packets: 2);
        var (h, _) = c.ReadReply(false);
        Assert.Equal(seq, h.SeqNum);
        Assert.Equal(UsbIpCodec.EInval, h.Field14);

        seq = c.SendSubmit(2, true, 64);
        (h, _) = c.ReadReply(true);
        Assert.Equal(seq, h.SeqNum);
        Assert.Equal(UsbIpCodec.EInval, h.Field14);
        Assert.Equal(0, h.Length);

        // Still usable afterwards.
        (h, _) = c.Control(new SetupPacket(0x80, 0x06, 0x0100, 0, 18));
        Assert.Equal(18, h.Length);
    }

    [Fact]
    public void Removing_a_pad_drops_its_connection_and_server_keeps_running()
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        IVirtualPad pad = server.Create(VirtualKind.DualSense, null);
        using var c = new FakeUsbIpClient(server.Port);
        c.Import(pad.BusId);
        WaitFor(() => pad.Imported);

        pad.Dispose();
        Assert.True(c.ReadsEof());
        Assert.False(pad.Imported);

        IVirtualPad next = server.Create(VirtualKind.Xbox360, null);
        Assert.Equal("1-1", next.BusId);
        using var c2 = new FakeUsbIpClient(server.Port);
        Assert.Equal(320, c2.Import(next.BusId).Length);
    }
}
