using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Integration;

public sealed class TelemetryRulesTests
{
    private static readonly TelemetryValues Signals = new(1, 72, 0.5, 0.5, 0.5, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0);

    [Theory]
    [InlineData(0.1, 12)]
    [InlineData(0.25, 0)]
    [InlineData(0.5, 25)]
    [InlineData(0.75, 100)]
    [InlineData(1.0, 100)]
    public void Last_active_rule_wins_with_threshold_and_exponent(double rpm, byte rumble)
    {
        var config = new TelemetryConfig
        {
            Rules =
            [
                new() { Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.RumbleLeft, Max = 12, To = 0 },
                new() { Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.RumbleLeft, From = 0.25, To = 0.75, Exponent = 2, Max = 100 },
                new() { Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.RumbleRight, From = 0.9, To = 0.1, Max = 200 },
            ],
        };
        UdpState result = TelemetryRules.Evaluate(config, Signals with { Rpm = rpm }, TimeSpan.Zero);
        Assert.Equal(rumble, result.RumbleLeft);
        Assert.Equal(rpm >= 0.9 ? (byte)200 : (byte?)null, result.RumbleRight);
    }

    [Fact]
    public void Driven_trigger_rounds_and_clamps_to_its_template_while_invalid_rules_fall_through()
    {
        var feedback = new TriggerConfig
        {
            Mode = TriggerMode.Feedback,
            Params = new Dictionary<string, double> { ["start"] = 1 },
        };
        var weapon = new TriggerConfig
        {
            Mode = TriggerMode.Weapon,
            Params = new Dictionary<string, double> { ["end"] = 5, ["strength"] = 3 },
        };
        var config = new TelemetryConfig
        {
            Rules =
            [
                new() { Signal = TelemetrySignal.Brake, Target = TelemetryTarget.LeftTrigger, Trigger = feedback, Param = "strength", Min = 1, Max = 12 },
                new() { Signal = TelemetrySignal.Brake, Target = TelemetryTarget.LeftTrigger, Trigger = feedback },
                new() { Signal = TelemetrySignal.Brake, Target = TelemetryTarget.LeftTrigger, Trigger = feedback, Param = "unsupported" },
                new() { Signal = TelemetrySignal.Gas, Target = TelemetryTarget.RightTrigger, Trigger = weapon, Param = "start", Min = 2, Max = 99 },
            ],
        };

        UdpState result = TelemetryRules.Evaluate(config, Signals, TimeSpan.Zero);
        Assert.Equal(TriggerEffect.Feedback(1, 6), result.LeftTrigger);
        Assert.Equal(TriggerEffect.Weapon(4, 5, 3), result.RightTrigger);
        Assert.Equal(TriggerEffect.Feedback(1, 8),
            TelemetryRules.Evaluate(config, Signals with { Brake = 1 }, TimeSpan.Zero).LeftTrigger);
        Assert.DoesNotContain("strength", feedback.Params.Keys);
    }

    [Theory]
    [InlineData(0, 128, 64, 32)]
    [InlineData(62, 128, 64, 32)]
    [InlineData(63, 0, 0, 0)]
    [InlineData(124, 0, 0, 0)]
    [InlineData(125, 128, 64, 32)]
    public void Lightbar_lerps_and_blinks_eight_times_per_second(int ms, byte r, byte g, byte b)
    {
        var config = new TelemetryConfig
        {
            Rules =
            [
                new() { Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.Lightbar, ColorTo = new(255, 128, 64), BlinkAt = 0.5 },
                new() { Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.PlayerLeds, Min = 1, Max = 5 },
            ],
        };
        UdpState result = TelemetryRules.Evaluate(config, Signals, TimeSpan.FromMilliseconds(ms));
        Assert.Equal(new Rgb(r, g, b), result.Lightbar);
        Assert.Equal((byte)0b00111, result.PlayerLeds);
    }

    [Fact]
    public void Normalization_drops_unknown_enums_and_extra_rules_and_clamps_values()
    {
        const string json = """
            {"telemetry":{"rules":[
                {"signal":"unknownSignal","target":"rumbleLeft"},
                {"signal":"brake","target":"unknownTarget"},
                {"signal":999,"target":"rumbleLeft"},
                {"signal":"brake","target":"leftTrigger","trigger":{"mode":"unknownMode"}},
                null,
                {"signal":"steer","target":"playerLeds","from":-7,"to":9,"exponent":0,"min":-2,"max":99,
                 "colorTo":{"r":-5,"g":900,"b":42},"blinkAt":1.5}
            ]}}
            """;
        Profile profile = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!;
        var issues = new List<string>();
        TelemetryRule cleaned = Assert.Single(Validation.Normalize(profile, issues).Telemetry.Rules);
        Assert.Equal(5, issues.Count);
        Assert.Equal(-1, cleaned.From);
        Assert.Equal(1, cleaned.To);
        Assert.Equal(0.2, cleaned.Exponent);
        Assert.Equal(0, cleaned.Min);
        Assert.Equal(5, cleaned.Max);
        Assert.Equal(1, cleaned.BlinkAt);
        Assert.Equal(new TelemetryColor(0, 0, 0), cleaned.ColorFrom);
        Assert.Equal(new TelemetryColor(0, 255, 42), cleaned.ColorTo);

        var many = new TelemetryConfig { Rules = Enumerable.Repeat(cleaned, 33).ToArray() };
        Assert.Equal(32, Validation.Normalize(profile with { Telemetry = many }).Telemetry.Rules.Count);
        Assert.Empty(Validation.Normalize(profile with { Telemetry = null! }).Telemetry.Rules);
        Assert.Empty(JsonSerializer.Deserialize<Profile>("{}", TactileJson.Options)!.Telemetry.Rules);
    }

