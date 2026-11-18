using System.Net.Sockets;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

/// <summary>Plays the VHCI side of USB/IP over loopback.</summary>
internal sealed class FakeUsbIpClient : IDisposable
{
    private readonly TcpClient _tcp;
    private uint _seq;

    public FakeUsbIpClient(int port)
    {
        _tcp = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        _tcp.Connect("127.0.0.1", port);
        _tcp.ReceiveTimeout = 5000;
        Stream = _tcp.GetStream();
    }

    public NetworkStream Stream { get; }

    public byte[] Import(string busId)
    {
        byte[] req = new byte[40];
        UsbIpCodec.WriteOpCommon(req, UsbIpCodec.OpReqImport, 0);
        UsbIpCodec.WriteBusId(req.AsSpan(8), busId);
        Stream.Write(req);
        byte[] op = Read(8);
        (_, _, uint status) = UsbIpCodec.ReadOpCommon(op);
        return status == 0 ? [.. op, .. Read(UsbIpCodec.DeviceRecordSize)] : op;
    }

    /// <summary>Next seqnum with the usbip-win2 direction bit in the low bit.</summary>
    public uint NextSeq(bool dirIn) => (++_seq << 1) | (dirIn ? 1u : 0u);

    public uint SendSubmit(uint ep, bool dirIn, int length, SetupPacket? setup = null, byte[]? payload = null, int packets = UsbIpCodec.NonIso)
    {
        uint seq = NextSeq(dirIn);
        var h = new UrbHeader(UsbIpCodec.CmdSubmit, seq, (1u << 16) | 1, dirIn ? 1u : 0u, ep, 0, length, 0, packets, 4,
            setup is { } s ? UsbIpCodec.PackSetup(s) : 0);
        byte[] b = new byte[UsbIpCodec.HeaderSize];
        UsbIpCodec.WriteHeader(b, h);
        Stream.Write(b);
        if (payload is not null)
        {
            Stream.Write(payload);
        }

        return seq;
    }

    public uint SendUnlink(uint target)
    {
        uint seq = NextSeq(false);
        var h = new UrbHeader(UsbIpCodec.CmdUnlink, seq, (1u << 16) | 1, 0, 0, unchecked((int)target), 0, 0, 0, 0, 0);
        byte[] b = new byte[UsbIpCodec.HeaderSize];
        UsbIpCodec.WriteHeader(b, h);
        Stream.Write(b);
        return seq;
    }

    /// <summary>Reads one RET_SUBMIT or RET_UNLINK with its IN payload.</summary>
    public (UrbHeader Header, byte[] Data) ReadReply(bool expectPayload)
    {
        UrbHeader h = UsbIpCodec.ReadHeader(Read(UsbIpCodec.HeaderSize));
        byte[] data = h.Command == UsbIpCodec.RetSubmit && expectPayload && h.Length > 0 ? Read(h.Length) : [];
        return (h, data);
    }

    public (UrbHeader Header, byte[] Data) Control(SetupPacket s, byte[]? payload = null)
    {
        uint seq = SendSubmit(0, s.In, s.In ? s.Length : payload?.Length ?? 0, s, payload);
        var r = ReadReply(s.In);
        Assert.Equal(seq, r.Header.SeqNum);
        return r;
    }

    public byte[] Read(int n)
    {
        byte[] b = new byte[n];
        Stream.ReadExactly(b);
        return b;
    }

    /// <summary>True if the server closed the connection.</summary>
    public bool ReadsEof()
    {
        try
        {
            return Stream.Read(new byte[1]) == 0;
        }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: not SocketError.TimedOut })
        {
            return true;
        }
    }

    public void Dispose() => _tcp.Dispose();
}
