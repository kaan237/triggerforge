using System.Text;
using Tactile.Device.DualSense;

namespace Tactile.Emulation;

internal readonly record struct ControlResult(int Status, byte[] Data)
{
    public static ControlResult Ok() => new(0, []);
    public static ControlResult Ok(byte[] data) => new(0, data);
    public static ControlResult Stall { get; } = new(UsbIpCodec.EPipe, []);
}

/// <summary>
/// One virtual USB device: standard control requests, the current input report and pending startup packets.
/// The USB/IP connection owns the URB queue and asks for input through <see cref="NextInput"/>.
/// </summary>
internal abstract class VirtualUsbDevice : IVirtualPad
{
    public const int DevNum = 1;
    public const int SpeedFull = 2;
    public const int SpeedHigh = 3;

    private static readonly InputState Neutral = new(
        Buttons.None, new Stick(128, 128), new Stick(128, 128), 0, 0, default, default, 0,
        new TouchPoint(false, 0, 0, 0), new TouchPoint(false, 0, 0, 0), new Battery(100, ChargeState.Full),
        false, false, false);

    private readonly Lock _gate = new();
    private readonly UsbIpServer _server;
    private readonly Queue<byte[]> _startup = new();
    private byte[]? _input;
    private bool _fresh;
    private int _imported;
    private byte _configuration;

    protected VirtualUsbDevice(UsbIpServer server, int busNum)
    {
        _server = server;
        BusNum = busNum;
        BusId = $"{busNum}-1";
    }

    public abstract VirtualKind Kind { get; }
    public string BusId { get; }
    public int BusNum { get; }
    public bool Imported => Volatile.Read(ref _imported) != 0;
    public event Action<PadFeedback>? Feedback;

    public abstract int Speed { get; }
    public abstract byte[] DeviceDescriptor { get; }
    public abstract byte[] ConfigDescriptor { get; }
    public abstract int InEndpoint { get; }
    public abstract int OutEndpoint { get; }
    public abstract int IntervalMs { get; }

    /// <summary>String descriptors from index 1.</summary>
    protected abstract string[] Strings { get; }

    protected virtual IEnumerable<byte[]> StartupPackets => [];

    /// <summary>Set by the connection while imported; called after new input is stored.</summary>
    public Action? InputReady { get; set; }

    public void Submit(InputState state)
    {
        lock (_gate)
        {
            _input = BuildInput(state);
            _fresh = true;
        }

        InputReady?.Invoke();
    }

    public void Dispose() => _server.Remove(this);

    public bool TryAttach()
    {
        if (Interlocked.CompareExchange(ref _imported, 1, 0) != 0)
        {
            return false;
        }

        lock (_gate)
        {
            _configuration = 0;
            _fresh = true;
            _startup.Clear();
            foreach (byte[] p in StartupPackets)
            {
                _startup.Enqueue(p);
            }
        }

        return true;
    }

    public void Detach()
    {
        InputReady = null;
        Volatile.Write(ref _imported, 0);
    }

    /// <summary>
    /// The report for the next interrupt IN completion: a startup packet, else the input report if it is new or
    /// <paramref name="force"/> is set (interval repeat), else null to keep the URB pending.
    /// </summary>
    public byte[]? NextInput(bool force)
    {
        lock (_gate)
        {
            if (_startup.Count > 0)
            {
                return _startup.Dequeue();
            }

            if (!_fresh && !force)
            {
                return null;
            }

            _fresh = false;
            return _input ??= BuildInput(Neutral);
        }
    }

    protected byte[] CurrentInput()
    {
        lock (_gate)
        {
            return _input ??= BuildInput(Neutral);
        }
    }

    /// <summary>Called under the device lock; may keep counters.</summary>
    protected abstract byte[] BuildInput(InputState state);

    public abstract void Out(ReadOnlySpan<byte> data, long timestamp);

    protected virtual byte[]? ClassDescriptor(SetupPacket s) => null;

    protected virtual ControlResult ClassOrVendor(SetupPacket s, ReadOnlySpan<byte> data, long timestamp) => ControlResult.Stall;

    public ControlResult Control(SetupPacket s, ReadOnlySpan<byte> data, long timestamp)
    {
        ControlResult r = s.Type == 0 ? Standard(s) : ClassOrVendor(s, data, timestamp);
        _server.Log?.Invoke($"{BusId} setup {s} data {data.Length} -> {(r.Status == 0 ? $"ok {r.Data.Length}" : "stall")}");
        return r;
    }

    protected void Raise(PadFeedback feedback)
    {
        try
        {
            Feedback?.Invoke(feedback);
        }
        catch (Exception e)
        {
            _server.Log?.Invoke($"{BusId} feedback handler failed: {e.Message}");
        }
    }

    private ControlResult Standard(SetupPacket s)
    {
        switch (s.Request)
        {
            case 0x00: // GET_STATUS
                return ControlResult.Ok([0, 0]);
            case 0x01: // CLEAR_FEATURE
            case 0x03: // SET_FEATURE
            case 0x05: // SET_ADDRESS
                return ControlResult.Ok();
            case 0x06: // GET_DESCRIPTOR
                return Descriptor(s) is { } d ? ControlResult.Ok(d) : ControlResult.Stall;
            case 0x08: // GET_CONFIGURATION
                lock (_gate)
                {
                    return ControlResult.Ok([_configuration]);
                }
            case 0x09: // SET_CONFIGURATION
                if ((s.Value & 0xFF) > ConfigDescriptor[5])
                {
                    return ControlResult.Stall;
                }

                lock (_gate)
                {
                    _configuration = (byte)s.Value;
                }

                return ControlResult.Ok();
            case 0x0A: // GET_INTERFACE
                return ControlResult.Ok([0]);
            case 0x0B: // SET_INTERFACE: no alternate settings without audio
                return s.Value == 0 ? ControlResult.Ok() : ControlResult.Stall;
            default:
                return ControlResult.Stall;
        }
    }

    private byte[]? Descriptor(SetupPacket s)
    {
        int type = s.Value >> 8;
        int index = s.Value & 0xFF;
        return type switch
        {
            1 => DeviceDescriptor,
            2 when index == 0 => ConfigDescriptor,
            3 => StringDescriptor(index),
            _ => ClassDescriptor(s),
        };
    }

    private byte[]? StringDescriptor(int index)
    {
        if (index == 0)
        {
            return [4, 3, 0x09, 0x04];
        }

        if (index > Strings.Length)
        {
            return null;
        }

        byte[] text = Encoding.Unicode.GetBytes(Strings[index - 1]);
        byte[] d = new byte[2 + text.Length];
        d[0] = (byte)d.Length;
        d[1] = 3;
        text.CopyTo(d, 2);
        return d;
    }

    protected static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
}
