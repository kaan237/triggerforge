using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal sealed class UdpApiServer(
    Func<TimeSpan> now,
    Action<int, Func<OutputState, OutputState>?> setLayer,
    Action<int, string> activateProfile,
    Func<int, UdpControllerStatus> controllerStatus,
    Action<Notice> report,
    string dsxPortFilePath = DsxPortFile.DefaultPath,
    Func<IReadOnlyList<ControllerSlot>>? getSlots = null) : IDisposable
{
    private const int MaxPacketSize = 16 * 1024;
    private const int MaxCommands = 32;
    private const int MaxPacketsPerSecond = 500;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly ListenerWait _wait = new();
    private readonly LayerPublisher[] _layers = Enumerable.Range(0, 4)
        .Select(index => new LayerPublisher(layer => setLayer(index, layer))).ToArray();
    private readonly UdpState[] _states = Enumerable.Repeat(UdpState.Empty, 4).ToArray();
    private readonly TimeSpan?[] _lastAddressed = new TimeSpan?[4];
    private readonly Queue<TimeSpan> _arrivals = new(MaxPacketsPerSecond);
    private IntegrationSettings _settings = new();
    private volatile Thread? _worker;
    private volatile Socket? _socket;
    private int _stopping;
    private UdpListenerState _listener;
    private int _port = new IntegrationSettings().UdpPort;
    private TimeSpan _retryAt;
    private long _packets;
    private long _rejected;
    private long _unsupported;
    private TimeSpan? _lastPacket;
    private UdpFormat? _format;
    public UdpApiServer(Func<TimeSpan> now, Action<Func<OutputState, OutputState>?> setLayer,
        Action<string> activateProfile, Func<UdpControllerStatus> controllerStatus, Action<Notice> report,
        string dsxPortFilePath = DsxPortFile.DefaultPath)
        : this(now, (index, layer) => { if (index == 0) setLayer(layer); }, (_, id) => activateProfile(id),
            index => index == 0 ? controllerStatus() : default, report, dsxPortFilePath)
    {
    }

    private bool Stopping => Volatile.Read(ref _stopping) != 0;

    internal UdpState StateAt(ControllerSlot slot)
    {
        UdpState state = _layers[slot.Index].Applied;
        return ReferenceEquals(state.Owner, slot) ? state : UdpState.Empty;
    }

    public UdpStatus Status
    {
        get
        {
            lock (_lock)
            {
                long? age = _lastPacket is { } last ? (long)(now() - last).TotalMilliseconds : null;
                return new(_listener, _port, _packets, _rejected, _unsupported, age,
                    _layers[0].Applied != UdpState.Empty ||
                    (getSlots is null
                        ? _layers.Skip(1).Any(layer => layer.Applied != UdpState.Empty)
                        : getSlots().Any(slot => slot.Index > 0 && slot.Controller.Connected && StateAt(slot) != UdpState.Empty)), _format);
            }
        }
    }

    public void Configure(IntegrationSettings settings)
    {
        List<LayerUpdate> publish = [];
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            bool rebind = settings.UdpEnabled != _settings.UdpEnabled || settings.UdpPort != _settings.UdpPort;
            bool advertise = settings.Dsx && settings.DsxPortFile && !(_settings.Dsx && _settings.DsxPortFile);
            _settings = settings;
            if (rebind)
            {
                CloseSocket();
            }

            if (!settings.UdpEnabled)
            {
                Array.Fill(_states, UdpState.Empty);
                publish = PrepareLayers(now(), out _, immediate: true);
            }
            else if (rebind)
            {
                if (_worker is null)
                {
                    var worker = new Thread(Run) { IsBackground = true, Name = "TriggerForge: UDP" };
                    worker.Start();
                    _worker = worker;
                }

                if (!Stopping)
                {
                    TryBind(now());
                }
            }
            else if (advertise && _socket is not null)
            {
                WritePortFile();
            }

            _wait.Wake();
        }

        Publish(publish);
    }

    public void Release()
    {
        List<LayerUpdate> publish;
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            Array.Fill(_states, UdpState.Empty);
            publish = PrepareLayers(now(), out _);
            _wait.Wake();
        }

        Publish(publish);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        // Closing the socket does not wait for an in-flight output callback.
        _socket?.Dispose();
        _wait.Wake();
        if (_worker != Thread.CurrentThread)
        {
            _worker?.Join(TimeSpan.FromSeconds(1));
        }
    }

    private void Run()
    {
        // A complete UDP datagram fits even when it exceeds the protocol's limit.
        byte[] buffer = new byte[ushort.MaxValue];
        try
        {
            while (!Stopping)
            {
                Socket? listening = null;
                try
                {
                    List<LayerUpdate> publish;
                    CancellationTokenSource? receive = null;
                    int wait;
                    lock (_lock)
                    {
                        if (Stopping)
                        {
                            break;
                        }

                        TimeSpan time = now();
                        if (_settings.UdpEnabled && _socket is null && time >= _retryAt)
                        {
                            TryBind(time);
                        }

                        publish = PrepareLayers(time, out TimeSpan? deadline);
                        listening = _socket;
                        if (_settings.UdpEnabled && listening is null)
                        {
                            deadline = Earlier(deadline, _retryAt);
                        }

                        wait = ListenerWait.Until(time, deadline);
                        if (publish.Count == 0)
                        {
                            receive = _wait.Begin();
                        }
                    }

                    if (receive is null)
                    {
                        Publish(publish);
                        continue;
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
                    lock (_lock)
                    {
                        if (!Stopping && (listening is null || IsCurrent(listening)))
                        {
                            _rejected++;
                        }
                    }
                }
            }
        }
        finally
        {
            List<LayerUpdate> publish;
            lock (_lock)
            {
                CloseSocket();
                Array.Fill(_states, UdpState.Empty);
                publish = PrepareLayers(now(), out _, immediate: true);
            }

            try
            {
                Publish(publish);
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

    private bool IsCurrent(Socket socket) => !Stopping && ReferenceEquals(socket, _socket);

    private static TimeSpan? Earlier(TimeSpan? first, TimeSpan? second) =>
        first is null || (second is not null && second < first) ? second : first;

    private readonly record struct LayerUpdate(LayerPublisher Layer, LayerPublisher.Publication Publication);

    private IReadOnlyList<ControllerSlot>? CaptureOccupants()
    {
        IReadOnlyList<ControllerSlot>? slots = getSlots?.Invoke();
        if (slots is not null)
        {
            for (int i = 1; i < _states.Length; i++)
            {
                if (!ReferenceEquals(_states[i].Owner, Occupant(slots, i)))
                {
                    _states[i] = UdpState.Empty;
                    _lastAddressed[i] = null;
                }
            }
        }

        return slots;
    }

    private static ControllerSlot? Occupant(IReadOnlyList<ControllerSlot> slots, int index) =>
        slots.FirstOrDefault(s => s.Index == index);

    private bool Connected(IReadOnlyList<ControllerSlot>? slots, int index) =>
        slots is null ? controllerStatus(index).Connected : Occupant(slots, index)?.Controller.Connected == true;

    private void Addressed(IReadOnlyList<ControllerSlot>? slots, int index, TimeSpan time)
    {
        _lastAddressed[index] = time;
        if (slots is not null && index > 0 && _states[index] != UdpState.Empty)
        {
            UdpState state = _states[index] with { Owner = null };
            _states[index] = state == UdpState.Empty ? UdpState.Empty : state with { Owner = Occupant(slots, index) };
        }
    }

    private List<LayerUpdate> PrepareLayers(TimeSpan time, out TimeSpan? deadline, bool immediate = false)
    {
        CaptureOccupants();
        List<LayerUpdate> updates = [];
        deadline = null;
        for (int i = 0; i < _states.Length; i++)
        {
            if (_states[i] != UdpState.Empty && _settings.ReleaseAfterSeconds > 0 &&
                _lastAddressed[i] is { } last)
            {
                TimeSpan release = last + TimeSpan.FromSeconds(_settings.ReleaseAfterSeconds);
                if (time >= release)
                {
                    _states[i] = UdpState.Empty;
                }
                else
                {
                    deadline = Earlier(deadline, release);
                }
            }

            LayerPublisher layer = _layers[i];
            if (layer.Prepare(_states[i], time, immediate) is { } update)
            {
                updates.Add(new(layer, update));
            }

            if (layer.HasPending(_states[i]))
            {
                deadline = Earlier(deadline, layer.NextPublish);
            }
        }

        return updates;
    }

    private void Publish(List<LayerUpdate> updates)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (LayerUpdate update in updates)
        {
            try
            {
                update.Layer.Publish(update.Publication, _lock);
            }
            catch (Exception e)
            {
                failure ??= ExceptionDispatchInfo.Capture(e);
            }
        }

        failure?.Throw();
    }

    private void CloseSocket()
    {
        _socket?.Dispose();
        _socket = null;
        _listener = UdpListenerState.Off;
        _port = _settings.UdpPort;
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
                // Without this, an answer to a sender that already closed its socket makes the next receive fail.
                candidate.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            candidate.Bind(new IPEndPoint(IPAddress.Loopback, _settings.UdpPort));
            _port = ((IPEndPoint)candidate.LocalEndPoint!).Port;
        }
        catch (SocketException)
        {
            candidate?.Dispose();
            bool firstFailure = _listener != UdpListenerState.PortBusy;
            _listener = UdpListenerState.PortBusy;
            _retryAt = time + RetryInterval;
            if (firstFailure)
            {
                report(new Notice(NoticeKinds.Info,
                    Text.Pick($"UDP port {_port} is in use. Close DSX if it is running, or choose another port.", $"UDP portu {_port} kullanımda. DSX açıksa kapatın ya da portu değiştirin.")));
            }

            return;
        }

        _socket = candidate;
        if (Stopping)
        {
            CloseSocket();
            return;
        }

        _listener = UdpListenerState.Listening;
        if (_settings.Dsx && _settings.DsxPortFile)
        {
            WritePortFile();
        }
    }

    private void WritePortFile()
    {
        try
        {
            DsxPortFile.Write(dsxPortFilePath, _port);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            report(new Notice(NoticeKinds.Info, Text.Pick("Could not write the DSX port file.", "DSX port dosyası yazılamadı.")));
        }
    }

    private void HandlePacket(Socket socket, SocketReceiveFromResult packet, byte[] buffer)
    {
        TimeSpan time;
        lock (_lock)
        {
            if (!IsCurrent(socket))
            {
                return;
            }

            time = now();
            while (_arrivals.TryPeek(out TimeSpan first) && time - first >= RateWindow)
            {
                _arrivals.Dequeue();
            }

            if (_arrivals.Count >= MaxPacketsPerSecond)
            {
                _rejected++;
                return;
            }

            _arrivals.Enqueue(time);
            if (packet.ReceivedBytes > MaxPacketSize ||
                packet.RemoteEndPoint is not IPEndPoint endpoint || !IPAddress.IsLoopback(endpoint.Address))
            {
                _rejected++;
                return;
            }
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.AsMemory(0, packet.ReceivedBytes),
            new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("tactile", out JsonElement version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int v) && v == 1)
            {
                if (TryCommands(root, "commands", out JsonElement commands))
                {
                    if (ProcessNative(commands, time, socket) is { } reply)
                    {
                        SendAnswer(socket, packet.RemoteEndPoint, JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            ok = reply.Error is null,
                            error = reply.Error,
                            connected = reply.Controller.Connected,
                            link = reply.Controller.Link,
                            battery = reply.Controller.Battery,
                        }, TactileJson.Compact));
                    }

                    return;
                }
            }
            else if (TryCommands(root, "instructions", out JsonElement instructions))
            {
                if (ProcessDsx(instructions, time, socket) is { } controller)
                {
                    SendAnswer(socket, packet.RemoteEndPoint, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Status = "TriggerForge: commands received",
                        TimeReceived = DateTime.Now.ToString(),
                        isControllerConnected = controller.Connected,
                        BatteryLevel = controller.Battery ?? 0,
                    }));
                }

                return;
            }
        }

        lock (_lock)
        {
            if (IsCurrent(socket))
            {
                _rejected++;
            }
        }
    }

    internal (string? Error, UdpControllerStatus Controller)? ProcessNative(
        JsonElement commands, TimeSpan time, Socket? socket = null)
    {
        JsonElement.ArrayEnumerator remaining = commands.EnumerateArray();
        string? error = null;
        string? profileId;
        int index = 0;
        try
        {
            do
            {
                lock (_lock)
                {
                    if (socket is not null && !IsCurrent(socket))
                    {
                        return null;
                    }

                    IReadOnlyList<ControllerSlot>? slots = CaptureOccupants();
                    profileId = NativeCommands.ApplyUntilProfile(ref remaining, _states,
                        slot => Addressed(slots, slot, time), ref index, slot => Connected(slots, slot));
                }

                if (profileId is not null)
                {
                    activateProfile(index, profileId);
                }
            }
            while (profileId is not null);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException)
        {
            error = NativeCommands.ErrorMessage(e);
        }

        lock (_lock)
        {
            if (socket is not null && !IsCurrent(socket))
            {
                return null;
            }

            _lastPacket = time;
            _format = UdpFormat.Native;
            if (error is null)
            {
                _packets++;
            }
            else
            {
                _rejected++;
            }
        }

        return (error, controllerStatus(index));
    }

    internal UdpControllerStatus? ProcessDsx(JsonElement instructions, TimeSpan time, Socket? socket = null)
    {
        lock (_lock)
        {
            if (socket is not null && !IsCurrent(socket))
            {
                return null;
            }

            if (!_settings.Dsx)
            {
                _rejected++;
                return null;
            }

            IReadOnlyList<ControllerSlot>? slots = CaptureOccupants();
            _unsupported += DsxTranslator.Apply(instructions, _states,
                index => Connected(slots, index), index => Addressed(slots, index, time));
            _packets++;
            _lastPacket = time;
            _format = UdpFormat.Dsx;
        }

        return controllerStatus(0);
    }

    private void SendAnswer(Socket socket, EndPoint sender, byte[] answer)
    {
        PublishPending(socket);
        if (IsCurrent(socket))
        {
            socket.SendTo(answer, sender);
        }
    }

    internal void PublishPending(Socket? socket = null)
    {
        List<LayerUpdate> publish;
        lock (_lock)
        {
            if (socket is not null && !IsCurrent(socket))
            {
                return;
            }

            publish = PrepareLayers(now(), out _);
        }

        Publish(publish);
    }

    private static bool TryCommands(JsonElement root, string name, out JsonElement commands) =>
        root.TryGetProperty(name, out commands) && commands.ValueKind == JsonValueKind.Array &&
        commands.GetArrayLength() <= MaxCommands;
}
