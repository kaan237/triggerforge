using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

/// <summary>
/// DSU (Cemuhook) motion server for emulators, protocol version 1001. Loopback only; answers
/// version and controller info requests and streams every published input report to the clients
/// that subscribed in the last five seconds.
/// </summary>
internal sealed class DsuServer(Func<TimeSpan> now, Action<Notice> report) : IDisposable
{
    private const ushort ProtocolVersion = 1001;
    private const int HeaderLength = 16;
    private const int PayloadStart = 20;
    private const int MaxPacketSize = 1024;
    private const int MaxClients = 8;
    private const int SlotCount = 4;
    private const int MaxPacketsPerSecond = 500;
    private const int MaxRateSenders = 64;
    private const int MaxSubscriptionsPerClient = 8;
    private const int RetryPollMilliseconds = 250;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const uint VersionRequest = 0x100000;
    private const uint InfoRequest = 0x100001;
    private const uint DataRequest = 0x100002;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly ListenerWait _wait = new();
    private readonly Dictionary<IPEndPoint, Queue<TimeSpan>> _arrivals = [];
    private readonly Dictionary<IPEndPoint, List<Subscription>> _clients = [];
    private readonly SlotState?[] _slots = new SlotState?[SlotCount];
    private readonly int[] _generations = new int[SlotCount];
    private readonly uint _serverId = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private volatile Thread? _worker;
    private volatile Socket? _socket;
    private volatile bool _publishing;
    private int _stopping;
    private DsuListenerState _state;
    private bool _enabled;
    private bool _conflict;
    private int _port = new IntegrationSettings().DsuPort;
    private int _configuredPort = new IntegrationSettings().DsuPort;
    private TimeSpan _retryAt;
    private long _packets;
    private long _rejected;

    private bool Stopping => Volatile.Read(ref _stopping) != 0;

    public DsuStatus Status
    {
        get
        {
            lock (_lock)
            {
                Expire(now());
                return new(Stopping ? DsuListenerState.Off : _state, _port, _clients.Count,
                    Interlocked.Read(ref _packets), Interlocked.Read(ref _rejected));
            }
        }
    }

    public void Configure(IntegrationSettings settings)
    {
        lock (_lock)
        {
            bool conflict = settings.DsuPort == settings.UdpPort || settings.DsuPort == settings.ForzaPort ||
                TelemetryListener.SamePort(settings.DsuPort, settings.F1Port) ||
                TelemetryListener.SamePort(settings.DsuPort, settings.BeamNgPort);
            if (Stopping || (_enabled == settings.DsuEnabled && _configuredPort == settings.DsuPort &&
                (!_enabled || _conflict == conflict)))
            {
                return;
            }

            _enabled = settings.DsuEnabled;
            _conflict = conflict;
            _configuredPort = settings.DsuPort;
            CloseSocket();
            _clients.Clear();
            _arrivals.Clear();
            _state = _enabled && _conflict ? DsuListenerState.PortConflict : DsuListenerState.Off;
            if (_enabled && !_conflict)
            {
                if (_worker is null)
                {
                    var worker = new Thread(Run) { IsBackground = true, Name = "TriggerForge: DSU" };
                    worker.Start();
                    _worker = worker;
                }

                TryBind(now());
            }

            _wait.Wake();
        }
    }

    /// <summary>Changes every time the slot disconnects; read it before taking a report to publish.</summary>
    public int Generation(int slot)
    {
        lock (_lock)
        {
            return (uint)slot < SlotCount ? _generations[slot] : 0;
        }
    }

