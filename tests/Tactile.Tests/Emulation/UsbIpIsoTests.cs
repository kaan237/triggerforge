using System.Buffers.Binary;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class UsbIpIsoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Iso_submit_body_and_reply_have_payload_on_the_correct_side(bool input)
    {
        byte[] data = [1, 2, 3, 4, 5];
        byte[] descriptors = FakeUsbIpClient.IsoDescriptors((0, 2), (2, 3));
        var header = new UrbHeader(1, 0x1234, 0x10001, input ? 1u : 0u, input ? 2u : 1u, 0, 5, -17, 2, 1, 0);
        using var request = new MemoryStream(input ? descriptors : [.. data, .. descriptors]);
        IsoSubmit submit = UsbIpCodec.ReadIsoSubmit(request, header);

        Assert.Equal(request.Length, request.Position);
        Assert.Equal(input ? new byte[5] : data, submit.Buffer);
        Assert.Equal(descriptors, submit.Descriptors);
        Assert.True(UsbIpCodec.ValidIsoPackets(submit, 3));

        data.AsSpan(0, 2).CopyTo(UsbIpCodec.IsoPacketBuffer(submit, 0));
        data.AsSpan(2).CopyTo(UsbIpCodec.IsoPacketBuffer(submit, 1));
        using var reply = new MemoryStream();
        UsbIpCodec.WriteIsoReply(reply, new byte[48], submit, 0, 5);
        byte[] expected =
        [
            0, 0, 0, 3, 0, 0, 0x12, 0x34,
            .. new byte[16], 0, 0, 0, 5, 0xFF, 0xFF, 0xFF, 0xEF,
            0, 0, 0, 2, .. new byte[12],
            .. input ? data : [],
            0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 0,
            0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 3, 0, 0, 0, 0,
        ];
        Assert.Equal(expected, reply.ToArray());
    }

    [Theory]
    [InlineData(false, 1u, 392u, 392, 1)]
    [InlineData(false, 0u, 393u, 393, 1)]
    [InlineData(false, uint.MaxValue, 2u, 392, 1)]
    [InlineData(false, 0u, 0u, 0, 129)]
    [InlineData(true, 0u, 193u, 193, 1)]
    [InlineData(true, 1u, 191u, 192, 1)]
    public void Invalid_iso_submit_echoes_descriptors_without_payload(
        bool input, uint offset, uint packetLength, int bufferLength, int count)
    {
        using IUsbIpServer server = UsbIpServerFactory.Start();
        using IVirtualPad pad = server.CreateDualSense(null, audio: true);
        using var client = new FakeUsbIpClient(server.Port);
        client.Import(pad.BusId);
        byte[] descriptors = new byte[count * 16];
        FakeUsbIpClient.IsoDescriptors((offset, packetLength)).CopyTo(descriptors, 0);
        descriptors[^1] = 0xF1;

        byte[] body = input ? descriptors : [.. new byte[bufferLength], .. descriptors];
        uint seq = client.SendSubmit(input ? 2u : 1u, input, bufferLength, payload: body, packets: count, startFrame: 19);
        var (header, data) = client.ReadReply(input);

        Assert.Equal(new UrbHeader(3, seq, 0, 0, 0, -22, 0, 19, count, 0, 0), header);
        Assert.Empty(data);
        Assert.Equal(descriptors, client.Read(descriptors.Length));
        Assert.Equal(18, client.Control(new SetupPacket(0x80, 6, 0x0100, 0, 18)).Data.Length);
    }

    [Fact]
    public void Audio_pad_import_delivers_four_out_packets_and_silent_mic_packets()
    {
        const int size = 384;
        using IUsbIpServer server = UsbIpServerFactory.Start();
        using IVirtualPad pad = server.CreateDualSense(null, audio: true);
        var audio = Assert.IsAssignableFrom<IVirtualAudio>(pad);
        var samples = new System.Collections.Concurrent.ConcurrentQueue<short[]>();
        audio.Audio += packet => samples.Enqueue(packet.ToArray());
        using var client = new FakeUsbIpClient(server.Port);
        byte[] import = client.Import(pad.BusId);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(import.AsSpan(8 + 0x128)));
        Assert.Equal(4, import[8 + 0x137]);
        Assert.Equal(0, client.Control(new SetupPacket(0x01, 0x0B, 1, 1, 0)).Header.Field14);
        byte[] payload = new byte[4 * size];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(i * size), (short)(-10 - i));
        }

        byte[] descriptors = FakeUsbIpClient.IsoDescriptors((0, size), (size, size), (2 * size, size), (3 * size, size));
        uint seq = client.SendSubmit(1, false, payload.Length, payload: [.. payload, .. descriptors], packets: 4, startFrame: 381);
        var (outReply, _) = client.ReadReply(false);
        Assert.Equal(new UrbHeader(3, seq, 0, 0, 0, 0, 1536, 381, 4, 0, 0), outReply);
        byte[] outDescriptors = client.Read(4 * 16);
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(descriptors.AsSpan(i * 16 + 8), size);
        }

        Assert.Equal(descriptors, outDescriptors);
        Assert.Equal([-10, -11, -12, -13], samples.Select(s => (int)s[0]));
        Assert.All(samples, s => Assert.Equal(size / 2, s.Length));

        Assert.Equal(0, client.Control(new SetupPacket(0x01, 0x0B, 1, 2, 0)).Header.Field14);
        seq = client.SendSubmit(2, true, 384, payload: FakeUsbIpClient.IsoDescriptors((0, 192), (192, 100)),
            packets: 2, startFrame: 407);
        var (inReply, mic) = client.ReadReply(true);
        Assert.Equal(new UrbHeader(3, seq, 0, 0, 0, 0, 292, 407, 2, 0, 0), inReply);
        Assert.Equal(new byte[292], mic);
        Assert.Equal(new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 192, 0, 0, 0, 192, 0, 0, 0, 0,
            0, 0, 0, 192, 0, 0, 0, 100, 0, 0, 0, 100, 0, 0, 0, 0,
        }, client.Read(32));
    }

    [Fact]
    public void Unlink_cancels_a_queued_iso_urb_and_sends_no_submit_reply()
    {
        using var server = new UsbIpServer(0, isoClock: () => 0);
        using IVirtualPad pad = server.CreateDualSense(null, audio: true);
        using var client = new FakeUsbIpClient(server.Port);
        client.Import(pad.BusId);
        uint seq = client.SendSubmit(1, false, 0, payload: new byte[16], packets: 1);
        uint cancel = client.SendUnlink(seq);

        Assert.Equal(UsbIpCodec.RetUnlinkHeader(cancel, -104), client.ReadReply(false).Header);
        uint again = client.SendUnlink(seq);
        Assert.Equal(UsbIpCodec.RetUnlinkHeader(again, 0), client.ReadReply(false).Header);
        Assert.Equal(18, client.Control(new SetupPacket(0x80, 6, 0x0100, 0, 18)).Data.Length);
    }
}
