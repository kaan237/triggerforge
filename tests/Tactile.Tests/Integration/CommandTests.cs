using System.Text.Json;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Integration;

public sealed class CommandTests
{
    public static TheoryData<string, TriggerEffect> DsxModes => new()
    {
        { "0", TriggerEffect.Off },
        { "1", TriggerEffect.Feedback(8, 8) },
        { "2", TriggerEffect.Feedback(0, 1) },
        { "3", TriggerEffect.Feedback(0, 2) },
        { "4", TriggerEffect.Feedback(0, 5) },
        { "5", TriggerEffect.Feedback(0, 6) },
        { "6", TriggerEffect.Feedback(0, 7) },
        { "7", TriggerEffect.Feedback(0, 8) },
        { "8, 300", TriggerEffect.Vibration(0, 8, 255) },
        { "9", TriggerEffect.Feedback([0, 6, 0, 6, 0, 6, 0, 6, 0, 6]) },
        { "10", TriggerEffect.Feedback(0, 4) },
        { "11", TriggerEffect.Vibration(0, 4, 8) },
        { "12, 0", TriggerEffect.Off },
        { "13, 80, 14", TriggerEffect.Feedback(9, 8) },
        { "14, 8, -2, 11, 11", TriggerEffect.Bow(7, 8, 8, 8) },
        { "15, 5, 2, 6, 0, 300", TriggerEffect.Galloping(5, 6, 6, 7, 255) },
        { "16, -1, 1, 9", TriggerEffect.Weapon(2, 3, 8) },
        { "17, 1, 4, 50", TriggerEffect.Vibration(1, 4, 50) },
        { "18, 9, 8, 13, 4, 256, -1", TriggerEffect.Machine(8, 9, 7, 4, 255, 0) },
    };