    /// <summary>
    /// Latest input of a controller slot with its gyro in deg/s and accelerometer in g.
    /// A report read under an older <paramref name="generation"/> than the slot's current one is dropped.
    /// </summary>
    public void PublishInput(int slot, InputState s, Vector3 gyroDegPerSec, Vector3 accelG, Link link, string? mac,
        int? generation = null)
    {
        if (!_publishing || (uint)slot >= SlotCount)
        {
            return;
        }

        byte[] packet;
        IPEndPoint[] targets;
        Socket socket;
        lock (_lock)
        {
            if (Stopping || _socket is not { } bound || (generation is { } seen && seen != _generations[slot]))
            {
                return;
            }

            TimeSpan time = now();
            SlotState state = _slots[slot] ??= new SlotState();
            if (!state.Connected)
            {
                state.ResetClock();
            }

            state.Connected = true;
            state.Link = link;
            state.Mac = ParseMac(mac) ?? SyntheticMac(slot);
            state.Battery = s.Battery;
            ulong micros = state.NextMicros(s.SensorTimestamp, time);
            uint counter = state.Counter++;
            Expire(time);
            targets = _clients.Where(c => c.Value.Any(sub => sub.Matches(slot, state.Mac))).Select(c => c.Key).ToArray();
            if (targets.Length == 0)
            {
                return;
            }

            packet = BuildData(slot, state, s, gyroDegPerSec, accelG, micros, counter);
            socket = bound;
        }

        foreach (IPEndPoint target in targets)
        {
            Send(socket, packet, target);
        }
    }

