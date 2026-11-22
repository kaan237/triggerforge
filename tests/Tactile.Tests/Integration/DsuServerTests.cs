using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Integration;

public sealed class DsuServerTests
{
    private const uint Version = 0x100000;
    private const uint Info = 0x100001;
    private const uint Data = 0x100002;
    private const string Mac = "12:34:56:78:9A:BC";
    private static readonly InputState Input = new(
        Buttons.Left | Buttons.Up | Buttons.Options | Buttons.Create | Buttons.Cross |
        Buttons.Triangle | Buttons.R1 | Buttons.L2 | Buttons.PS | Buttons.Touchpad,
        new(10, 20), new(30, 40), 50, 60, default, default, 3000,
        new(true, 7, 321, 654), new(false, 8, 789, 901), new(100, ChargeState.Full),
        false, false, false);

    [Fact]
    public async Task Answers_version_and_controller_info_for_four_slots_with_valid_crc()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient client = fixture.Connect();
        fixture.Server.PublishInput(0, Input, default, default, Link.Usb, Mac);
        fixture.Server.PublishInput(1, Input with { Battery = new(55, ChargeState.Charging) },
            default, default, Link.Bluetooth, null);

        await Send(client, Request(Version));
        byte[] version = await Receive(client, Version, 22);
        Assert.Equal((ushort)1001, BinaryPrimitives.ReadUInt16LittleEndian(version));

        await Send(client, Request(Info, [4, 0, 0, 0, 0, 1, 2, 3]));
        byte[][] expected =
        [
            [0, 2, 2, 1, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xEF, 0],
            [1, 2, 2, 2, 0x02, 0x54, 0x41, 0x43, 0x54, 2, 0xEE, 0],
            [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ];
        var slots = new HashSet<byte>();
        for (int i = 0; i < expected.Length; i++)
        {
            byte[] payload = await Receive(client, Info, 32);
            Assert.InRange(payload[0], (byte)0, (byte)3);
            Assert.True(slots.Add(payload[0]));
            Assert.Equal(expected[payload[0]], payload);
        }
    }

    [Fact]
    public async Task Streams_motion_and_input_to_subscribed_clients_and_expires_after_5s()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient all = fixture.Connect();
        using UdpClient slot = fixture.Connect();
        using UdpClient mac = fixture.Connect();
        byte[] allSlots = Request(Data, new byte[8]);
        await Send(all, allSlots);
        await Send(slot, Request(Data, [1, 1, 0, 0, 0, 0, 0, 0]));
        await Send(mac, Request(Data, [2, 0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC]));
        await Until(() => fixture.Server.Status.Clients == 3);

