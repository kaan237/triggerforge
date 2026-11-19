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

        public Fixture()
        {
            Listener = new TelemetryListener(() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)),
                () => Volatile.Read(ref _config),
                layer =>
                {
                    Interlocked.Increment(ref _layerUpdates);
                    Volatile.Write(ref _output, layer?.Invoke(OutputState.Default) ?? OutputState.Default);
                }, Notices.Enqueue);
        }

        public IntegrationSettings Settings { get; } = new() { ForzaEnabled = true, ForzaPort = 0 };
        public TelemetryListener Listener { get; }
        public ConcurrentQueue<Notice> Notices { get; } = new();
        public OutputState Output => Volatile.Read(ref _output);
        public int LayerUpdates => Volatile.Read(ref _layerUpdates);
        public TelemetryConfig Config { set => Volatile.Write(ref _config, value); }

        public void Advance(int milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);

        public UdpClient Connect()
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(IPAddress.Loopback, Listener.Status.Port);
            return client;
        }

        public void Dispose() => Listener.Dispose();
    }
}
