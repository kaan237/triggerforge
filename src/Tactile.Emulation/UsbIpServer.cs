using System.Net;
using System.Net.Sockets;

namespace Tactile.Emulation;

/// <summary>USB/IP server on 127.0.0.1 (docs/protocol-emulation.md §1). One TCP connection per imported device.</summary>
public sealed class UsbIpServer : IUsbIpServer
{
    private const int MaxConnections = 16;
    private const int HandshakeTimeoutMs = 5000;

    private readonly TcpListener _listener;
    private readonly Func<long>? _isoClock;
    private readonly Lock _gate = new();
    private readonly List<VirtualUsbDevice> _pads = [];
    private readonly List<UsbIpConnection> _connections = [];
    private int _open;
    private volatile bool _disposed;

    internal UsbIpServer(int port, Func<long>? isoClock = null)
    {
        _isoClock = isoClock;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true, Name = "usbip accept" }.Start();
    }

    public int Port { get; }

    /// <summary>Optional log of every setup packet and connection event (for the first hardware runs).</summary>
    public Action<string>? Log { get; set; }

    internal IVirtualPad CreateDualSense(DualSenseIdentity? identity, bool audio) => Create(VirtualKind.DualSense, identity, audio);

    public IVirtualPad Create(VirtualKind kind, DualSenseIdentity? identity) => Create(kind, identity, audio: false);

    private IVirtualPad Create(VirtualKind kind, DualSenseIdentity? identity, bool audio)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int busNum = 1;
            while (_pads.Exists(p => p.BusNum == busNum))
            {
                busNum++;
            }

            VirtualUsbDevice pad = kind switch
            {
                VirtualKind.DualSense when audio => new VirtualDualSense.WithAudio(this, busNum, identity),
                VirtualKind.DualSense => new VirtualDualSense(this, busNum, identity),
                _ => new VirtualXbox360(this, busNum),
            };
            _pads.Add(pad);
            return pad;
        }
    }

    public void Remove(IVirtualPad pad)
    {
        UsbIpConnection[] drop;
        lock (_gate)
        {
            _pads.Remove((VirtualUsbDevice)pad);
            drop = _connections.Where(c => c.Device == pad).ToArray();
        }

        foreach (UsbIpConnection c in drop)
        {
            c.Close();
        }
    }

    public void Dispose()
    {
        UsbIpConnection[] drop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pads.Clear();
            drop = [.. _connections];
        }

        _listener.Stop();
        foreach (UsbIpConnection c in drop)
        {
            c.Close();
        }
    }

    private void Accept()
    {
        while (!_disposed)
        {
            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (Interlocked.Increment(ref _open) > MaxConnections)
            {
                Interlocked.Decrement(ref _open);
                socket.Dispose();
                continue;
            }

            new Thread(() => Handle(socket)) { IsBackground = true, Name = "usbip connection" }.Start();
        }
    }

    private void Handle(Socket socket)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        UsbIpConnection? connection = null;
        try
        {
            socket.NoDelay = true;
            socket.ReceiveTimeout = HandshakeTimeoutMs;
            socket.SendTimeout = HandshakeTimeoutMs;
            byte[] op = new byte[UsbIpCodec.OpCommonSize];
            stream.ReadExactly(op);
            (ushort version, ushort code, _) = UsbIpCodec.ReadOpCommon(op);
            if (version != UsbIpCodec.Version)
            {
                Log?.Invoke($"usbip: bad version {version:X4}");
                return;
            }

            if (code == UsbIpCodec.OpReqDevList)
            {
                stream.Write(DevList());
                return;
            }

            if (code != UsbIpCodec.OpReqImport)
            {
                Log?.Invoke($"usbip: unknown op {code:X4}");
                return;
            }

            byte[] busIdBytes = new byte[UsbIpCodec.BusIdSize];
            stream.ReadExactly(busIdBytes);
            string? busId = UsbIpCodec.ReadBusId(busIdBytes);
            VirtualUsbDevice? pad;
            lock (_gate)
            {
                pad = _disposed ? null : _pads.Find(p => p.BusId == busId);
            }

            byte[] reply = new byte[UsbIpCodec.OpCommonSize + UsbIpCodec.DeviceRecordSize];
            if (pad is null || !pad.TryAttach())
            {
                uint status = (uint)(pad is null ? UsbIpCodec.StatusNoSuchBusId : UsbIpCodec.StatusBusy);
                UsbIpCodec.WriteOpCommon(reply, UsbIpCodec.OpRepImport, status);
                stream.Write(reply.AsSpan(0, UsbIpCodec.OpCommonSize));
                Log?.Invoke($"usbip: import {busId} refused ({status})");
                return;
            }

            connection = new UsbIpConnection(stream, pad, _isoClock);
            lock (_gate)
            {
                _connections.Add(connection);
            }

            try
            {
                UsbIpCodec.WriteOpCommon(reply, UsbIpCodec.OpRepImport, UsbIpCodec.StatusOk);
                UsbIpCodec.WriteDeviceRecord(reply.AsSpan(UsbIpCodec.OpCommonSize), pad.BusId, pad.BusNum, VirtualUsbDevice.DevNum, pad.Speed, pad.DeviceDescriptor, pad.ConfigDescriptor);
                stream.Write(reply);
                Log?.Invoke($"usbip: imported {pad.BusId}");

                // The host may stay idle for a long time; TCP keep-alive on its side detects a dead peer.
                socket.ReceiveTimeout = 0;
                connection.Run();
            }
            finally
            {
                connection.Close();
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"usbip: connection closed: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            if (connection is not null)
            {
                lock (_gate)
                {
                    _connections.Remove(connection);
                }
            }

            Interlocked.Decrement(ref _open);
        }
    }

    private byte[] DevList()
    {
        VirtualUsbDevice[] pads;
        lock (_gate)
        {
            pads = [.. _pads];
        }

        var records = pads.Select(p => (Pad: p, Interfaces: UsbIpCodec.Interfaces(p.ConfigDescriptor))).ToArray();
        int size = UsbIpCodec.OpCommonSize + 4 + records.Sum(r => UsbIpCodec.DeviceRecordSize + 4 * r.Interfaces.Count);
        byte[] b = new byte[size];
        UsbIpCodec.WriteOpCommon(b, UsbIpCodec.OpRepDevList, UsbIpCodec.StatusOk);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), records.Length);
        int at = 12;
        foreach ((VirtualUsbDevice pad, List<(byte Class, byte SubClass, byte Protocol)> interfaces) in records)
        {
            UsbIpCodec.WriteDeviceRecord(b.AsSpan(at), pad.BusId, pad.BusNum, VirtualUsbDevice.DevNum, pad.Speed, pad.DeviceDescriptor, pad.ConfigDescriptor);
            at += UsbIpCodec.DeviceRecordSize;
            foreach ((byte c, byte s, byte p) in interfaces)
            {
                b[at] = c;
                b[at + 1] = s;
                b[at + 2] = p;
                at += 4;
            }
        }

        return b;
    }
}