    public void Disconnect(int slot)
    {
        if ((uint)slot >= SlotCount)
        {
            return;
        }

        lock (_lock)
        {
            _generations[slot]++;
            if (_slots[slot] is { } state)
            {
                state.Connected = false;
                state.ResetClock();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _publishing = false;
        _socket?.Dispose();
        _wait.Wake();
        if (_worker != Thread.CurrentThread)
        {
            _worker?.Join(TimeSpan.FromSeconds(1));
        }
    }

    private void Run()
    {
        byte[] buffer = new byte[ushort.MaxValue];
        try
        {
            while (!Stopping)
            {
                Socket? listening = null;
                try
                {
                    int wait;
                    CancellationTokenSource receive;
                    lock (_lock)
                    {
                        if (Stopping)
                        {
                            break;
                        }

                        TimeSpan time = now();
                        if (_enabled && !_conflict && _socket is null && time >= _retryAt)
                        {
                            TryBind(time);
                        }

                        listening = _socket;
                        wait = _enabled && !_conflict && listening is null
                            ? Math.Min(RetryPollMilliseconds, ListenerWait.Until(time, _retryAt))
                            : Timeout.Infinite;
                        receive = _wait.Begin();
                    }

                    if (Stopping)
                    {
                        _wait.Wake();
                    }

                    if (_wait.Receive(listening, buffer, wait, receive) is { } packet)
                    {
                        HandlePacket(listening!, packet, buffer);
                    }
                }
                catch (Exception)
                {
                    if (!Stopping && (listening is null || ReferenceEquals(listening, _socket)))
                    {
                        Interlocked.Increment(ref _rejected);
                    }
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                CloseSocket();
                _state = DsuListenerState.Off;
            }
        }
    }

    private void TryBind(TimeSpan time)
    {
        Socket? candidate = null;
        try
        {
            candidate = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            candidate.ExclusiveAddressUse = true;
            candidate.Blocking = false;
            if (OperatingSystem.IsWindows())
            {
                // Without this, an answer to an emulator that already closed its socket makes the next receive fail.
                candidate.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            candidate.Bind(new IPEndPoint(IPAddress.Loopback, _configuredPort));
            _port = ((IPEndPoint)candidate.LocalEndPoint!).Port;
        }
        catch (SocketException)
        {
            candidate?.Dispose();
            bool firstFailure = _state != DsuListenerState.PortBusy;
            _state = DsuListenerState.PortBusy;
            _retryAt = time + RetryInterval;
            if (firstFailure)
            {
                report(new Notice(NoticeKinds.Info,
                    $"DSU port {_configuredPort} is in use. Close other motion servers or choose another port.",
                    $"DSU portu {_configuredPort} kullanımda. Diğer hareket sunucularını kapatın ya da portu değiştirin."));
            }

            return;
        }

        _socket = candidate;
        if (Stopping)
        {
            CloseSocket();
            return;
        }

        _state = DsuListenerState.Listening;
        _publishing = true;
    }

    private void CloseSocket()
    {
        _publishing = false;
        Socket? socket = _socket;
        _socket = null;
        socket?.Dispose();
        _port = _configuredPort;
    }

    private void HandlePacket(Socket socket, SocketReceiveFromResult packet, byte[] buffer)
    {
        var replies = new List<byte[]>(SlotCount);
        lock (_lock)
        {
            if (Stopping || !ReferenceEquals(socket, _socket))
            {
                return;
            }

            TimeSpan time = now();
            if (packet.RemoteEndPoint is IPEndPoint origin && !AllowArrival(origin, time))
            {
                Interlocked.Increment(ref _rejected);
                return;
            }

            if (packet.RemoteEndPoint is not IPEndPoint sender || !IPAddress.IsLoopback(sender.Address) ||
                !Handle(buffer.AsSpan(0, packet.ReceivedBytes), sender, time, replies))
            {
                Interlocked.Increment(ref _rejected);
                return;
            }
        }

        foreach (byte[] reply in replies)
        {
            Send(socket, reply, (IPEndPoint)packet.RemoteEndPoint);
        }
    }

    /// <summary>Rolling window per sender, so one flooding client cannot starve the others.</summary>
    private bool AllowArrival(IPEndPoint sender, TimeSpan time)
    {
        if (!_arrivals.TryGetValue(sender, out Queue<TimeSpan>? window))
        {
            if (_arrivals.Count >= MaxRateSenders)
            {
                foreach (IPEndPoint idle in _arrivals.Where(a => a.Value.Count == 0 || time - a.Value.Last() >= RateWindow).Select(a => a.Key).ToArray())
                {
                    _arrivals.Remove(idle);
                }

                if (_arrivals.Count >= MaxRateSenders)
                {
                    return false;
                }
            }

            window = _arrivals[new IPEndPoint(sender.Address, sender.Port)] = new Queue<TimeSpan>();
        }

        while (window.TryPeek(out TimeSpan first) && time - first >= RateWindow)
        {
            window.Dequeue();
        }

        if (window.Count >= MaxPacketsPerSecond)
        {
            return false;
        }

        window.Enqueue(time);
        return true;
    }

    /// <summary>False for anything that is not a well-formed request.</summary>
    private bool Handle(ReadOnlySpan<byte> data, IPEndPoint sender, TimeSpan time, List<byte[]> replies)
    {
        if (data.Length < PayloadStart || data.Length > MaxPacketSize || !data[..4].SequenceEqual("DSUC"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != ProtocolVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) != data.Length - HeaderLength || !HasValidCrc(data))
        {
            return false;
        }

        ReadOnlySpan<byte> payload = data[PayloadStart..];
        switch (BinaryPrimitives.ReadUInt32LittleEndian(data[HeaderLength..]))
        {
            case VersionRequest:
            {
                byte[] reply = NewPacket(PayloadStart + 2, VersionRequest);
                BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(PayloadStart), ProtocolVersion);
                replies.Add(Seal(reply));
                return true;
            }

            case InfoRequest:
            {
                if (payload.Length < 4)
                {
                    return false;
                }

                int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
                if (count is < 1 or > SlotCount || payload.Length < 4 + count)
                {
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    int index = payload[4 + i];
                    if (index < SlotCount)
                    {
                        byte[] reply = NewPacket(PayloadStart + 12, InfoRequest);
                        WriteShared(reply.AsSpan(PayloadStart), index, _slots[index]);
                        replies.Add(Seal(reply));
                    }
                }

                return true;
            }

            case DataRequest:
            {
                if (payload.Length < 8)
                {
                    return false;
                }

                Expire(time);
                if (!_clients.TryGetValue(sender, out List<Subscription>? subs))
                {
                    if (_clients.Count >= MaxClients)
                    {
                        _clients.Remove(_clients.MinBy(c => c.Value.Max(sub => sub.LastSeen)).Key);
                    }

                    subs = _clients[new IPEndPoint(sender.Address, sender.Port)] = [];
                }

                var request = new Subscription(payload[0], payload[1], payload.Slice(2, 6).ToArray(), time);
                int existing = subs.FindIndex(sub => sub.SameTarget(request));
                if (existing >= 0)
                {
                    subs[existing] = request;
                }
                else
                {
                    if (subs.Count >= MaxSubscriptionsPerClient)
                    {
                        subs.RemoveAt(subs.IndexOf(subs.MinBy(sub => sub.LastSeen)));
                    }

                    subs.Add(request);
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool HasValidCrc(ReadOnlySpan<byte> data)
    {
        Span<byte> copy = stackalloc byte[data.Length];
        data.CopyTo(copy);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(copy[8..]);
        copy.Slice(8, 4).Clear();
        return stored == Crc32.Compute(copy);
    }

    private void Expire(TimeSpan time)
    {
        foreach ((IPEndPoint client, List<Subscription> subs) in _clients.ToArray())
        {
            subs.RemoveAll(sub => time - sub.LastSeen >= ClientTimeout);
            if (subs.Count == 0)
            {
                _clients.Remove(client);
            }
        }
    }

    private void Send(Socket socket, byte[] packet, IPEndPoint target)
    {
        try
        {
            socket.SendTo(packet, target);
            Interlocked.Increment(ref _packets);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The emulator went away or the socket closed; the subscription expires on its own.
        }
    }

    private byte[] NewPacket(int length, uint type)
    {
        byte[] packet = new byte[length];
        "DSUS"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)(length - HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), _serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderLength), type);
        return packet;
    }

    private static byte[] Seal(byte[] packet)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), Crc32.Compute(packet));
        return packet;
    }

    /// <summary>The 11 bytes both controller info and data packets start with, plus one more for the connected flag.</summary>
    private static void WriteShared(Span<byte> payload, int slot, SlotState? state)
    {
        payload[0] = (byte)slot;
        if (state is not { Connected: true })
        {
            return;
        }

        payload[1] = 2;
        payload[2] = 2;
        payload[3] = (byte)(state.Link == Link.Usb ? 1 : 2);
        state.Mac.CopyTo(payload[4..]);
        payload[10] = BatteryByte(state.Battery);
    }

    private static byte BatteryByte(Battery battery) => battery.Charge switch
    {
        ChargeState.Full => 0xEF,
        ChargeState.Charging => 0xEE,
        _ => (byte)Math.Clamp(1 + battery.Percent * 4 / 100, 1, 5),
    };

    private byte[] BuildData(int slot, SlotState state, InputState s, Vector3 gyro, Vector3 accel, ulong micros, uint counter)
    {
        byte[] packet = NewPacket(PayloadStart + 80, DataRequest);
        Span<byte> p = packet.AsSpan(PayloadStart);
        WriteShared(p, slot, state);
        p[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], counter);

        p[16] = (byte)(Flag(s, Buttons.Left, 0x80) | Flag(s, Buttons.Down, 0x40) | Flag(s, Buttons.Right, 0x20) |
            Flag(s, Buttons.Up, 0x10) | Flag(s, Buttons.Options, 0x08) | Flag(s, Buttons.R3, 0x04) |
            Flag(s, Buttons.L3, 0x02) | Flag(s, Buttons.Create, 0x01));
        p[17] = (byte)(Flag(s, Buttons.Square, 0x80) | Flag(s, Buttons.Cross, 0x40) | Flag(s, Buttons.Circle, 0x20) |
            Flag(s, Buttons.Triangle, 0x10) | Flag(s, Buttons.R1, 0x08) | Flag(s, Buttons.L1, 0x04) |
            Flag(s, Buttons.R2, 0x02) | Flag(s, Buttons.L2, 0x01));
        p[18] = (byte)(s.IsDown(Buttons.PS) ? 1 : 0);
        p[19] = (byte)(s.IsDown(Buttons.Touchpad) ? 1 : 0);
        p[20] = s.Left.X;
        p[21] = (byte)(255 - s.Left.Y);
        p[22] = s.Right.X;
        p[23] = (byte)(255 - s.Right.Y);
        p[24] = Analog(s, Buttons.Left);
        p[25] = Analog(s, Buttons.Down);
        p[26] = Analog(s, Buttons.Right);
        p[27] = Analog(s, Buttons.Up);
        p[28] = Analog(s, Buttons.Square);
        p[29] = Analog(s, Buttons.Cross);
        p[30] = Analog(s, Buttons.Circle);
        p[31] = Analog(s, Buttons.Triangle);
        p[32] = Analog(s, Buttons.R1);
        p[33] = Analog(s, Buttons.L1);
        p[34] = s.R2;
        p[35] = s.L2;
        WriteTouch(p[36..], s.Touch1);
        WriteTouch(p[42..], s.Touch2);
        BinaryPrimitives.WriteUInt64LittleEndian(p[48..], micros);
        BinaryPrimitives.WriteSingleLittleEndian(p[56..], accel.X);
        BinaryPrimitives.WriteSingleLittleEndian(p[60..], accel.Y);
        BinaryPrimitives.WriteSingleLittleEndian(p[64..], accel.Z);
        BinaryPrimitives.WriteSingleLittleEndian(p[68..], gyro.X);
        BinaryPrimitives.WriteSingleLittleEndian(p[72..], gyro.Y);
        BinaryPrimitives.WriteSingleLittleEndian(p[76..], gyro.Z);
        return Seal(packet);
    }

