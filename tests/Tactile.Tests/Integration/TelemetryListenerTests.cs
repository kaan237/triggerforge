using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Integration;

public sealed class TelemetryListenerTests
{
    [Fact]
    public async Task Valid_frames_drive_layer_until_race_stops_or_a_second_passes()
    {
        using var fixture = new Fixture();
        fixture.Listener.Configure(fixture.Settings);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Null(fixture.Listener.Status.Values);
        using UdpClient sender = fixture.Connect();
        await sender.SendAsync(ForzaPacketTests.Packet(324).AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);

        ForzaStatus status = fixture.Listener.Status;
        Assert.Equal(ForzaListenerState.Receiving, status.State);
        Assert.Equal(1, status.Packets);
        JsonElement json = JsonSerializer.SerializeToElement(status, TactileJson.Compact);
        Assert.Equal("fh", json.GetProperty("format").GetString());
        Assert.Equal(72, json.GetProperty("values").GetProperty("speedKmh").GetDouble());

        fixture.Advance(10);
        await sender.SendAsync(ForzaPacketTests.Packet(raceOn: 0).AsMemory());
        await Until(() => fixture.Listener.Status.Packets == 2 && fixture.Output.RumbleRight == 0);
        Assert.Equal(ForzaListenerState.Receiving, fixture.Listener.Status.State);

        fixture.Advance(10);
        await sender.SendAsync(ForzaPacketTests.Packet().AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);
        fixture.Advance(999);
        await sender.SendAsync(new byte[20 * 1024].AsMemory());
        await Until(() => fixture.Listener.Status.Rejected == 1);
        Assert.Equal(ForzaListenerState.Receiving, fixture.Listener.Status.State);
        Assert.Equal(50, fixture.Output.RumbleRight);

        fixture.Advance(1);
        await Until(() => fixture.Output.RumbleRight == 0);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(3, fixture.Listener.Status.Packets);
        Assert.Equal(0.5, fixture.Listener.Status.Values!.Rpm);
    }

    [Fact]
    public async Task Latest_frame_wins_within_publish_interval_and_empty_profile_releases_it()
    {
        using var fixture = new Fixture();
        fixture.Listener.Configure(fixture.Settings);
        using UdpClient sender = fixture.Connect();
        await sender.SendAsync(ForzaPacketTests.Packet().AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);

        fixture.Advance(9);
        await sender.SendAsync(ForzaPacketTests.Packet(rpm: 2200).AsMemory());
        await Until(() => fixture.Listener.Status.Packets == 2);
        await sender.SendAsync(ForzaPacketTests.Packet(rpm: 5800).AsMemory());
        await Until(() => fixture.Listener.Status.Packets == 3);
        Assert.Equal(50, fixture.Output.RumbleRight);
        Assert.Equal(1, fixture.LayerUpdates);

        fixture.Advance(1);
        await Until(() => fixture.Output.RumbleRight == 80);
        Assert.Equal(2, fixture.LayerUpdates);
        fixture.Config = new TelemetryConfig();
        fixture.Advance(10);
        await Until(() => fixture.Output.RumbleRight == 0);
        Assert.Equal(ForzaListenerState.Receiving, fixture.Listener.Status.State);
    }

    [Fact]
    public async Task Conflict_releases_port_and_layer_and_unrelated_settings_keep_receiver()
    {
        using var fixture = new Fixture();
        fixture.Listener.Configure(fixture.Settings);
        int port = fixture.Listener.Status.Port;
        using UdpClient sender = fixture.Connect();
        await sender.SendAsync(ForzaPacketTests.Packet().AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);

        fixture.Listener.Configure(fixture.Settings with { UdpPort = port, Dsx = false, ReleaseAfterSeconds = 30 });
        Assert.Equal(port, fixture.Listener.Status.Port);
        Assert.Equal(ForzaListenerState.Receiving, fixture.Listener.Status.State);
        Assert.Equal(50, fixture.Output.RumbleRight);

        fixture.Advance(10);
        fixture.Listener.Configure(fixture.Settings with { UdpPort = port, ForzaPort = port });
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.Status.State);
        Assert.Empty(fixture.Notices);
        await Until(() => fixture.Output.RumbleRight == 0);
        using Socket other = Bind(port);

