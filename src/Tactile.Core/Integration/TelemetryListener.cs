using System.Net;
using System.Net.Sockets;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal sealed class TelemetryListener(
    Func<TimeSpan> now,
    Func<TelemetryConfig> activeRules,
    Action<Func<OutputState, OutputState>?> setLayer,
    Action<Notice> report) : IDisposable
{
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int PollMilliseconds = 10;
    private static readonly TimeSpan PacketLifetime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly ListenerWait _wait = new();
    private readonly LayerPublisher _layer = new(setLayer);
    private volatile Thread? _worker;
    private volatile Socket? _socket;
    private int _stopping;
    private ForzaListenerState _state;
    private bool _enabled;
    private bool _conflict;
    private int _port = new IntegrationSettings().ForzaPort;
    private int _configuredPort = new IntegrationSettings().ForzaPort;
    private TimeSpan _retryAt;
    private long _packets;
    private long _rejected;
    private TimeSpan? _lastPacket;
    private TelemetryFrame? _frame;
    private (TelemetryValues Values, TelemetryConfig Rules, bool BlinkOn)? _evaluated;
    private UdpState _pending = UdpState.Empty;

    private bool Stopping => Volatile.Read(ref _stopping) != 0;

    public ForzaStatus Status
    {
        get
        {
            lock (_lock)
            {
                ForzaListenerState state = Stopping ? ForzaListenerState.Off :
                    _state == ForzaListenerState.Listening && IsRecent(now()) ? ForzaListenerState.Receiving : _state;
                return new(state, _port, _frame?.Format, _packets, _rejected, _frame?.Values);
            }
        }
    }

    public void Configure(IntegrationSettings settings)
    {
        lock (_lock)
        {
            bool conflict = settings.ForzaPort == settings.UdpPort;
            if (Stopping || (_enabled == settings.ForzaEnabled && _configuredPort == settings.ForzaPort &&
                (!_enabled || _conflict == conflict)))
            {
                return;
            }

            _enabled = settings.ForzaEnabled;
            _conflict = conflict;
            _configuredPort = settings.ForzaPort;
            CloseSocket();
            _lastPacket = null;
            _state = _enabled && _conflict ? ForzaListenerState.PortConflict : ForzaListenerState.Off;
            if (_enabled && !_conflict)
            {
                if (_worker is null)
                {
                    var worker = new Thread(Run) { IsBackground = true, Name = "Tactile Forza" };
                    worker.Start();
                    _worker = worker;
                }

                TryBind(now());
            }

            _wait.Wake();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

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
                try
                {
                    UpdateLayer();
                }
                catch (Exception)
                {
                    lock (_lock)
                    {
                        if (!Stopping)
                        {
                            _rejected++;
                        }
                    }
                }

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
                        int retry = _enabled && !_conflict && listening is null
                            ? ListenerWait.Until(time, _retryAt)
                            : Timeout.Infinite;
                        wait = IsRecent(time) || _layer.Applied != UdpState.Empty
                            ? (retry == Timeout.Infinite ? PollMilliseconds : Math.Min(PollMilliseconds, retry))
                            : retry;
                        receive = _wait.Begin();
                    }

                    if (Stopping)
                    {
                        _wait.Wake();
                    }

                    if (_wait.Receive(listening, buffer, wait, receive) is { } packet)
                    {
                        lock (_lock)
                        {
                            if (Stopping || !ReferenceEquals(listening, _socket))
                            {
                                continue;
                            }

                            if (packet.RemoteEndPoint is IPEndPoint endpoint && IPAddress.IsLoopback(endpoint.Address) &&
                                ForzaPacket.TryParse(buffer.AsSpan(0, packet.ReceivedBytes), out TelemetryFrame frame))
                            {
                                _frame = frame;
                                _lastPacket = now();
                                _packets++;
                            }
                            else
                            {
                                _rejected++;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    lock (_lock)
                    {
                        if (!Stopping && (listening is null || ReferenceEquals(listening, _socket)))
                        {
                            _rejected++;
                        }
                    }
                }
            }
        }
        finally
        {
            LayerPublisher.Publication? publish;
            lock (_lock)
            {
                CloseSocket();
                _state = ForzaListenerState.Off;
                publish = _layer.Prepare(UdpState.Empty, now(), immediate: true);
            }

            try
            {
                _layer.Publish(publish, _lock);
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _rejected++;
                }
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
                candidate.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            candidate.Bind(new IPEndPoint(IPAddress.Loopback, _configuredPort));
            _port = ((IPEndPoint)candidate.LocalEndPoint!).Port;
        }
        catch (SocketException)
        {
            candidate?.Dispose();
            bool firstFailure = _state != ForzaListenerState.PortBusy;
            _state = ForzaListenerState.PortBusy;
            _retryAt = time + RetryInterval;
            if (firstFailure)
            {
                report(new Notice(NoticeKinds.Info, $"Forza telemetri portu {_port} kullanımda. Portu değiştirin."));
            }

            return;
        }

        _socket = candidate;
        if (Stopping)
        {
            CloseSocket();
            return;
        }

        _state = ForzaListenerState.Listening;
    }

    private void CloseSocket()
    {
        Socket? socket = _socket;
        _socket = null;
        socket?.Dispose();
        _port = _configuredPort;
    }

    private bool IsRecent(TimeSpan time) => _lastPacket is { } last && time - last < PacketLifetime;

    private void UpdateLayer()
    {
        bool needsRules;
        lock (_lock)
        {
            TimeSpan time = now();
            if (Stopping || (_layer.NextPublish is { } next && time < next))
            {
                return;
            }

            needsRules = _frame is { Values.RaceOn: 1 } && IsRecent(time);
        }

        TelemetryConfig? rules = needsRules ? activeRules() : null;
        LayerPublisher.Publication? publish;
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            TimeSpan time = now();
            if (rules is not null && _frame is { } frame && IsRecent(time))
            {
                bool blinkOn = TelemetryRules.BlinkOn(time);
                if (_evaluated is not { } previous || previous.Values != frame.Values ||
                    !ReferenceEquals(previous.Rules, rules) || previous.BlinkOn != blinkOn)
                {
                    _pending = TelemetryRules.Evaluate(rules, frame.Values, time);
                    _evaluated = (frame.Values, rules, blinkOn);
                }
            }
            else
            {
                _evaluated = null;
                _pending = UdpState.Empty;
            }

            publish = _layer.Prepare(_pending, time);
        }

        _layer.Publish(publish, _lock);
    }
}