    [Theory]
    [MemberData(nameof(DsxModes), DisableDiscoveryEnumeration = true)]
    public void Dsx_trigger_modes(string args, TriggerEffect expected)
    {
        using JsonDocument packet = JsonDocument.Parse($$"""[{"type":1,"parameters":[0,"2",{{args}}]}]""");
        (UdpState state, int unsupported) = ApplyDsx(packet, UdpState.Empty);
        Assert.Equal(0, unsupported);
        Assert.Equal(expected, state.RightTrigger);
        Assert.Null(state.LeftTrigger);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 6)]
    [InlineData(10, 6)]
    [InlineData(11, 6)]
    [InlineData(12, 6)]
    [InlineData(13, 6)]
    [InlineData(14, 6)]
    [InlineData(15, 6)]
    [InlineData(16, 6)]
    public void Dsx_custom_modes(int mode, byte rawMode)
    {
        using JsonDocument packet = JsonDocument.Parse(
            $$"""[{"type":1,"parameters":[0,1,12,{{mode}},-3,310,"8",true,5,6,7]}]""");
        (UdpState state, int unsupported) = ApplyDsx(packet, UdpState.Empty);
        Assert.Equal(0, unsupported);
        Assert.Equal(TriggerEffect.FromBytes([rawMode, 0, 255, 8, 1, 5, 6, 7, 0, 0, 0]), state.LeftTrigger);
    }

    [Fact]
    public void Dsx_applies_multiple_instructions_and_ignores_invalid_instructions()
    {
        using JsonDocument packet = JsonDocument.Parse("""
            [
              {"type":0,"parameters":[0]},
              {"type":99,"parameters":[0]},
              {"type":1,"parameters":[0,1,82]},
              {"type":1,"parameters":[0,1,12,20,0,0,0,0,0,0,0]},
              {"type":2,"parameters":[0,-5,"999",true,44]},
              {"type":2,"parameters":[0,1,2,"blue"]},
              {"type":3,"parameters":[0,true,false,3,"0","1"]},
              {"type":4,"parameters":[0,2,255]},
              {"type":5,"parameters":[0,1]}
            ]
            """);
        (UdpState state, int unsupported) = ApplyDsx(packet, UdpState.Empty);
        Assert.Equal(5, unsupported);
        Assert.Equal(new Rgb(0, 255, 1), state.Lightbar);
        Assert.Equal((byte)0b10101, state.PlayerLeds);
        Assert.Equal(MicLed.Pulse, state.MicLed);
        Assert.Null(state.LeftTrigger);
    }

    [Theory]
    [InlineData("""{"type":1,"parameters":[0]}""")]
    [InlineData("""{"type":1,"parameters":[0,3,0]}""")]
    [InlineData("""{"type":1,"parameters":[0,1,8]}""")]
    [InlineData("""{"type":1,"parameters":[0,1,12]}""")]
    [InlineData("""{"type":1,"parameters":[0,1,12,1,2]}""")]
    [InlineData("""{"type":2}""")]
    [InlineData("""{"type":2,"parameters":[0,1,2]}""")]
    [InlineData("""{"type":3,"parameters":[0,true,false,true,false]}""")]
    [InlineData("""{"type":4,"parameters":[0,1]}""")]
    [InlineData("""{"type":4,"parameters":[0,0,5]}""")]
    [InlineData("""{"type":5,"parameters":[0,{}]}""")]
    [InlineData("""{"type":5,"parameters":[0]}""")]
    [InlineData("""{"type":6,"parameters":"0,1"}""")]
    [InlineData("""{"type":6,"parameters":[0]}""")]
    [InlineData("""{"type":7,"parameters":[]}""")]
    public void Dsx_counts_malformed_known_instructions_without_changing_state(string instruction)
    {
        using JsonDocument packet = JsonDocument.Parse($"[{instruction}]");
        var original = new UdpState(PlayerLeds: 8);
        (UdpState state, int unsupported) = ApplyDsx(packet, original);
        Assert.Equal(1, unsupported);
        Assert.Equal(original, state);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 10)]
    [InlineData(2, 21)]
    [InlineData(3, 27)]
    [InlineData(4, 31)]
    [InlineData(5, 0)]
    public void Dsx_player_layouts(int number, byte mask)
    {
        using JsonDocument packet = JsonDocument.Parse($$"""[{"type":6,"parameters":[0,{{number}}]}]""");
        Assert.Equal(mask, ApplyDsx(packet, UdpState.Empty).State.PlayerLeds);
    }

    [Theory]
    [InlineData(0, MicLed.On)]
    [InlineData(1, MicLed.Pulse)]
    [InlineData(2, MicLed.Off)]
    public void Dsx_microphone_modes(int mode, MicLed expected)
    {
        using JsonDocument packet = JsonDocument.Parse($$"""[{"type":5,"parameters":[0,{{mode}}]}]""");
        Assert.Equal(expected, ApplyDsx(packet, UdpState.Empty).State.MicLed);
    }

    [Fact]
    public void Dsx_reset_clears_earlier_effects()
    {
        using JsonDocument packet = JsonDocument.Parse("""
            [{"type":2,"parameters":[0,1,2,3]},{"type":7,"parameters":[0]},{"type":5,"parameters":[0,0]}]
            """);
        Assert.Equal(new UdpState(MicLed: MicLed.On), ApplyDsx(packet, new(PlayerLeds: 31)).State);
    }

    private static (UdpState State, int Unsupported) ApplyDsx(JsonDocument packet, UdpState initial)
    {
        UdpState[] states = [initial];
        int unsupported = DsxTranslator.Apply(packet.RootElement, states, index => index == 0, _ => { });
        return (states[0], unsupported);
    }

    [Fact]
    public void Native_commands_can_overlay_and_clear_individual_fields()
    {
        using JsonDocument packet = JsonDocument.Parse("""
            [
              {"cmd":"trigger","side":"left","config":{"mode":"feedback","params":{"start":2,"strength":3}}},
              {"cmd":"trigger","side":"right","config":{"mode":"off"}},
              {"cmd":"trigger","side":"right","config":null},
              {"cmd":"lightbar","r":1,"g":2,"b":3},
              {"cmd":"playerLeds","mask":31},
              {"cmd":"micLed","mode":"pulse"},
              {"cmd":"rumble","left":10,"right":20},
              {"cmd":"profile","id":"profile-id"},
              {"cmd":"status"}
            ]
            """);
        var activated = new List<string>();
        (UdpState state, string? error) = NativeCommands.Apply(packet.RootElement, UdpState.Empty, activated.Add);
        var output = state.Overlay(OutputState.Default with { RightTrigger = TriggerEffect.Feedback(1, 2) });
        Assert.Null(error);
        Assert.Equal(new[] { "profile-id" }, activated);
        Assert.Equal(TriggerEffect.Feedback(2, 3), output.LeftTrigger);
        Assert.Equal(TriggerEffect.Feedback(1, 2), output.RightTrigger);
        Assert.Equal(new Rgb(1, 2, 3), output.Lightbar);
        Assert.Equal(31, output.PlayerLeds);
        Assert.Equal(MicLed.Pulse, output.MicLed);
        Assert.Equal(10, output.RumbleLeft);
        Assert.Equal(20, output.RumbleRight);
    }

    [Theory]
    [InlineData("""{"cmd":"missing"}""")]
    [InlineData("""{"cmd":"trigger","side":"left","config":{"mode":"feedback","params":{"start":99,"strength":3}}}""")]
    [InlineData("""{"cmd":"trigger","side":"left","config":{"mode":"feedback","params":null}}""")]
    [InlineData("""{"cmd":"trigger","side":"left","config":{"mode":"missing"}}""")]
    [InlineData("""{"cmd":"profile","id":"missing"}""")]
    [InlineData("""{"cmd":"playerLeds","mask":"7"}""")]
    public void Native_stops_on_first_error_without_undoing_earlier_commands(string invalid)
    {
        using JsonDocument packet = JsonDocument.Parse(
            $$"""[{"cmd":"reset"},{"cmd":"playerLeds","mask":8},{{invalid}},{"cmd":"playerLeds","mask":9}]""");
        (UdpState state, string? error) = NativeCommands.Apply(packet.RootElement, new(Lightbar: new Rgb(1, 2, 3)),
            _ => throw new KeyNotFoundException("Profil bulunamadı."));
        Assert.NotNull(error);
        Assert.Equal(new UdpState(PlayerLeds: 8), state);
    }
}