    [Fact]
    public void Forza_template_replaces_profile_rules_and_survives_json_round_trip()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Profile profile = Assert.Single(store.All);
        Profile saved = store.Save(profile with { Telemetry = TelemetryTemplates.Forza }, out IReadOnlyList<string> reported);
        Assert.Empty(reported);
        Profile reloaded = Assert.Single(new ProfileStore(dir.Path).All);
        Assert.Equal(profile.Id, reloaded.Id);
        Assert.Equal(7, reloaded.Telemetry.Rules.Count);
        Assert.Equal(JsonSerializer.Serialize(saved.Telemetry, TactileJson.Compact),
            JsonSerializer.Serialize(reloaded.Telemetry, TactileJson.Compact));

        var values = Signals with { Rpm = 1, Brake = 1, RumbleStrip = 1 };
        UdpState normal = TelemetryRules.Evaluate(reloaded.Telemetry, values, TimeSpan.Zero);
        Assert.Equal(TriggerEffect.Feedback(1, 5), normal.LeftTrigger);
        Assert.Equal(TriggerEffect.Feedback(2, 4), normal.RightTrigger);
        Assert.Equal(new Rgb(255, 0, 0), normal.Lightbar);
        Assert.Equal((byte)0b11111, normal.PlayerLeds);
        Assert.Equal((byte)120, normal.RumbleRight);

        UdpState assists = TelemetryRules.Evaluate(reloaded.Telemetry, values with { Abs = 1, Traction = 1 }, TimeSpan.Zero);
        Assert.Equal(TriggerEffect.Vibration(1, 6, 35), assists.LeftTrigger);
        Assert.Equal(TriggerEffect.Vibration(2, 5, 40), assists.RightTrigger);
        Assert.Equal(UdpState.Empty, TelemetryRules.Evaluate(reloaded.Telemetry, values with { RaceOn = 0 }, TimeSpan.Zero));
    }

    [Fact]
    public void Rev_lights_and_drs_signals_drive_rules_and_forza_reports_rpm_as_rev_lights()
    {
        var config = new TelemetryConfig
        {
            Rules =
            [
                new() { Signal = TelemetrySignal.RevLights, Target = TelemetryTarget.RumbleLeft, Max = 200 },
                new() { Signal = TelemetrySignal.Drs, From = 0.5, To = 0.5, Target = TelemetryTarget.RumbleRight, Max = 77 },
            ],
        };
        UdpState state = TelemetryRules.Evaluate(config, Signals with { RevLights = 0.5, Drs = 1 }, TimeSpan.Zero);
        Assert.Equal((byte)100, state.RumbleLeft);
        Assert.Equal((byte)77, state.RumbleRight);
        Assert.Null(TelemetryRules.Evaluate(config, Signals, TimeSpan.Zero).RumbleRight);
        Assert.Equal(2, Validation.Normalize(new Profile { Telemetry = config }).Telemetry.Rules.Count);

        Assert.True(ForzaPacket.TryParse(ForzaPacketTests.Packet(324), out TelemetryFrame frame));
        Assert.Equal(frame.Values.Rpm, frame.Values.RevLights);
        Assert.Equal(0, frame.Values.Drs);
    }

    [Theory]
    [InlineData("forza", 120, 35, 40, false)]
    [InlineData("f1", 140, 40, 45, true)]
    [InlineData("acc", 130, 38, 42, false)]
    [InlineData("ac", 120, 35, 40, false)]
    [InlineData("beamNg", 120, 32, 36, true)]
    public void All_five_game_templates_validate_and_drive_expected_targets(
        string name, int rumble, int absFrequency, int tractionFrequency, bool revLightsLightbar)
    {
        Assert.True(TelemetryTemplates.TryGet(name, out TelemetryConfig template));
        Assert.True(TelemetryTemplates.TryGet(name.ToUpperInvariant(), out _));
        var issues = new List<string>();
        TelemetryConfig normalized = Validation.Normalize(new Profile { Telemetry = template }, issues).Telemetry;
        Assert.Empty(issues);
        Assert.Equal(7, normalized.Rules.Count);

        var full = Signals with { Rpm = 1, RevLights = 1, Brake = 1, Abs = 1, Traction = 1, RumbleStrip = 1, Drs = 1 };
        UdpState state = TelemetryRules.Evaluate(normalized, full, TimeSpan.Zero);
        Assert.Equal(TriggerEffect.Vibration(1, 6, absFrequency), state.LeftTrigger);
        Assert.Equal(TriggerEffect.Vibration(2, 5, tractionFrequency), state.RightTrigger);
        Assert.Equal(new Rgb(255, 0, 0), state.Lightbar);
        Assert.Equal((byte)0b11111, state.PlayerLeds);
        Assert.Equal((byte)rumble, state.RumbleRight);

        // Only the signal each game feeds the lightbar with moves it.
        UdpState revOnly = TelemetryRules.Evaluate(normalized, full with { Rpm = 0 }, TimeSpan.Zero);
        UdpState rpmOnly = TelemetryRules.Evaluate(normalized, full with { RevLights = 0 }, TimeSpan.Zero);
        Assert.Equal(revLightsLightbar, revOnly.Lightbar == new Rgb(255, 0, 0));
        Assert.Equal(!revLightsLightbar, rpmOnly.Lightbar == new Rgb(255, 0, 0));
    }

    [Fact]
    public void Unknown_template_names_are_not_found()
    {
        Assert.False(TelemetryTemplates.TryGet(null, out TelemetryConfig config));
        Assert.Empty(config.Rules);
        Assert.False(TelemetryTemplates.TryGet("wrc", out _));
        Assert.True(TelemetryTemplates.TryGet("beamng", out _));
    }
}
