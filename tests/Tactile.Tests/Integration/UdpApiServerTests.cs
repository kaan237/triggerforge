using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Integration;

[Collection("Localization")]
public sealed class UdpApiServerTests
{
    private const string Status = """{"tactile":1,"commands":[{"cmd":"status"}]}""";
    private const string Rumble = """{"tactile":1,"commands":[{"cmd":"rumble","left":55,"right":22}]}""";
    private const string Reset = """{"tactile":1,"commands":[{"cmd":"reset"}]}""";
    private const string Dsx = """{"instructions":[{"type":99,"parameters":[0]},{"type":2,"parameters":[0,5,8,13]}]}""";

    [Fact]
    public async Task Answers_both_wire_formats_and_overlays_only_set_fields()
    {
        using var fixture = new Fixture();
        fixture.Controller = new(Link.Bluetooth, 76);
        using UdpClient client = fixture.Connect();

        JsonElement native = await Reply(client, Rumble);
        Assert.True(native.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, native.GetProperty("error").ValueKind);
        Assert.True(native.GetProperty("connected").GetBoolean());
        Assert.Equal("bluetooth", native.GetProperty("link").GetString());
        Assert.Equal(76, native.GetProperty("battery").GetInt32());
        Assert.Equal(UdpFormat.Native, fixture.Server.Status.Format);
        Assert.Equal(55, fixture.Output.RumbleLeft);
        Assert.Equal(22, fixture.Output.RumbleRight);
        Assert.Equal(OutputState.Default.Lightbar, fixture.Output.Lightbar);
        Assert.True(fixture.Server.Status.Holding);

        fixture.Controller = default;
        fixture.Advance(10);
        JsonElement dsx = await Reply(client, Dsx);
        Assert.Equal("TriggerForge: commands received", dsx.GetProperty("Status").GetString());
        Assert.True(DateTime.TryParse(dsx.GetProperty("TimeReceived").GetString(), out _));
        Assert.False(dsx.GetProperty("isControllerConnected").GetBoolean());
        Assert.Equal(0, dsx.GetProperty("BatteryLevel").GetInt32());
        Assert.Equal(new Rgb(5, 8, 13), fixture.Output.Lightbar);
        Assert.Equal(55, fixture.Output.RumbleLeft);
        Assert.Equal(UdpFormat.Dsx, fixture.Server.Status.Format);
        Assert.Equal(2, fixture.Server.Status.Packets);
        Assert.Equal(1, fixture.Server.Status.Unsupported);
    }

