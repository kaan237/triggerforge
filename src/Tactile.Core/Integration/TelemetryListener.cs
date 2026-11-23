using System.Net;
using System.Net.Sockets;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

/// <summary>
/// Receives racing telemetry from the enabled sources, each on its own worker so one failing source never
/// stops the others, and lets the most recent active frame drive the telemetry output layer.
/// </summary>
internal sealed class TelemetryListener : IDisposable
{
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int PollMilliseconds = 10;
    private const int AcPollMilliseconds = 16;
    private static readonly TimeSpan PacketLifetime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Func<TimeSpan> _now;
    private readonly Func<TelemetryConfig> _activeRules;
    private readonly Action<Notice> _report;
    private readonly ISharedMemorySource? _sharedMemory;
    private readonly Lock _lock = new();
    private readonly LayerPublisher _layer;
    private readonly ForzaChannel _forza;
    private readonly F1Channel _f1;
    private readonly BeamNgChannel _beamNg;
    private readonly AcChannel _ac;
    private int _stopping;
    private TelemetrySource? _active;
    private TelemetrySource? _driver;
    private TelemetryValues? _activeValues;
    private TimeSpan _activeAt;
    private (TelemetryValues Values, TelemetryConfig Rules, bool BlinkOn)? _evaluated;
    private UdpState _pending = UdpState.Empty;

    public TelemetryListener(
        Func<TimeSpan> now,
        Func<TelemetryConfig> activeRules,
        Action<Func<OutputState, OutputState>?> setLayer,
        Action<Notice> report,
        ISharedMemorySource? sharedMemory = null)
    {
        _now = now;
        _activeRules = activeRules;
        _report = report;
        _sharedMemory = sharedMemory;
        _layer = new LayerPublisher(setLayer);
        _forza = new ForzaChannel(this);
        _f1 = new F1Channel(this);
        _beamNg = new BeamNgChannel(this);
        _ac = new AcChannel(this);
    }

    private bool Stopping => Volatile.Read(ref _stopping) != 0;