    private static int Flag(InputState s, Buttons button, int bit) => s.IsDown(button) ? bit : 0;

    private static byte Analog(InputState s, Buttons button) => s.IsDown(button) ? (byte)255 : (byte)0;

    private static void WriteTouch(Span<byte> p, TouchPoint touch)
    {
        p[0] = (byte)(touch.Active ? 1 : 0);
        p[1] = touch.Id;
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], touch.X);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], touch.Y);
    }

    private static byte[] SyntheticMac(int slot) => [0x02, 0x54, 0x41, 0x43, 0x54, (byte)(slot + 1)];

    private static byte[]? ParseMac(string? mac)
    {
        string[]? parts = mac?.Split(':', '-');
        if (parts is not { Length: 6 })
        {
            return null;
        }

        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private readonly record struct Subscription(byte Flags, byte Slot, byte[] Mac, TimeSpan LastSeen)
    {
        /// <summary>Flag 0 = every slot, bit 0 = the named slot, bit 1 = the named MAC address.</summary>
        public bool Matches(int slot, byte[] mac) =>
            Flags == 0 || ((Flags & 1) != 0 && Slot == slot) || ((Flags & 2) != 0 && Mac.AsSpan().SequenceEqual(mac));

        public bool SameTarget(Subscription other) =>
            Flags == other.Flags && (Flags == 0 || (Slot == other.Slot && Mac.AsSpan().SequenceEqual(other.Mac)));
    }

    private sealed class SlotState
    {
        private const uint MaxSensorStep = 3_000_000;
        private uint _lastRaw;
        private ulong _ticks;
        private ulong _micros;
        private ulong _offset;
        private bool _started;
        private bool _everStarted;
        private TimeSpan? _lastTime;

        public bool Connected { get; set; }
        public Link Link { get; set; }
        public byte[] Mac { get; set; } = [];
        public Battery Battery { get; set; }
        public uint Counter { get; set; }

        /// <summary>A new connection counts its sensor ticks from its first report, on top of the time already reported.</summary>
        public void ResetClock()
        {
            _started = false;
            _lastTime = null;
        }

        /// <summary>
        /// Motion time in microseconds that never goes back: the controller's 0.333 µs sensor ticks
        /// (32-bit, so differences wrap), or the elapsed clock time while the controller sends none.
        /// </summary>
        public ulong NextMicros(uint raw, TimeSpan time)
        {
            // Zero is a valid counter value after a wrap; it only means "no sensor time" when no wrap is near.
            if (raw == 0 && !(_started && raw - _lastRaw < MaxSensorStep))
            {
                _everStarted = true;
                if (_lastTime is { } last && time > last)
                {
                    _micros += (ulong)((time - last).Ticks / 10);
                }

                _started = false;
            }
            else
            {
                if (!_started)
                {
                    // Restart: the time already reported is the base; only raw differences add to it.
                    // The very first report of a server run keeps the controller's own time.
                    _offset = _micros;
                    _ticks = _everStarted ? 0 : raw;
                    _started = _everStarted = true;
                }
                else
                {
                    uint delta = raw - _lastRaw;
                    // A step "backwards" is a counter that restarted, not 100 seconds of motion.
                    if (delta < int.MaxValue)
                    {
                        _ticks += delta;
                    }
                }

                _lastRaw = raw;
                _micros = Math.Max(_micros, _offset + _ticks / 3);
            }

            _lastTime = time;
            return _micros;
        }
    }
}