        fixture.Server.PublishInput(0, Input, new(90, -45, 15), new(0, 1, 0), Link.Usb, Mac);
        byte[] payload = await Receive(all, Data, 100);
        Assert.Equal(payload, await Receive(mac, Data, 100));
        Assert.Equal(2L, fixture.Server.Status.Packets);
        Assert.Equal<byte>(
            [0, 2, 2, 1, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xEF, 1, 0, 0, 0, 0],
            payload[..16]);
        Assert.Equal<byte>(
            [0x99, 0x59, 1, 1, 10, 235, 30, 215, 255, 0, 0, 255, 0, 255, 0, 255, 255, 0, 60, 50],
            payload[16..36]);
        Assert.Equal<byte>([1, 7, 0x41, 1, 0x8E, 2, 0, 8, 0x15, 3, 0x85, 3], payload[36..48]);
        Assert.Equal(1000UL, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(48)));
        float[] motion = [0, 1, 0, 90, -45, 15];
        for (int i = 0; i < motion.Length; i++)
        {
            Assert.Equal(motion[i], BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(56 + i * 4)));
        }

        fixture.Server.PublishInput(1, Input, default, default, Link.Bluetooth, null);
        byte[] slotPayload = await Receive(all, Data, 100);
        Assert.Equal((byte)1, slotPayload[0]);
        Assert.Equal(slotPayload, await Receive(slot, Data, 100));
        Assert.Equal(4L, fixture.Server.Status.Packets);

        fixture.Advance(4999);
        fixture.Server.PublishInput(0, Input with { SensorTimestamp = 6000 }, default, default, Link.Usb, Mac);
        byte[] next = await Receive(all, Data, 100);
        Assert.Equal(next, await Receive(mac, Data, 100));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(next.AsSpan(12)));
        Assert.Equal(2000UL, BinaryPrimitives.ReadUInt64LittleEndian(next.AsSpan(48)));

        fixture.Advance(2);
        InputState later = Input with { SensorTimestamp = 9000 };
        fixture.Server.PublishInput(0, later, default, default, Link.Usb, Mac);
        Assert.Equal(0, fixture.Server.Status.Clients);
        Assert.Equal(6L, fixture.Server.Status.Packets);

        await Send(all, allSlots);
        await Until(() => fixture.Server.Status.Clients == 1);
        fixture.Server.PublishInput(0, later with { SensorTimestamp = 12000 }, default, default, Link.Usb, Mac);
        Assert.Equal((byte)0, (await Receive(all, Data, 100))[0]);
        Assert.Equal(7L, fixture.Server.Status.Packets);
    }

    [Fact]
    public async Task Rejects_malformed_bad_crc_and_rate_limited_packets_without_stopping()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient client = fixture.Connect();
        byte[] valid = Request(Version);
        byte[] wrongMagic = Request(Version, magic: "DSUX");
        byte[] wrongVersion = Request(Version, protocol: 1000);
        byte[] wrongLength = Request(Version, declaredLength: 5);
        byte[] badCrc = (byte[])valid.Clone();
        badCrc[8] ^= 1;
        byte[][] malformed = [valid[..19], Request(Version, new byte[1005]), wrongMagic, wrongVersion, wrongLength, badCrc];
        for (int i = 0; i < malformed.Length; i++)
        {
            await Send(client, malformed[i]);
            await Until(() => fixture.Server.Status.Rejected == i + 1);
        }
        Assert.Equal(0L, fixture.Server.Status.Packets);

        fixture.Advance(1000);
        // A reply acknowledges each arrival so the test measures the limiter independently of UDP buffer capacity.
        for (int i = 0; i < 500; i++)
        {
            await Send(client, valid);
            await Receive(client, Version, 22);
        }
        await Send(client, valid);
        await Until(() => fixture.Server.Status.Rejected == malformed.Length + 1);
        Assert.Equal(500L, fixture.Server.Status.Packets);
        Assert.Equal(DsuListenerState.Listening, fixture.Server.Status.State);

        fixture.Advance(1000);
        await Send(client, valid);
        await Receive(client, Version, 22);
        Assert.Equal(malformed.Length + 1L, fixture.Server.Status.Rejected);
    }

    [Fact]
    public async Task Port_busy_and_port_conflict_with_udp_or_forza_are_reported()
    {
        using var fixture = new Fixture();
        using var occupied = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
        occupied.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        IntegrationSettings settings = fixture.Settings with { DsuPort = port, UdpPort = 0, ForzaPort = 0 };

        fixture.Server.Configure(settings with { UdpPort = port });
        Assert.Equal(DsuListenerState.PortConflict, fixture.Server.Status.State);
        fixture.Server.Configure(settings with { ForzaPort = port, ForzaEnabled = true });
        Assert.Equal(DsuListenerState.PortConflict, fixture.Server.Status.State);
        Assert.Empty(fixture.Notices);

        fixture.Server.Configure(settings);
        Assert.Equal(DsuListenerState.PortBusy, fixture.Server.Status.State);
        Assert.Contains($"DSU port {port}", Assert.Single(fixture.Notices).Message);
        occupied.Dispose();
        fixture.Advance(4999);
        await Task.Delay(300);
        Assert.Equal(DsuListenerState.PortBusy, fixture.Server.Status.State);
        fixture.Advance(1);
        await Until(() => fixture.Server.Status.State == DsuListenerState.Listening);
        Assert.Equal(port, fixture.Server.Status.Port);
        Assert.Single(fixture.Notices);
        using UdpClient client = fixture.Connect();
        await Send(client, Request(Version));
        await Receive(client, Version, 22);
    }

    [Fact]
    public async Task Timestamp_keeps_growing_across_reconnects_and_missing_sensor_time()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient client = fixture.Connect();
        await Send(client, Request(Data, new byte[8]));
        await Until(() => fixture.Server.Status.Clients == 1);

        async Task<ulong> Publish(uint raw)
        {
            fixture.Server.PublishInput(0, Input with { SensorTimestamp = raw }, default, default, Link.Usb, Mac);
            return BinaryPrimitives.ReadUInt64LittleEndian((await Receive(client, Data, 100)).AsSpan(48));
        }

        ulong first = await Publish(3_000_000);
        Assert.Equal(1_000_000UL, first);

        // A sensor that goes quiet advances by the clock, not by a made-up jump.
        fixture.Advance(500);
        ulong quiet = await Publish(0);
        Assert.InRange(quiet, first + 499_000, first + 501_000);

        // Sensor time returns with a small counter: it continues from the last value.
        ulong back = await Publish(30);
        Assert.True(back >= quiet);
        ulong next = await Publish(3030);
        Assert.Equal(back + 1000, next);

        // A reconnect restarts the controller's own counter; reported time still never goes back.
        fixture.Server.Disconnect(0);
        ulong again = await Publish(60);
        Assert.True(again >= next);
        Assert.Equal(again + 1000, await Publish(3060));
    }

    [Fact]
    public async Task One_client_can_follow_several_slots_and_a_flood_does_not_starve_another_client()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient both = fixture.Connect();
        using UdpClient other = fixture.Connect();
        await Send(both, Request(Data, [1, 0, 0, 0, 0, 0, 0, 0]));
        await Send(both, Request(Data, [1, 1, 0, 0, 0, 0, 0, 0]));
        await Until(() => fixture.Server.Status.Clients == 1);

        fixture.Server.PublishInput(0, Input, default, default, Link.Usb, Mac);
        fixture.Server.PublishInput(1, Input, default, default, Link.Bluetooth, null);
        Assert.Equal((byte)0, (await Receive(both, Data, 100))[0]);
        Assert.Equal((byte)1, (await Receive(both, Data, 100))[0]);

        byte[] valid = Request(Version);
        // The two subscriptions above already used two of the 500 arrivals.
        for (int i = 0; i < 498; i++)
        {
            await Send(both, valid);
            await Receive(both, Version, 22);
        }

        await Send(both, valid);
        await Until(() => fixture.Server.Status.Rejected == 1);
        await Send(other, valid);
        Assert.Equal((ushort)1001, BinaryPrimitives.ReadUInt16LittleEndian(await Receive(other, Version, 22)));
    }

    private static byte[] Request(uint type, byte[]? payload = null, string magic = "DSUC",
        ushort protocol = 1001, ushort? declaredLength = null)
    {
        byte[] packet = new byte[20 + (payload?.Length ?? 0)];
        System.Text.Encoding.ASCII.GetBytes(magic, packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), protocol);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), declaredLength ?? (ushort)(packet.Length - 16));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), type);
        payload?.CopyTo(packet, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), Crc32.Compute(packet));
        return packet;
    }

    private static ValueTask<int> Send(UdpClient client, byte[] packet) => client.SendAsync(packet.AsMemory());

    private static async Task<byte[]> Receive(UdpClient client, uint type, int size)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] packet = (await client.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(size, packet.Length);
        Assert.True(packet.AsSpan(0, 4).SequenceEqual("DSUS"u8));
        Assert.Equal((ushort)1001, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)));
        Assert.Equal((ushort)(size - 16), BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)));
        Assert.Equal(type, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
        packet.AsSpan(8, 4).Clear();
        Assert.Equal(crc, Crc32.Compute(packet));
        return packet[20..];
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private long _ticks;

        public Fixture() => Server = new(() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)), Notices.Enqueue);

        public DsuServer Server { get; }
        public IntegrationSettings Settings { get; } = new() { DsuEnabled = true, DsuPort = 0 };
        public ConcurrentQueue<Notice> Notices { get; } = new();

        public void Advance(int milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);

        public UdpClient Connect()
        {
            DsuStatus status = Server.Status;
            Assert.Equal(DsuListenerState.Listening, status.State);
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(IPAddress.Loopback, status.Port);
            return client;
        }

        public void Dispose() => Server.Dispose();
    }

    [Fact]
    public async Task Large_sensor_time_wraps_restarts_and_gaps_never_jump()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient client = fixture.Connect();
        await Send(client, Request(Data, new byte[8]));
        await Until(() => fixture.Server.Status.Clients == 1);

        async Task<ulong> Publish(uint raw)
        {
            fixture.Server.PublishInput(0, Input with { SensorTimestamp = raw }, default, default, Link.Usb, Mac);
            return BinaryPrimitives.ReadUInt64LittleEndian((await Receive(client, Data, 100)).AsSpan(48));
        }

        var stamps = new List<ulong> { await Publish(4_294_900_000) };
        // 0.333 us ticks: 30_000 ticks are 10 ms. The counter wraps past 2^32 after the first step.
        foreach (uint raw in new uint[] { 4_294_960_000, 20_000, 50_000 })
        {
            stamps.Add(await Publish(raw));
        }

        fixture.Advance(20);
        stamps.Add(await Publish(0));
        stamps.Add(await Publish(3_000_000_000));
        stamps.Add(await Publish(3_000_030_000));
        fixture.Server.Disconnect(0);
        stamps.Add(await Publish(2_000_000_000));
        stamps.Add(await Publish(2_000_030_000));

        for (int i = 1; i < stamps.Count; i++)
        {
            Assert.InRange(stamps[i] - stamps[i - 1], 0UL, 100_000UL);
        }

        Assert.True(stamps[^1] > stamps[0]);
    }

    [Fact]
    public async Task A_report_taken_before_a_disconnect_does_not_reconnect_the_slot()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient client = fixture.Connect();
        await Send(client, Request(Data, new byte[8]));
        await Until(() => fixture.Server.Status.Clients == 1);

        int generation = fixture.Server.Generation(0);
        fixture.Server.PublishInput(0, Input, default, default, Link.Usb, Mac, generation);
        await Receive(client, Data, 100);

        fixture.Server.Disconnect(0);
        fixture.Server.PublishInput(0, Input, default, default, Link.Usb, Mac, generation);
        Assert.Equal(1L, fixture.Server.Status.Packets);

        await Send(client, Request(Info, [1, 0, 0, 0, 0]));
        byte[] info = await Receive(client, Info, 32);
        Assert.Equal<byte>([0, 0, 0, 0], info[1..5]);

        fixture.Server.PublishInput(0, Input, default, default, Link.Usb, Mac, fixture.Server.Generation(0));
        Assert.Equal((byte)0, (await Receive(client, Data, 100))[0]);
    }

    [Fact]
    public async Task Leading_zero_reports_and_a_wrap_through_zero_do_not_jump()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings);
        using UdpClient client = fixture.Connect();
        await Send(client, Request(Data, new byte[8]));
        await Until(() => fixture.Server.Status.Clients == 1);

        async Task<ulong> Publish(uint raw)
        {
            fixture.Server.PublishInput(0, Input with { SensorTimestamp = raw }, default, default, Link.Usb, Mac);
            return BinaryPrimitives.ReadUInt64LittleEndian((await Receive(client, Data, 100)).AsSpan(48));
        }

        var stamps = new List<ulong> { await Publish(0), await Publish(0), await Publish(3_000_000_000) };
        stamps.Add(await Publish(3_000_030_000));
        // A new connection with a counter close to its wrap; it runs through exactly zero and on.
        fixture.Server.Disconnect(0);
        stamps.Add(await Publish(4_294_900_000));
        stamps.Add(await Publish(4_294_967_000));
        stamps.Add(await Publish(0));
        stamps.Add(await Publish(500));
        stamps.Add(await Publish(30_500));

        for (int i = 1; i < stamps.Count; i++)
        {
            Assert.InRange(stamps[i] - stamps[i - 1], 0UL, 100_000UL);
        }

        // 296 ticks up to the wrap, 500 after it: about 265 us across the zero.
        Assert.InRange(stamps[7] - stamps[5], 260UL, 270UL);
        Assert.Equal(10_000UL, stamps[^1] - stamps[^2]);
    }
}