    public ForzaStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new(_forza.StateAt(_now()), _forza.Port, _forza.ForzaFormat, _forza.Packets, _forza.Rejected, _forza.Values);
            }
        }
    }

    public F1Status F1Status
    {
        get
        {
            lock (_lock)
            {
                return new(_f1.StateAt(_now()), _f1.Port, _f1.Session.Format, _f1.Packets, _f1.Rejected, _f1.Unsupported, _f1.Values);
            }
        }
    }

    public BeamNgStatus BeamNgStatus
    {
        get
        {
            lock (_lock)
            {
                return new(_beamNg.StateAt(_now()), _beamNg.Port, _beamNg.Packets, _beamNg.Rejected, _beamNg.Values);
            }
        }
    }

    public AcStatus AcStatus
    {
        get
        {
            lock (_lock)
            {
                AcListenerState state = Stopping || !_ac.Enabled ? AcListenerState.Off :
                    _ac.IsRecent(_now()) ? AcListenerState.Receiving : AcListenerState.Listening;
                return new(state, state == AcListenerState.Receiving ? _ac.Game : null, _ac.Packets, _ac.Values);
            }
        }
    }

    public TelemetrySource? ActiveSource
    {
        get
        {
            lock (_lock)
            {
                return !Stopping && CurrentValues(_now()) is not null ? _active : null;
            }
        }
    }

    // Zero is the "any free port" request used by tests and never counts as a clash.
    internal static bool SamePort(int a, int b) => a != 0 && a == b;

    public void Configure(IntegrationSettings settings)
    {
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            // Two passes, so every changed channel lets go of its port before any channel binds a new one.
            _beamNg.Reconfigure(settings.BeamNgEnabled, settings.BeamNgPort,
                SamePort(settings.BeamNgPort, settings.UdpPort) ||
                (settings.ForzaEnabled && SamePort(settings.BeamNgPort, settings.ForzaPort)) ||
                (settings.F1Enabled && SamePort(settings.BeamNgPort, settings.F1Port)));
            _f1.Reconfigure(settings.F1Enabled, settings.F1Port,
                SamePort(settings.F1Port, settings.UdpPort) ||
                (settings.ForzaEnabled && SamePort(settings.F1Port, settings.ForzaPort)));
            _forza.Reconfigure(settings.ForzaEnabled, settings.ForzaPort, SamePort(settings.ForzaPort, settings.UdpPort));
            _beamNg.Bind();
            _f1.Bind();
            _forza.Bind();
            _ac.Configure(settings.AcEnabled);
        }
    }

    /// <summary>Closes the sockets that hold the port without binding anything. The next Configure rebinds them.</summary>
    public void Release(int port)
    {
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            _beamNg.Release(port);
            _f1.Release(port);
            _forza.Release(port);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        Channel[] channels = [_forza, _f1, _beamNg, _ac];
        // The lock waits for a Configure that is already running, so its worker is registered before the joins.
        lock (_lock)
        {
            foreach (Channel channel in channels)
            {
                channel.Stop();
            }
        }

        foreach (Channel channel in channels)
        {
            channel.Join();
        }
    }

    private TelemetryValues? CurrentValues(TimeSpan time) =>
        _active is not null && _activeValues is { RaceOn: 1 } values && time - _activeAt < PacketLifetime ? values : null;

    // A frame with RaceOn 1 takes over; one with RaceOn 0 only ends the source that is driving the layer.
    // A repeated frame (takeOver false) only keeps its own source alive.
    private void Arbitrate(TelemetrySource source, TelemetryValues values, TimeSpan time, bool takeOver = true)
    {
        if (values.RaceOn == 1)
        {
            if (!takeOver && _active != source)
            {
                return;
            }

            _active = source;
            _driver = source;
            _activeValues = values;
            _activeAt = time;
        }
        else if (_active == source)
        {
            _active = null;
            _activeValues = null;
        }
    }

    private void ClearActive(TelemetrySource first, TelemetrySource? second = null)
    {
        if (_active is { } active && (active == first || active == second))
        {
            _active = null;
            _activeValues = null;
        }
    }

    // Only the source that drives the layer wakes fast: it has frames to expire or a layer left to clear.
    private bool NeedsFastWake(TimeSpan time, TelemetrySource first, TelemetrySource? second = null) =>
        _driver is { } driver && (driver == first || driver == second) &&
        (CurrentValues(time) is not null || _layer.Applied != UdpState.Empty || _layer.HasPending(UdpState.Empty));

    private void PublishEmpty()
    {
        LayerPublisher.Publication? publish;
        lock (_lock)
        {
            publish = _layer.Prepare(UdpState.Empty, _now(), immediate: true);
        }

        try
        {
            _layer.Publish(publish, _lock);
        }
        catch (Exception)
        {
            // The layer owner is already shutting down.
        }
    }

    private void UpdateLayer()
    {
        bool needsRules;
        lock (_lock)
        {
            TimeSpan time = _now();
            if (Stopping || (_layer.NextPublish is { } next && time < next))
            {
                return;
            }

            needsRules = CurrentValues(time) is not null;
        }

        TelemetryConfig? rules = needsRules ? _activeRules() : null;
        LayerPublisher.Publication? publish;
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            TimeSpan time = _now();
            if (rules is null && CurrentValues(time) is not null)
            {
                // A source took over after the first check; the next pass has the rules for it.
                return;
            }

            if (rules is not null && CurrentValues(time) is { } values)
            {
                bool blinkOn = TelemetryRules.BlinkOn(time);
                if (_evaluated is not { } previous || previous.Values != values ||
                    !ReferenceEquals(previous.Rules, rules) || previous.BlinkOn != blinkOn)
                {
                    _pending = TelemetryRules.Evaluate(rules, values, time);
                    _evaluated = (values, rules, blinkOn);
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

    private abstract class Channel(TelemetryListener owner)
    {
        protected TelemetryListener Owner { get; } = owner;
        protected volatile Thread? Worker;

        public abstract void Stop();

        public void Join()
        {
            Thread? worker = Worker;
            if (worker is not null && worker != Thread.CurrentThread)
            {
                worker.Join(TimeSpan.FromSeconds(1));
            }
        }

        // A failure here is counted on this channel only and never reaches the others.
        protected void RefreshLayer()
        {
            try
            {
                Owner.UpdateLayer();
            }
            catch (Exception)
            {
                lock (Owner._lock)
                {
                    if (!Owner.Stopping)
                    {
                        OnError();
                    }
                }
            }
        }

        protected abstract void OnError();
    }

    private abstract class UdpChannel(TelemetryListener owner, TelemetrySource source, string label, int defaultPort)
        : Channel(owner)
    {
        private readonly ListenerWait _wait = new();
        private readonly string _label = label;
        private volatile Socket? _socket;
        private ForzaListenerState _state;
        private bool _enabled;
        private bool _conflict;
        private bool _bindPending;
        private int _configuredPort = defaultPort;
        private TimeSpan _retryAt;
        private TimeSpan? _lastPacket;

        public int Port { get; private set; } = defaultPort;
        public long Packets { get; private set; }
        public long Rejected { get; private set; }
        public long Unsupported { get; private set; }
        public TelemetryValues? Values { get; private set; }

        protected abstract string LabelTr { get; }

        public ForzaListenerState StateAt(TimeSpan time) => Owner.Stopping ? ForzaListenerState.Off :
            _state == ForzaListenerState.Listening && IsRecent(time) ? ForzaListenerState.Receiving : _state;

        // First pass: let go of the old socket when the configuration changed. Nothing is bound here.
        public void Reconfigure(bool enabled, int port, bool conflict)
        {
            bool unbound = _enabled && !_conflict && _socket is null && _state == ForzaListenerState.Off;
            if (_enabled == enabled && _configuredPort == port && (!_enabled || _conflict == conflict) && !unbound)
            {
                return;
            }

            _enabled = enabled;
            _conflict = conflict;
            _configuredPort = port;
            CloseSocket();
            _lastPacket = null;
            Owner.ClearActive(source);
            OnReset();
            _state = _enabled && _conflict ? ForzaListenerState.PortConflict : ForzaListenerState.Off;
            _bindPending = _enabled && !_conflict;
            _wait.Wake();
        }

        // Second pass: bind what the first pass released.
        public void Bind()
        {
            if (!_bindPending)
            {
                return;
            }

            _bindPending = false;
            if (!_enabled || _conflict)
            {
                return;
            }

            if (Worker is null)
            {
                var worker = new Thread(Run) { IsBackground = true, Name = $"TriggerForge: {_label}" };
                worker.Start();
                Worker = worker;
            }

            TryBind(Owner._now());
            _wait.Wake();
        }

        public void Release(int port)
        {
            if (_socket is null || !SamePort(Port, port))
            {
                return;
            }

            CloseSocket();
            _state = ForzaListenerState.Off;
            // The worker must not take the port back before the next Configure decides who gets it.
            _retryAt = Owner._now() + RetryInterval;
            _wait.Wake();
        }

        public override void Stop()
        {
            _socket?.Dispose();
            _wait.Wake();
        }

        protected enum Outcome { Rejected, Unsupported, Ignored, Frame }

        protected abstract Outcome Handle(ReadOnlySpan<byte> packet, out TelemetryValues? values);

        protected virtual void OnReset()
        {
        }

        protected override void OnError() => Rejected++;

        private bool IsRecent(TimeSpan time) => _lastPacket is { } last && time - last < PacketLifetime;

        private void Run()
        {
            byte[] buffer = new byte[ushort.MaxValue];
            try
            {
                while (!Owner.Stopping)
                {
                    RefreshLayer();

                    Socket? listening = null;
                    try
                    {
                        int wait;
                        CancellationTokenSource receive;
                        lock (Owner._lock)
                        {
                            if (Owner.Stopping)
                            {
                                break;
                            }

                            TimeSpan time = Owner._now();
                            if (_enabled && !_conflict && _socket is null && time >= _retryAt)
                            {
                                TryBind(time);
                            }

                            listening = _socket;
                            int retry = _enabled && !_conflict && listening is null
                                ? ListenerWait.Until(time, _retryAt)
                                : Timeout.Infinite;
                            wait = Owner.NeedsFastWake(time, source)
                                ? (retry == Timeout.Infinite ? PollMilliseconds : Math.Min(PollMilliseconds, retry))
                                : retry;
                            receive = _wait.Begin();
                        }

                        if (Owner.Stopping)
                        {
                            _wait.Wake();
                        }

                        if (_wait.Receive(listening, buffer, wait, receive) is { } packet)
                        {
                            lock (Owner._lock)
                            {
                                if (Owner.Stopping || !ReferenceEquals(listening, _socket))
                                {
                                    continue;
                                }

                                Accept(packet, buffer);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        lock (Owner._lock)
                        {
                            if (!Owner.Stopping && (listening is null || ReferenceEquals(listening, _socket)))
                            {
                                Rejected++;
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (Owner._lock)
                {
                    CloseSocket();
                    _state = ForzaListenerState.Off;
                }

                Owner.PublishEmpty();
            }
        }

        private void Accept(SocketReceiveFromResult packet, byte[] buffer)
        {
            if (packet.RemoteEndPoint is not IPEndPoint endpoint || !IPAddress.IsLoopback(endpoint.Address))
            {
                Rejected++;
                return;
            }

            Outcome outcome = Handle(buffer.AsSpan(0, packet.ReceivedBytes), out TelemetryValues? values);
            switch (outcome)
            {
                case Outcome.Rejected:
                    Rejected++;
                    return;
                case Outcome.Unsupported:
                    Unsupported++;
                    return;
            }

            TimeSpan time = Owner._now();
            _lastPacket = time;
            Packets++;
            if (outcome == Outcome.Frame && values is not null)
            {
                Values = values;
                Owner.Arbitrate(source, values, time);
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
                Port = ((IPEndPoint)candidate.LocalEndPoint!).Port;
            }
            catch (SocketException)
            {
                candidate?.Dispose();
                bool firstFailure = _state != ForzaListenerState.PortBusy;
                _state = ForzaListenerState.PortBusy;
                _retryAt = time + RetryInterval;
                if (firstFailure)
                {
                    Owner._report(new Notice(NoticeKinds.Info, Text.Pick(
                        $"{_label} telemetry port {Port} is in use. Choose another port.",
                        $"{LabelTr} telemetri portu {Port} kullanımda. Portu değiştirin.")));
                }

                return;
            }

            _socket = candidate;
            if (Owner.Stopping)
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
            Port = _configuredPort;
        }
    }

    private sealed class ForzaChannel(TelemetryListener owner)
        : UdpChannel(owner, TelemetrySource.Forza, "Forza", new IntegrationSettings().ForzaPort)
    {
        public ForzaFormat? ForzaFormat { get; private set; }

        protected override string LabelTr => "Forza";

        protected override Outcome Handle(ReadOnlySpan<byte> packet, out TelemetryValues? values)
        {
            if (!ForzaPacket.TryParse(packet, out TelemetryFrame frame))
            {
                values = null;
                return Outcome.Rejected;
            }

            ForzaFormat = frame.Format;
            values = frame.Values;
            return Outcome.Frame;
        }
    }

    private sealed class F1Channel(TelemetryListener owner)
        : UdpChannel(owner, TelemetrySource.F1, "F1", new IntegrationSettings().F1Port)
    {
        public F1TelemetrySession Session { get; private set; } = new();

        protected override string LabelTr => "F1";

        protected override Outcome Handle(ReadOnlySpan<byte> packet, out TelemetryValues? values) =>
            Session.TryParse(packet, out values) switch
            {
                F1PacketResult.Updated => Outcome.Frame,
                F1PacketResult.Ignored => Outcome.Ignored,
                F1PacketResult.Unsupported => Outcome.Unsupported,
                _ => Outcome.Rejected,
            };

        protected override void OnReset() => Session = new();
    }

    private sealed class BeamNgChannel(TelemetryListener owner)
        : UdpChannel(owner, TelemetrySource.BeamNg, "BeamNG", new IntegrationSettings().BeamNgPort)
    {
        private BeamNgOutGauge _gauge = new();

        protected override string LabelTr => "BeamNG";

        protected override Outcome Handle(ReadOnlySpan<byte> packet, out TelemetryValues? values)
        {
            bool ok = _gauge.TryParse(packet, out TelemetryValues parsed);
            values = ok ? parsed : null;
            return ok ? Outcome.Frame : Outcome.Rejected;
        }

        protected override void OnReset() => _gauge = new();
    }

    private sealed class AcChannel(TelemetryListener owner) : Channel(owner)
    {
        private readonly ManualResetEventSlim _signal = new(false);
        private TimeSpan? _lastFrame;

        public bool Enabled { get; private set; }
        public AcGame? Game { get; private set; }
        public long Packets { get; private set; }
        public TelemetryValues? Values { get; private set; }

        public bool IsRecent(TimeSpan time) => _lastFrame is { } last && time - last < PacketLifetime;

        public void Configure(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            _lastFrame = null;
            Game = null;
            Owner.ClearActive(TelemetrySource.Acc, TelemetrySource.Ac);
            if (enabled && Worker is null)
            {
                var worker = new Thread(Run) { IsBackground = true, Name = "TriggerForge: Assetto Corsa" };
                worker.Start();
                Worker = worker;
            }

            _signal.Set();
        }

        public override void Stop() => _signal.Set();

        protected override void OnError()
        {
        }

        private void Run()
        {
            AccSharedMemoryReader? reader = null;
            try
            {
                while (!Owner.Stopping)
                {
                    try
                    {
                        RefreshLayer();

                        bool enabled;
                        bool fast;
                        lock (Owner._lock)
                        {
                            _signal.Reset();
                            if (Owner.Stopping)
                            {
                                break;
                            }

                            enabled = Enabled;
                            fast = Owner.NeedsFastWake(Owner._now(), TelemetrySource.Acc, TelemetrySource.Ac);
                        }

                        int wait = Timeout.Infinite;
                        if (!enabled)
                        {
                            DisposeReader(ref reader);
                        }
                        else
                        {
                            if (Poll(ref reader))
                            {
                                // The accepted frame reaches the layer before the next wait.
                                RefreshLayer();
                            }

                            wait = reader is null ? AcPollMilliseconds
                                : (int)Math.Clamp(reader.NextWait().TotalMilliseconds, 1, AccSharedMemory.OpenRetryInterval.TotalMilliseconds);
                        }

                        if (fast)
                        {
                            // A layer that is still applied has to be cleared on time, even with the game closed.
                            wait = wait == Timeout.Infinite ? AcPollMilliseconds : Math.Min(wait, AcPollMilliseconds);
                        }

                        _signal.Wait(wait);
                    }
                    catch (Exception)
                    {
                        // Only Stopping ends the worker; a short pause keeps a repeating failure from spinning.
                        _signal.Wait(100);
                    }
                }
            }
            finally
            {
                DisposeReader(ref reader);
                lock (Owner._lock)
                {
                    Game = null;
                    _lastFrame = null;
                }

                Owner.PublishEmpty();
            }
        }

        private static void DisposeReader(ref AccSharedMemoryReader? reader)
        {
            try
            {
                reader?.Dispose();
            }
            catch (Exception)
            {
                // A reader that fails to close is dropped; the channel keeps running.
            }

            reader = null;
        }

        private bool Poll(ref AccSharedMemoryReader? reader)
        {
            try
            {
                reader ??= new AccSharedMemoryReader(Owner._now, Owner._sharedMemory);
                if (!reader.TryPoll(out AcTelemetryFrame frame, out bool changed))
                {
                    return false;
                }

                lock (Owner._lock)
                {
                    if (Owner.Stopping || !Enabled)
                    {
                        return false;
                    }

                    Accept(frame, changed, Owner._now());
                    return true;
                }
            }
            catch (Exception)
            {
                // The reader is rebuilt on the next pass; the other sources keep running.
                DisposeReader(ref reader);
                return false;
            }
        }

        private void Accept(AcTelemetryFrame frame, bool changed, TimeSpan time)
        {
            double revLights = double.IsFinite(frame.RevLights) ? Math.Clamp(frame.RevLights, 0, 1) : 0;
            int drs = frame.Drs != 0 ? 1 : 0;
            TelemetryValues values = frame.Values.RevLights == revLights && frame.Values.Drs == drs
                ? frame.Values
                : frame.Values with { RevLights = revLights, Drs = drs };
            Game = frame.IsAcc ? AcGame.Acc : AcGame.Ac;
            Values = values;
            if (changed)
            {
                Packets++;
            }

            // The single frame that ends a session drops the card back to listening.
            _lastFrame = values.RaceOn == 1 ? time : null;
            if (values.RaceOn == 0)
            {
                // The end of a session clears whichever of the two sources was driving, even after a detection change.
                Owner.ClearActive(TelemetrySource.Acc, TelemetrySource.Ac);
            }
            else
            {
                Owner.Arbitrate(frame.IsAcc ? TelemetrySource.Acc : TelemetrySource.Ac, values, time, takeOver: changed);
            }
        }
    }
}