        fixture.Listener.Configure(fixture.Settings);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        using UdpClient next = fixture.Connect();
        await next.SendAsync(new byte[] { 1 }.AsMemory());
        await Until(() => fixture.Listener.Status.Rejected == 1);
        fixture.Advance(10);
        await next.SendAsync(ForzaPacketTests.Packet().AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);

        fixture.Advance(10);
        fixture.Listener.Configure(fixture.Settings with { ForzaEnabled = false });
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.Status.State);
        await Until(() => fixture.Output.RumbleRight == 0);
    }

    [Fact]
    public async Task Busy_port_retries_every_five_seconds_and_notifies_once()
    {
        using var fixture = new Fixture();
        using Socket occupied = Bind(0);
        int port = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        fixture.Listener.Configure(fixture.Settings with { ForzaPort = port });
        Assert.Equal(ForzaListenerState.PortBusy, fixture.Listener.Status.State);
        Assert.Contains(port.ToString(), Assert.Single(fixture.Notices).Message);

        fixture.Advance(5000);
        await Task.Delay(30);
        Assert.Equal(ForzaListenerState.PortBusy, fixture.Listener.Status.State);
        Assert.Single(fixture.Notices);

        occupied.Dispose();
        fixture.Advance(5000);
        await Until(() => fixture.Listener.Status.State == ForzaListenerState.Listening, timeoutSeconds: 6);
        Assert.Single(fixture.Notices);
    }

    [Fact]
    public async Task Dispose_releases_layer_and_bound_port()
    {
        using var fixture = new Fixture();
        fixture.Listener.Configure(fixture.Settings);
        int port = fixture.Listener.Status.Port;
        using UdpClient sender = fixture.Connect();
        await sender.SendAsync(ForzaPacketTests.Packet().AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);

        fixture.Listener.Dispose();
        Assert.Equal(0, fixture.Output.RumbleRight);
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.Status.State);
        using Socket probe = Bind(port);
    }

    [Fact]
    public async Task Multi_source_last_active_frame_wins_and_reports_active_source()
    {
        var memory = new FakeMemory();
        memory.Set(1, 2, 4000);
        using var fixture = new Fixture(memory);
        fixture.Listener.Configure(new IntegrationSettings
        {
            F1Enabled = true, F1Port = 0, BeamNgEnabled = true, BeamNgPort = 0, AcEnabled = true, ForzaEnabled = true, ForzaPort = 0,
        });
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.Acc && fixture.Output.RumbleRight == 50);
        using UdpClient f1 = fixture.Connect(fixture.Listener.F1Status.Port);
        using UdpClient beamNg = fixture.Connect(fixture.Listener.BeamNgStatus.Port);

        fixture.Advance(10);
        await f1.SendAsync(F1TelemetryTests.CarTelemetry(2024, 0).AsMemory());
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.F1 && fixture.Output.RumbleRight == 50);
        Assert.Equal(2024, fixture.Listener.F1Status.Format);
        Assert.Equal(ForzaListenerState.Receiving, fixture.Listener.F1Status.State);

        fixture.Advance(10);
        await beamNg.SendAsync(BeamNgOutGaugeTests.Packet(rpm: 7000).AsMemory());
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.BeamNg && fixture.Output.RumbleRight == 100);

        memory.Set(2, 2, 2000);
        fixture.Advance(20);
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.Acc && fixture.Output.RumbleRight == 25);
        Assert.Equal(AcGame.Acc, fixture.Listener.AcStatus.Game);
        Assert.Equal(AcListenerState.Receiving, fixture.Listener.AcStatus.State);

        // An idle frame from a source that is not driving the layer leaves the active one alone.
        fixture.Advance(10);
        await f1.SendAsync(F1TelemetryTests.CarTelemetry(2024, 0, rpm: 0, speed: 0, gas: 0, brake: 0, revLights: 0).AsMemory());
        await Until(() => fixture.Listener.F1Status.Packets == 2);
        await Task.Delay(30);
        Assert.Equal(TelemetrySource.Acc, fixture.Listener.ActiveSource);
        Assert.Equal(25, fixture.Output.RumbleRight);

        fixture.Advance(1000);
        await Until(() => fixture.Listener.ActiveSource is null && fixture.Output.RumbleRight == 0);
        Assert.Equal(AcListenerState.Listening, fixture.Listener.AcStatus.State);
    }

    [Fact]
    public async Task Disabling_the_active_source_clears_it_at_once_and_other_sources_keep_running()
    {
        using var fixture = new Fixture();
        var settings = new IntegrationSettings { F1Enabled = true, F1Port = 0, BeamNgEnabled = true, BeamNgPort = 0 };
        fixture.Listener.Configure(settings);
        using UdpClient f1 = fixture.Connect(fixture.Listener.F1Status.Port);
        using UdpClient beamNg = fixture.Connect(fixture.Listener.BeamNgStatus.Port);
        await f1.SendAsync(F1TelemetryTests.CarTelemetry(2024, 0).AsMemory());
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.F1);

        fixture.Advance(10);
        fixture.Listener.Configure(settings with { F1Enabled = false });
        Assert.Null(fixture.Listener.ActiveSource);
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.F1Status.State);
        await Until(() => fixture.Output.RumbleRight == 0);

        fixture.Advance(10);
        await beamNg.SendAsync(BeamNgOutGaugeTests.Packet(rpm: 7000).AsMemory());
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.BeamNg && fixture.Output.RumbleRight == 100);
        await beamNg.SendAsync(new byte[10].AsMemory());
        await Until(() => fixture.Listener.BeamNgStatus.Rejected == 1);
        Assert.Equal(TelemetrySource.BeamNg, fixture.Listener.ActiveSource);
    }

    [Fact]
    public async Task F1_counts_unsupported_formats_apart_from_rejected_datagrams()
    {
        using var fixture = new Fixture();
        fixture.Listener.Configure(new IntegrationSettings { F1Enabled = true, F1Port = 0 });
        using UdpClient f1 = fixture.Connect(fixture.Listener.F1Status.Port);
        await f1.SendAsync(F1TelemetryTests.Packet(2023, 6, 1352, 0).AsMemory());
        await f1.SendAsync(new byte[5].AsMemory());
        await Until(() => fixture.Listener.F1Status is { Unsupported: 1, Rejected: 1 });
        Assert.Equal(0, fixture.Listener.F1Status.Packets);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.F1Status.State);
        Assert.Null(fixture.Listener.ActiveSource);
    }

    [Fact]
    public async Task Ac_session_end_returns_to_listening_and_releases_the_layer()
    {
        var memory = new FakeMemory();
        memory.Set(1, 2, 4000);
        using var fixture = new Fixture(memory);
        fixture.Listener.Configure(new IntegrationSettings { AcEnabled = true });
        await Until(() => fixture.Listener.ActiveSource == TelemetrySource.Acc && fixture.Output.RumbleRight == 50);

        memory.Set(2, 3, 4000);
        fixture.Advance(20);
        await Until(() => fixture.Listener.AcStatus.State == AcListenerState.Listening && fixture.Output.RumbleRight == 0);
        Assert.Null(fixture.Listener.ActiveSource);

        fixture.Listener.Configure(new IntegrationSettings());
        Assert.Equal(AcListenerState.Off, fixture.Listener.AcStatus.State);
    }

    [Fact]
    public void Port_conflicts_follow_priority_order_udp_forza_f1_beamng()
    {
        using var fixture = new Fixture();
        int[] ports = FreePorts(4);
        int udp = ports[0], forza = ports[1], f1 = ports[2], beam = ports[3];
        var all = new IntegrationSettings
        {
            UdpPort = udp, ForzaEnabled = true, F1Enabled = true, BeamNgEnabled = true,
            ForzaPort = udp, F1Port = udp, BeamNgPort = udp,
        };
        fixture.Listener.Configure(all);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.F1Status.State);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.BeamNgStatus.State);

        fixture.Listener.Configure(all with { ForzaPort = forza, F1Port = forza, BeamNgPort = forza });
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.F1Status.State);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.BeamNgStatus.State);

        fixture.Listener.Configure(all with { ForzaPort = forza, F1Port = f1, BeamNgPort = f1 });
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.F1Status.State);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.BeamNgStatus.State);

        fixture.Listener.Configure(all with { ForzaPort = forza, F1Port = f1, BeamNgPort = beam });
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.F1Status.State);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.BeamNgStatus.State);

        // A higher priority source takes a port that a lower one holds.
        fixture.Listener.Configure(all with { ForzaPort = f1, F1Port = f1, BeamNgPort = beam });
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.F1Status.State);

        // A disabled source never reports a conflict.
        fixture.Listener.Configure(all with { ForzaPort = forza, F1Port = forza, F1Enabled = false, BeamNgEnabled = false });
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.F1Status.State);
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.BeamNgStatus.State);
    }

    [Fact]
    public async Task Dispose_releases_every_source_port_and_the_shared_memory_source()
    {
        var memory = new FakeMemory();
        var fixture = new Fixture(memory);
        fixture.Listener.Configure(new IntegrationSettings { F1Enabled = true, F1Port = 0, BeamNgEnabled = true, BeamNgPort = 0, AcEnabled = true });
        int f1Port = fixture.Listener.F1Status.Port;
        int beamPort = fixture.Listener.BeamNgStatus.Port;
        using UdpClient f1 = fixture.Connect(f1Port);
        await f1.SendAsync(F1TelemetryTests.CarTelemetry(2025, 0).AsMemory());
        await Until(() => fixture.Output.RumbleRight == 50);

        fixture.Listener.Dispose();
        Assert.Equal(0, fixture.Output.RumbleRight);
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.F1Status.State);
        Assert.Equal(AcListenerState.Off, fixture.Listener.AcStatus.State);
        Assert.Null(fixture.Listener.ActiveSource);
        using Socket probe = Bind(f1Port);
        using Socket probeBeam = Bind(beamPort);
        // The listener never owns the injected source: the reader only closes it.
        Assert.True(memory.Closed);
        Assert.False(memory.Disposed);
    }

    [Fact]
    public void Ports_can_be_swapped_between_sources_in_one_configure_without_a_false_busy_report()
    {
        using var fixture = new Fixture();
        int[] ports = FreePorts(2);
        var settings = new IntegrationSettings { ForzaEnabled = true, ForzaPort = ports[0], F1Enabled = true, F1Port = ports[1] };
        fixture.Listener.Configure(settings);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.F1Status.State);

        fixture.Listener.Configure(settings with { ForzaPort = ports[1], F1Port = ports[0] });
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.F1Status.State);
        Assert.Equal(ports[1], fixture.Listener.Status.Port);
        Assert.Equal(ports[0], fixture.Listener.F1Status.Port);

        // Forza leaves and F1 takes its port in the same call.
        fixture.Listener.Configure(settings with { ForzaEnabled = false, ForzaPort = ports[1], F1Port = ports[1] });
        Assert.Equal(ForzaListenerState.Off, fixture.Listener.Status.State);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.F1Status.State);
        Assert.Equal(ports[1], fixture.Listener.F1Status.Port);
        Assert.Empty(fixture.Notices);
    }

    [Fact]
    public void Release_frees_the_port_for_another_listener_and_the_next_configure_rebinds_or_reports_the_conflict()
    {
        using var fixture = new Fixture();
        int port = FreePorts(1)[0];
        var settings = new IntegrationSettings { ForzaEnabled = true, ForzaPort = port };
        fixture.Listener.Configure(settings);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);

        fixture.Listener.Release(port);
        using (Bind(port))
        {
        }

        Assert.Equal(ForzaListenerState.Off, fixture.Listener.Status.State);
        fixture.Listener.Configure(settings);
        Assert.Equal(ForzaListenerState.Listening, fixture.Listener.Status.State);
        Assert.Equal(port, fixture.Listener.Status.Port);

        fixture.Listener.Release(port);
        fixture.Listener.Configure(settings with { UdpEnabled = true, UdpPort = port });
        Assert.Equal(ForzaListenerState.PortConflict, fixture.Listener.Status.State);
        Assert.Empty(fixture.Notices);
    }

    private static int[] FreePorts(int count)
    {
        Socket[] sockets = Enumerable.Range(0, count).Select(_ => Bind(0)).ToArray();
        int[] ports = sockets.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToArray();
        foreach (Socket socket in sockets)
        {
            socket.Dispose();
        }

        return ports;
    }

    private sealed class FakeMemory : ISharedMemorySource
    {
        private readonly Lock _lock = new();
        private byte[] _physics = [];
        private byte[] _graphics = new byte[64];
        private bool _open;
        private volatile bool _disposed;

        public bool Disposed => _disposed;

        public void Set(int packetId, int status, int rpm)
        {
            byte[] physics = new byte[800];
            BinaryPrimitives.WriteInt32LittleEndian(physics, packetId);
            BinaryPrimitives.WriteInt32LittleEndian(physics.AsSpan(20), rpm);
            BinaryPrimitives.WriteInt32LittleEndian(physics.AsSpan(588), 8000);
            BinaryPrimitives.WriteSingleLittleEndian(physics.AsSpan(784), 0.01f);
            byte[] graphics = new byte[64];
            BinaryPrimitives.WriteInt32LittleEndian(graphics.AsSpan(4), status);
            lock (_lock)
            {
                _physics = physics;
                _graphics = graphics;
                _open = true;
            }
        }

        public bool TryRead(Span<byte> physics, out int physicsLength, Span<byte> graphics, out int graphicsLength,
            Span<byte> statics, out int staticsLength)
        {
            lock (_lock)
            {
                physicsLength = graphicsLength = staticsLength = 0;
                if (!_open)
                {
                    return false;
                }

                _physics.CopyTo(physics);
                _graphics.CopyTo(graphics);
                BinaryPrimitives.WriteInt32LittleEndian(statics[452..], 8000);
                physicsLength = _physics.Length;
                graphicsLength = _graphics.Length;
                staticsLength = 456;
                return true;
            }
        }

        public bool Closed { get; private set; }

        public void Close() => Closed = true;

        public void Dispose() => _disposed = true;
    }

    private static Socket Bind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        return socket;
    }

    private static async Task Until(Func<bool> condition, int timeoutSeconds = 3)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private OutputState _output = OutputState.Default;
        private int _layerUpdates;
        private long _ticks;
        private TelemetryConfig _config = new()
        {
            Rules = [new() { Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.RumbleRight, Max = 100 }],
        };

        public Fixture(ISharedMemorySource? memory = null)
        {
            Listener = new TelemetryListener(() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)),
                () => Volatile.Read(ref _config),
                layer =>
                {
                    Interlocked.Increment(ref _layerUpdates);
                    Volatile.Write(ref _output, layer?.Invoke(OutputState.Default) ?? OutputState.Default);
                }, Notices.Enqueue, memory);
        }

        public IntegrationSettings Settings { get; } = new() { ForzaEnabled = true, ForzaPort = 0 };
        public TelemetryListener Listener { get; }
        public ConcurrentQueue<Notice> Notices { get; } = new();
        public OutputState Output => Volatile.Read(ref _output);
        public int LayerUpdates => Volatile.Read(ref _layerUpdates);
        public TelemetryConfig Config { set => Volatile.Write(ref _config, value); }

        public void Advance(int milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);

        public UdpClient Connect() => Connect(Listener.Status.Port);

        public UdpClient Connect(int port)
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(IPAddress.Loopback, port);
            return client;
        }

        public void Dispose() => Listener.Dispose();
    }
}
