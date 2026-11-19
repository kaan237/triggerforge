using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal sealed class UdpApiServer(
    Func<TimeSpan> now,
    Action<Func<OutputState, OutputState>?> setLayer,
    Action<string> activateProfile,
    Func<UdpControllerStatus> controllerStatus,
    Action<Notice> report,
    string dsxPortFilePath = DsxPortFile.DefaultPath) : IDisposable
{
    private const int MaxPacketSize = 16 * 1024;
    private const int MaxCommands = 32;
    private const int MaxPacketsPerSecond = 500;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly ListenerWait _wait = new();
    private readonly LayerPublisher _layer = new(setLayer);
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
    private UdpState _state = UdpState.Empty;

    private bool Stopping => Volatile.Read(ref _stopping) != 0;

    public UdpStatus Status
    {
        get
        {
            lock (_lock)
            {
                long? age = _lastPacket is { } last ? (long)(now() - last).TotalMilliseconds : null;
                return new(_listener, _port, _packets, _rejected, _unsupported, age, _layer.Applied != UdpState.Empty, _format);
            }
        }
    }

    public void Configure(IntegrationSettings settings)
    {
        LayerPublisher.Publication? publish = null;
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
                _state = UdpState.Empty;
                publish = _layer.Prepare(_state, now(), immediate: true);
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

        _layer.Publish(publish, _lock);
    }

    public void Release()
    {
        LayerPublisher.Publication? publish;
        lock (_lock)
        {
            if (Stopping)
            {
                return;
            }

            _state = UdpState.Empty;
            publish = _layer.Prepare(_state, now());
            _wait.Wake();
        }

        _layer.Publish(publish, _lock);
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
                    LayerPublisher.Publication? publish;
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

                        TimeSpan? releaseAt = _state != UdpState.Empty && _settings.ReleaseAfterSeconds > 0
                            ? _lastPacket + TimeSpan.FromSeconds(_settings.ReleaseAfterSeconds)
                            : null;
                        if (releaseAt is { } release && time >= release)
                        {
                            _state = UdpState.Empty;
                            releaseAt = null;
                        }

                        publish = _layer.Prepare(_state, time);
                        listening = _socket;
                        TimeSpan? deadline = releaseAt;
                        if (_layer.HasPending(_state))
                        {
                            deadline = Earlier(deadline, _layer.NextPublish);
                        }

                        if (_settings.UdpEnabled && listening is null)
                        {
                            deadline = Earlier(deadline, _retryAt);
                        }

                        wait = ListenerWait.Until(time, deadline);
                        if (publish is null)
                        {
                            receive = _wait.Begin();
                        }
                    }

                    if (receive is null)
                    {
                        _layer.Publish(publish, _lock);
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
            LayerPublisher.Publication? publish;
            lock (_lock)
            {
                CloseSocket();
                _state = UdpState.Empty;
                publish = _layer.Prepare(_state, now(), immediate: true);
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

    private bool IsCurrent(Socket socket) => !Stopping && ReferenceEquals(socket, _socket);

    private static TimeSpan? Earlier(TimeSpan? first, TimeSpan? second) =>
        first is null || second < first ? second : first;

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
                    HandleNative(socket, packet.RemoteEndPoint, commands, time);
                    return;
                }
            }
            else if (TryCommands(root, "instructions", out JsonElement instructions))
            {
                HandleDsx(socket, packet.RemoteEndPoint, instructions, time);
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

    private void HandleNative(Socket socket, EndPoint sender, JsonElement commands, TimeSpan time)
    {
        JsonElement.ArrayEnumerator remaining = commands.EnumerateArray();
        string? error = null;
        string? profileId;
        try
        {
            do
            {
                lock (_lock)
                {
                    if (!IsCurrent(socket))
                    {
                        return;
                    }

                    profileId = NativeCommands.ApplyUntilProfile(ref remaining, ref _state);
                }

                if (profileId is not null)
                {
                    activateProfile(profileId);
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
            if (!IsCurrent(socket))
            {
                return;
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

        UdpControllerStatus controller = controllerStatus();
        byte[] answer = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ok = error is null,
            error,
            connected = controller.Connected,
            link = controller.Link,
            battery = controller.Battery,
        }, TactileJson.Compact);
        SendAnswer(socket, sender, answer);
    }

    private void HandleDsx(Socket socket, EndPoint sender, JsonElement instructions, TimeSpan time)
    {
        lock (_lock)
        {
            if (!IsCurrent(socket))
            {
                return;
            }

            if (!_settings.Dsx)
            {
                _rejected++;
                return;
            }

            (UdpState state, int unsupported) = DsxTranslator.Apply(instructions, _state);
            _state = state;
            _unsupported += unsupported;
            _packets++;
            _lastPacket = time;
            _format = UdpFormat.Dsx;        }

        UdpControllerStatus controller = controllerStatus();
        byte[] answer = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Status = "TriggerForge: commands received",
            TimeReceived = DateTime.Now.ToString(),
            isControllerConnected = controller.Connected,
            BatteryLevel = controller.Battery ?? 0,
        });
        SendAnswer(socket, sender, answer);
    }

    private void SendAnswer(Socket socket, EndPoint sender, byte[] answer)
    {
        LayerPublisher.Publication? publish;
        lock (_lock)
        {
            if (!IsCurrent(socket))
            {
                return;
            }

            publish = _layer.Prepare(_state, now());
        }

        _layer.Publish(publish, _lock);
        if (IsCurrent(socket))
        {
            socket.SendTo(answer, sender);
        }
    }

    private static bool TryCommands(JsonElement root, string name, out JsonElement commands) =>
        root.TryGetProperty(name, out commands) && commands.ValueKind == JsonValueKind.Array &&
        commands.GetArrayLength() <= MaxCommands;
}