    [Fact]
    public async Task Malformed_and_oversize_datagrams_do_not_stop_the_server()
    {
        using var fixture = new Fixture();
        using UdpClient client = fixture.Connect();
        string[] malformed =
        [
            "garbage",
            "",
            new string(' ', 20 * 1024),
            """{"tactile":1,"commands":[{"cmd":"status","extra":[[[[[[[[]]]]]]]]}]}""",
            """{"tactile":1,"commands":true}""",
            """{"instructions":99}""",
            """{"tactile":"1","commands":[]}""",
            """{"tactile":1,"commands":[""" + string.Join(',', Enumerable.Repeat("{}", 33)) + "]}",
            """{"instructions":[""" + string.Join(',', Enumerable.Repeat("{}", 33)) + "]}",
        ];
        for (int i = 0; i < malformed.Length; i++)
        {
            await Send(client, malformed[i]);
            int expected = i + 1;
            await Until(() => fixture.Server.Status.Rejected == expected);
        }

        JsonElement error = await Reply(client, """{"tactile":1,"commands":[{"cmd":5}]}""");
        Assert.False(error.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(error.GetProperty("error").GetString()!);
        JsonElement valid = await Reply(client, Status);
        Assert.True(valid.GetProperty("ok").GetBoolean());
        Assert.False(valid.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, valid.GetProperty("link").ValueKind);
        Assert.Equal(JsonValueKind.Null, valid.GetProperty("battery").ValueKind);
        Assert.Equal(malformed.Length + 1, fixture.Server.Status.Rejected);
        Assert.Equal(1, fixture.Server.Status.Packets);
    }

    [Fact]
    public async Task Profile_error_preserves_earlier_commands_and_stops_later_commands()
    {
        using var fixture = new Fixture();
        using UdpClient client = fixture.Connect();

        JsonElement answer = await Reply(client, """
            {"tactile":1,"commands":[
              {"cmd":"playerLeds","mask":8},
              {"cmd":"profile","id":"missing"},
              {"cmd":"playerLeds","mask":9}
            ]}
            """);

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("Profil bulunamadı.", answer.GetProperty("error").GetString());
        Assert.Equal(8, fixture.Output.PlayerLeds);
        Assert.Equal(1, fixture.Server.Status.Rejected);
    }

    [Fact]
    public async Task Rate_limit_counts_all_datagrams_in_a_rolling_second()
    {
        using var fixture = new Fixture();
        using UdpClient client = fixture.Connect();
        for (int i = 0; i < 500; i++)
        {
            await Reply(client, Status);
        }

        fixture.Advance(999);
        await Send(client, Rumble);
        await Until(() => fixture.Server.Status.Rejected == 1);
        Assert.Equal(500, fixture.Server.Status.Packets);
        Assert.False(fixture.Server.Status.Holding);
        fixture.Advance(1);
        Assert.True((await Reply(client, Status)).GetProperty("ok").GetBoolean());
        Assert.Equal(501, fixture.Server.Status.Packets);
    }

    [Fact]
    public async Task Busy_port_retries_and_writes_the_file_only_after_binding()
    {
        using var busy = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };
        busy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)busy.LocalEndPoint!).Port;
        using var fixture = new Fixture(start: false);
        fixture.Server.Configure(fixture.Settings with { UdpPort = port });
        Assert.Equal(UdpListenerState.PortBusy, fixture.Server.Status.State);
        Assert.False(File.Exists(fixture.PortFile));
        fixture.Advance(5000);
        await Task.Delay(40);
        Assert.Equal(UdpListenerState.PortBusy, fixture.Server.Status.State);
        Assert.Single(fixture.Notices);
        busy.Dispose();
        fixture.Advance(5000);
        await Until(() => fixture.Server.Status.State == UdpListenerState.Listening, timeoutSeconds: 6);
        Assert.Equal(port.ToString(CultureInfo.InvariantCulture), File.ReadAllText(fixture.PortFile));
        using UdpClient client = fixture.Connect();
        Assert.True((await Reply(client, Status)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Reset_silence_release_disable_and_dispose_drop_the_layer()
    {
        using var fixture = new Fixture();
        fixture.Server.Configure(fixture.Settings with { ReleaseAfterSeconds = 1 });
        using UdpClient client = fixture.Connect();
        await Reply(client, Rumble);
        fixture.Advance(999);
        Assert.True(fixture.Server.Status.Holding);
        fixture.Advance(1);
        await Until(() => !fixture.Server.Status.Holding);
        Assert.Equal(1000, fixture.Server.Status.LastPacketAgoMs);
        Assert.Equal(OutputState.Default, fixture.Output);

        fixture.Advance(10);
        await Reply(client, Rumble);
        fixture.Advance(10);
        await Reply(client, Reset);
        Assert.False(fixture.Server.Status.Holding);

        fixture.Advance(10);
        await Reply(client, Rumble);
        fixture.Advance(10);
        fixture.Server.Release();
        Assert.False(fixture.Server.Status.Holding);

        fixture.Advance(10);
        await Reply(client, Rumble);
        fixture.Server.Configure(fixture.Settings with { UdpEnabled = false });
        Assert.False(fixture.Server.Status.Holding);
        Assert.Equal(UdpListenerState.Off, fixture.Server.Status.State);

        fixture.Server.Configure(fixture.Settings);
        using UdpClient afterEnable = fixture.Connect();
        fixture.Advance(10);
        await Reply(afterEnable, Rumble);
        fixture.Server.Dispose();
        Assert.False(fixture.Server.Status.Holding);
        Assert.Equal(OutputState.Default, fixture.Output);
    }

    [Fact]
    public async Task Coalesces_layer_updates_and_keeps_socket_for_nonbinding_settings()
    {
        using var fixture = new Fixture();
        using UdpClient client = fixture.Connect();
        int port = fixture.Server.Status.Port;
        Assert.Equal(port.ToString(CultureInfo.InvariantCulture), File.ReadAllText(fixture.PortFile));
        await Reply(client, Rumble);
        await Reply(client, Rumble.Replace("55", "56"));
        await Reply(client, Rumble.Replace("55", "57"));
        Assert.Equal(1, fixture.LayerUpdates);
        Assert.Equal(55, fixture.Output.RumbleLeft);
        fixture.Advance(10);
        await Until(() => fixture.LayerUpdates == 2);
        Assert.Equal(57, fixture.Output.RumbleLeft);

        fixture.Server.Configure(fixture.Settings with { Dsx = false, ForzaEnabled = true, ReleaseAfterSeconds = 10 });
        Assert.Equal(port, fixture.Server.Status.Port);
        await Send(client, Dsx);
        await Until(() => fixture.Server.Status.Rejected == 1);
        Assert.True((await Reply(client, Status)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task File_write_failure_notices_and_keeps_listening()
    {
        using var language = new TestLanguage(Language.Tr);
        using var fixture = new Fixture(start: false);
        File.WriteAllText(Path.GetDirectoryName(fixture.PortFile)!, "blocked");
        fixture.Server.Configure(fixture.Settings);
        Assert.Equal(UdpListenerState.Listening, fixture.Server.Status.State);
        Assert.Equal("DSX port dosyası yazılamadı.", Assert.Single(fixture.Notices).Message);
        using UdpClient client = fixture.Connect();
        Assert.True((await Reply(client, Status)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Dispose_closes_the_socket_even_during_an_inflight_callback()
    {
        using var dir = new TempDir();
        using var publishing = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var cleared = new ManualResetEventSlim();
        using var server = new UdpApiServer(
            () => TimeSpan.Zero,
            layer =>
            {
                if (layer is null)
                {
                    cleared.Set();
                    return;
                }

                publishing.Set();
                finish.Wait(TimeSpan.FromSeconds(5));
            },
            _ => { }, () => default, _ => { }, Path.Combine(dir.Path, "port.txt"));
        server.Configure(new IntegrationSettings { UdpEnabled = true, UdpPort = 0 });
        var endpoint = new IPEndPoint(IPAddress.Loopback, server.Status.Port);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(endpoint);
        await Send(client, Rumble);

        try
        {
            Assert.True(await Task.Run(() => publishing.Wait(TimeSpan.FromSeconds(3))));
            UdpStatus status = await Task.Run(() => server.Status).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(UdpListenerState.Listening, status.State);
            await Task.Run(server.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ExclusiveAddressUse = true,
            };
            probe.Bind(endpoint);
            Assert.False(cleared.IsSet);
        }
        finally
        {
            finish.Set();
            await Task.Run(() => cleared.Wait(TimeSpan.FromSeconds(3)));
        }

        Assert.True(cleared.IsSet);
    }

    private static async Task Until(Func<bool> condition, int timeoutSeconds = 3)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task Send(UdpClient client, string json) =>
        await client.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory());

    private static async Task<JsonElement> Reply(UdpClient client, string json)
    {
        await Send(client, json);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        UdpReceiveResult result = await client.ReceiveAsync(timeout.Token);
        Assert.Equal(client.Client.RemoteEndPoint, result.RemoteEndPoint);
        using JsonDocument doc = JsonDocument.Parse(result.Buffer);
        return doc.RootElement.Clone();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new();
        private OutputState _output = OutputState.Default;
        private int _layerUpdates;
        private long _ticks;

        public Fixture(bool start = true)
        {
            PortFile = Path.Combine(_dir.Path, "dsx", "port.txt");
            Server = new UdpApiServer(
                () => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)),
                layer =>
                {
                    Volatile.Write(ref _output, layer?.Invoke(OutputState.Default) ?? OutputState.Default);
                    Interlocked.Increment(ref _layerUpdates);
                },
                _ => throw new KeyNotFoundException("Profil bulunamadı."),
                () => Controller, Notices.Enqueue, PortFile);
            if (start)
            {
                Server.Configure(Settings);
            }
        }

        public IntegrationSettings Settings { get; } = new() { UdpEnabled = true, UdpPort = 0 };
        public UdpApiServer Server { get; }
        public UdpControllerStatus Controller { get; set; }
        public string PortFile { get; }
        public System.Collections.Concurrent.ConcurrentQueue<Notice> Notices { get; } = new();
        public OutputState Output => Volatile.Read(ref _output);
        public int LayerUpdates => Volatile.Read(ref _layerUpdates);

        public void Advance(int milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);

        public UdpClient Connect()
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(IPAddress.Loopback, Server.Status.Port);
            return client;
        }

        public void Dispose()
        {
            Server.Dispose();
            _dir.Dispose();
        }
    }
}
