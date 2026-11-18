using System.Diagnostics;
using System.Net.Sockets;

namespace Tactile.Emulation;

/// <summary>URB phase of one imported device: one TCP connection, a reader thread and an IN pacer thread.</summary>
internal sealed class UsbIpConnection
{
    public const int MaxTransfer = 65536;
    public const int MaxPending = 64;
    public const int MaxIsoPackets = 1024;

    private readonly NetworkStream _stream;
    private readonly VirtualUsbDevice _device;
    private readonly Lock _write = new();
    private readonly List<(uint SeqNum, int Length)> _pending = [];
    private readonly object _wake = new();
    private readonly byte[] _replyHeader = new byte[UsbIpCodec.HeaderSize];
    private bool _signaled;
    private int _closed;
    private long _lastSend;

    public UsbIpConnection(NetworkStream stream, VirtualUsbDevice device)
    {
        _stream = stream;
        _device = device;
    }

    public VirtualUsbDevice Device => _device;

    private bool Closed => Volatile.Read(ref _closed) != 0;

    /// <summary>Runs until the connection drops or turns malformed. Throws on read/write errors.</summary>
    public void Run()
    {
        _device.InputReady = Wake;
        var pacer = new Thread(Pace) { IsBackground = true, Name = $"usbip pacer {_device.BusId}" };
        pacer.Start();
        try
        {
            byte[] header = new byte[UsbIpCodec.HeaderSize];
            while (!Closed)
            {
                _stream.ReadExactly(header);
                UrbHeader h = UsbIpCodec.ReadHeader(header);
                switch (h.Command)
                {
                    case UsbIpCodec.CmdSubmit:
                        Submit(h);
                        break;
                    case UsbIpCodec.CmdUnlink:
                        Unlink(h);
                        break;
                    default:
                        throw new InvalidDataException($"unexpected command {h.Command}");
                }
            }
        }
        finally
        {
            Close();
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _device.Detach();
        _stream.Dispose();
        Wake();
    }

    private void Submit(UrbHeader h)
    {
        bool iso = h.Packets != UsbIpCodec.NonIso && h.Packets != 0;
        if (h.Length < 0 || h.Length > MaxTransfer || (iso && (h.Packets < 0 || h.Packets > MaxIsoPackets)))
        {
            throw new InvalidDataException("bad submit lengths");
        }

        bool dirIn = h.Direction == 1;
        byte[] payload = [];
        if (!dirIn && h.Length > 0)
        {
            payload = new byte[h.Length];
            _stream.ReadExactly(payload);
        }

        if (iso)
        {
            byte[] skip = new byte[h.Packets * UsbIpCodec.IsoDescriptorSize];
            _stream.ReadExactly(skip);
        }

        long timestamp = Stopwatch.GetTimestamp();
        if (iso)
        {
            Reply(h.SeqNum, UsbIpCodec.EInval, []);
            return;
        }

        if (h.Endpoint == 0)
        {
            SetupPacket s = h.SetupPacket;
            ControlResult r = _device.Control(s, payload, timestamp);
            if (s.In)
            {
                int n = r.Status == 0 ? Math.Min(r.Data.Length, Math.Min(h.Length, s.Length)) : 0;
                Reply(h.SeqNum, r.Status, r.Data.AsSpan(0, n));
            }
            else
            {
                Reply(h.SeqNum, r.Status, [], r.Status == 0 ? payload.Length : 0);
            }
        }
        else if (dirIn && h.Endpoint == _device.InEndpoint)
        {
            if (h.Length == 0)
            {
                Reply(h.SeqNum, 0, []);
                return;
            }

            lock (_write)
            {
                if (_pending.Count >= MaxPending)
                {
                    throw new InvalidDataException("too many pending IN URBs");
                }

                _pending.Add((h.SeqNum, h.Length));
            }

            Complete(force: false);
        }
        else if (!dirIn && h.Endpoint == _device.OutEndpoint)
        {
            _device.Out(payload, timestamp);
            Reply(h.SeqNum, 0, [], payload.Length);
        }
        else
        {
            Reply(h.SeqNum, UsbIpCodec.EInval, []);
        }
    }

    private void Unlink(UrbHeader h)
    {
        uint target = unchecked((uint)h.Field14);
        lock (_write)
        {
            int i = _pending.FindIndex(p => p.SeqNum == target);
            if (i >= 0)
            {
                _pending.RemoveAt(i);
            }

            UsbIpCodec.WriteHeader(_replyHeader, UsbIpCodec.RetUnlinkHeader(h.SeqNum, i >= 0 ? UsbIpCodec.EConnReset : 0));
            _stream.Write(_replyHeader);
        }
    }

    /// <summary>Answers pending IN URBs with new input; with <paramref name="force"/>, one repeat of the current report.</summary>
    private void Complete(bool force)
    {
        lock (_write)
        {
            while (_pending.Count > 0)
            {
                byte[]? data = _device.NextInput(force);
                if (data is null)
                {
                    return;
                }

                force = false;
                (uint seqNum, int length) = _pending[0];
                _pending.RemoveAt(0);
                Reply(seqNum, 0, data.AsSpan(0, Math.Min(length, data.Length)));
                Volatile.Write(ref _lastSend, Stopwatch.GetTimestamp());
            }
        }
    }

    private void Reply(uint seqNum, int status, ReadOnlySpan<byte> data, int? actualLength = null)
    {
        lock (_write)
        {
            UsbIpCodec.WriteHeader(_replyHeader, UsbIpCodec.RetSubmitHeader(seqNum, status, actualLength ?? data.Length));
            _stream.Write(_replyHeader);
            if (data.Length > 0)
            {
                _stream.Write(data);
            }
        }
    }

    private void Wake()
    {
        lock (_wake)
        {
            _signaled = true;
            Monitor.Pulse(_wake);
        }
    }

    // New input is sent at once; without it the current report repeats every interval. Monitor.Wait follows the
    // system timer resolution, so idle repeats can be slower than the interval, which only matters while idle.
    private void Pace()
    {
        int interval = _device.IntervalMs;
        try
        {
            while (!Closed)
            {
                lock (_wake)
                {
                    if (!_signaled)
                    {
                        Monitor.Wait(_wake, interval);
                    }

                    _signaled = false;
                }

                if (Closed)
                {
                    return;
                }

                bool due = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSend)).TotalMilliseconds >= interval;
                Complete(due);
            }
        }
        catch (Exception)
        {
            Close();
        }
    }
}
