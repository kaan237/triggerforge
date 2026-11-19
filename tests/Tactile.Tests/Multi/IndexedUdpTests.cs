using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Multi;

public sealed class IndexedUdpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Secondary_overrides_are_discarded_on_disconnect_both_before_and_after_publication(bool applied)
    {
        using var pads = new FakePads();
        pads.Service.ScanIfIdle();
        using var server = new UdpApiServer(() => TimeSpan.Zero, (_, _) => { }, (_, _) => { },
            _ => default, _ => { }, getSlots: () => pads.Service.Slots);
        server.Configure(new IntegrationSettings { ReleaseAfterSeconds = 0 });
        ControllerSlot original = pads.Service.Slots[1];
        using JsonDocument rumble = JsonDocument.Parse("""[{"cmd":"rumble","controller":1,"left":75,"right":50}]""");
        Assert.Null(server.ProcessNative(rumble.RootElement, TimeSpan.Zero)!.Value.Error);
        if (applied)
        {
            server.PublishPending();
            Assert.Equal((byte?)75, server.StateAt(original).RumbleLeft);
        }

        pads.Present = [1, 3];
        pads[2].PublishDisconnect();
        Assert.False(server.Status.Holding);
        Assert.NotNull(server.ProcessNative(rumble.RootElement, TimeSpan.Zero)!.Value.Error);
        pads.Service.ScanIfIdle();
        server.PublishPending();
        ControllerSlot replacement = pads.Service.Slots[1];
        Assert.Equal(UdpState.Empty, server.StateAt(replacement));

        using JsonDocument leds = JsonDocument.Parse("""[{"type":6,"parameters":[1,3]}]""");
        server.ProcessDsx(leds.RootElement, TimeSpan.FromMilliseconds(10));
        Assert.Null(server.ProcessNative(rumble.RootElement, TimeSpan.FromMilliseconds(10))!.Value.Error);
        pads.Service.SelectPrimary(FakePads.Mac(3));
        server.PublishPending();
        Assert.Equal(UdpState.Empty, server.StateAt(pads.Service.Slots[1]));
        Assert.False(server.Status.Holding);
    }

    [Fact]
    public void Dsx_routes_instructions_to_connected_slots_and_answers_for_slot_zero()
    {
        using var fixture = new UdpFixture();
        using JsonDocument packet = JsonDocument.Parse("""
            [
              {"type":2,"parameters":[0,1,2,3]},
              {"type":6,"parameters":["2",3]},
              {"type":5,"parameters":[1,0]},
              {"type":5,"parameters":[4,0]},
              {"type":5,"parameters":[2.5,0]}
            ]
            """);
        UdpControllerStatus? reply = fixture.Server.ProcessDsx(packet.RootElement, fixture.Now);
        fixture.Server.PublishPending();
        Assert.Equal(new UdpControllerStatus(Link.Bluetooth, 80), reply);
        Assert.Equal(3, fixture.Server.Status.Unsupported);
        Assert.Equal(new Rgb(1, 2, 3), fixture.Output(0).Lightbar);
        Assert.Equal(0x1B, fixture.Output(2).PlayerLeds);
        Assert.Equal(OutputState.Default, fixture.Output(1));

        fixture.Advance(10);
        using JsonDocument reset = JsonDocument.Parse("""[{"type":7,"parameters":[2]}]""");
        fixture.Server.ProcessDsx(reset.RootElement, fixture.Now);
        fixture.Server.PublishPending();
        Assert.Equal(OutputState.Default, fixture.Output(2));
        Assert.Equal(new Rgb(1, 2, 3), fixture.Output(0).Lightbar);
    }

    [Fact]
    public void Native_commands_answer_for_the_last_address_and_reset_and_expire_each_slot_independently()
    {
        using var fixture = new UdpFixture();
        var reply = fixture.Native("""
            [{"cmd":"rumble","left":32,"right":64},{"cmd":"playerLeds","controller":2,"mask":10}]
            """);
        Assert.Null(reply.Error);
        Assert.Equal(new UdpControllerStatus(Link.Usb, 60), reply.Controller);
        Assert.Equal(32, fixture.Output(0).RumbleLeft);
        Assert.Equal(10, fixture.Output(2).PlayerLeds);

        fixture.Advance(500);
        fixture.Native("""[{"cmd":"status","controller":2}]""");
        fixture.Advance(500);
        fixture.Server.PublishPending();
        Assert.Equal(OutputState.Default, fixture.Output(0));
        Assert.Equal(10, fixture.Output(2).PlayerLeds);
        fixture.Advance(500);
        fixture.Server.PublishPending();
        Assert.Equal(OutputState.Default, fixture.Output(2));

        fixture.Advance(10);
        fixture.Native("""[{"cmd":"playerLeds","mask":4},{"cmd":"playerLeds","controller":2,"mask":10}]""");
        fixture.Advance(10);
        fixture.Native("""[{"cmd":"reset","controller":2}]""");
        Assert.Equal(4, fixture.Output(0).PlayerLeds);
        Assert.Equal(OutputState.Default, fixture.Output(2));
        fixture.Advance(10);
        fixture.Native("""[{"cmd":"playerLeds","controller":2,"mask":10}]""");
        fixture.Advance(10);
        fixture.Native("""[{"cmd":"reset"}]""");
        Assert.False(fixture.Server.Status.Holding);
    }

    [Fact]
    public void Native_validates_the_index_and_routes_profile_commands_in_order()
    {
        using var fixture = new UdpFixture();
        var reply = fixture.Native("""
            [{"cmd":"profile","id":"first"},{"cmd":"profile","controller":2,"id":"second"},
             {"cmd":"status","controller":4},{"cmd":"profile","id":"ignored"}]
            """);
        Assert.NotNull(reply.Error);
        Assert.Equal([(0, "first"), (2, "second")], fixture.Activated);
        Assert.False(fixture.Native("""[{"cmd":"status","controller":1}]""").Controller.Connected);
    }

    private sealed class UdpFixture : IDisposable
    {
        private readonly Func<OutputState, OutputState>?[] _layers = new Func<OutputState, OutputState>?[4];
        public List<(int Slot, string Id)> Activated { get; } = [];
        public UdpApiServer Server { get; }
        public TimeSpan Now { get; private set; }

        public UdpFixture()
        {
            Server = new(() => Now, (index, layer) => _layers[index] = layer, (index, id) => Activated.Add((index, id)),
                index => index switch { 0 => new(Link.Bluetooth, 80), 2 => new(Link.Usb, 60), _ => default }, _ => { });
            Server.Configure(new IntegrationSettings { ReleaseAfterSeconds = 1 });
        }

        public OutputState Output(int index) => _layers[index]?.Invoke(OutputState.Default) ?? OutputState.Default;
        public void Advance(int milliseconds) => Now += TimeSpan.FromMilliseconds(milliseconds);
        public void Dispose() => Server.Dispose();

        public (string? Error, UdpControllerStatus Controller) Native(string commands)
        {
            using JsonDocument packet = JsonDocument.Parse(commands);
            var reply = Server.ProcessNative(packet.RootElement, Now);
            Server.PublishPending();
            return reply!.Value;
        }
    }
}
