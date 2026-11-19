using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

[Collection("Localization")]
public class ProfileJsonTests
{
    private const string Sample = """
        {
          "version": 1,
          "id": "3f2a00000000000000000000000000aa",
          "name": "Ghost of Tsushima",
          "games": ["GhostOfTsushima.exe"],
          "lightbar": { "effect": "breathe", "color": "#3060FF", "color2": "#000000", "speed": 1.5, "brightness": "medium" },
          "playerLeds": { "mode": "custom", "number": 1, "mask": 17 },
          "micLed": "pulse",
          "triggers": {
            "left":  { "mode": "zonesVibration", "params": {}, "zones": [0,0,1,2,3,4,5,6,7,8], "frequency": 40 },
            "right": { "mode": "weapon", "params": { "start": 3, "end": 6, "strength": 8 } }
          },
          "triggerResponse": {
            "left":  { "deadzone": 10, "max": 240, "curve": "exponential", "exponent": 2.0, "points": null },
            "right": { "deadzone": 0, "max": 255, "curve": "custom", "exponent": 1.0,
                       "points": [[0,0],[0.25,0.1],[0.5,0.4],[0.75,0.8],[1,1]] }
          },
          "sticks": {
            "left":  { "innerDeadzone": 0.1, "outerDeadzone": 0.05, "shape": "square", "curve": "linear",
                       "exponent": 1.0, "points": null, "antiDeadzone": 0.2, "invertX": true, "invertY": false },
            "right": { "innerDeadzone": 0.05 }
          },
          "gyro": { "sensitivity": 2.0, "smoothing": 0.3, "invertX": false, "invertY": true },
          "rumble": { "strength": 1.5 },
          "somethingFromTheFuture": { "a": 1 }
        }
        """;

    [Fact]
    public void Reads_the_contract_sample_and_ignores_unknown_fields()
    {
        Profile p = JsonSerializer.Deserialize<Profile>(Sample, TactileJson.Options)!;

        Assert.Equal("Ghost of Tsushima", p.Name);
        Assert.Equal(["GhostOfTsushima.exe"], p.Games);
        Assert.Equal(LightbarEffect.Breathe, p.Lightbar.Effect);
        Assert.Equal(LedBrightness.Medium, p.Lightbar.Brightness);
        Assert.Equal(PlayerLedMode.Custom, p.PlayerLeds.Mode);
        Assert.Equal(17, p.PlayerLeds.Mask);
        Assert.Equal(MicLed.Pulse, p.MicLed);
        Assert.Equal(TriggerMode.ZonesVibration, p.Triggers.Left.Mode);
        Assert.Equal(40, p.Triggers.Left.Frequency);
        Assert.Equal(8, p.Triggers.Right.Params["strength"]);
        Assert.Equal(CurveKind.Custom, p.TriggerResponse.Right.Curve);
        Assert.Equal(StickShape.Square, p.Sticks.Left.Shape);
        Assert.True(p.Sticks.Left.InvertX);
        Assert.Equal(0.02, p.Sticks.Right.OuterDeadzone);
        Assert.True(p.Gyro.InvertY);
        Assert.Equal(1.5, p.Rumble.Strength);
    }

    [Fact]
    public void Missing_fields_take_defaults()
    {
        Profile p = JsonSerializer.Deserialize<Profile>("""{ "name": "x" }""", TactileJson.Options)!;

        Assert.Equal(1, p.Version);
        Assert.Empty(p.Games);
        Assert.Equal(LightbarEffect.Static, p.Lightbar.Effect);
        Assert.Equal("#3060FF", p.Lightbar.Color);
        Assert.Equal(PlayerLedMode.Number, p.PlayerLeds.Mode);
        Assert.Equal(TriggerMode.Off, p.Triggers.Left.Mode);
        Assert.Equal(255, p.TriggerResponse.Left.Max);
        Assert.Equal(0.05, p.Sticks.Left.InnerDeadzone);
        Assert.Equal(1.0, p.Rumble.Strength);

        AppSettings s = JsonSerializer.Deserialize<AppSettings>("{}", TactileJson.Options)!;
        Assert.True(s.CloseToTray);
        Assert.Equal(800, s.AutoProfileDelayMs);
        Assert.Equal([20, 10], s.LowBatteryWarnings);
        Assert.Equal(15, s.IdleSleepMinutes);
        Assert.Equal(Language.En, s.Language);
    }

    [Fact]
    public void Round_trip_keeps_everything_and_writes_camel_case_strings()
    {
        Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(Sample, TactileJson.Options)!);
        string json = JsonSerializer.Serialize(p, TactileJson.Options);

        Assert.Contains("\"effect\": \"breathe\"", json);
        Assert.Contains("\"mode\": \"zonesVibration\"", json);
        Assert.Contains("\"micLed\": \"pulse\"", json);
        Assert.Contains("\"brightness\": \"medium\"", json);

        Profile back = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(Validation.Normalize(back), TactileJson.Options));
    }

    [Fact]
    public void Settings_round_trip()
    {
        var s = new AppSettings
        {
            Language = Language.En,
            ControllerDefaults = new Dictionary<string, string> { ["AA:BB:CC:DD:EE:FF"] = new string('a', 32) },
            LowBatteryWarnings = [30, 15],
        };
        string json = JsonSerializer.Serialize(s, TactileJson.Options);
        Assert.Contains("\"language\": \"en\"", json);

        AppSettings back = JsonSerializer.Deserialize<AppSettings>(json, TactileJson.Options)!;
        Assert.Equal(Language.En, back.Language);
        Assert.Equal(new string('a', 32), back.ControllerDefaults["AA:BB:CC:DD:EE:FF"]);
        Assert.Equal([30, 15], back.LowBatteryWarnings);
    }

    [Fact]
    public void Normalize_clamps_ranges_and_reports_bad_triggers()
    {
        using var language = new TestLanguage(Language.Tr);
        var p = new Profile
        {
            Id = "not-an-id",
            Name = "   ",
            Games = ["C:\\Games\\a.exe", "A.EXE", ""],
            Lightbar = new LightbarConfig { Color = "red", Speed = 50 },
            PlayerLeds = new PlayerLedConfig { Number = 9, Mask = 0xFF },
            Triggers = new TriggerPair
            {
                Left = new TriggerConfig { Mode = TriggerMode.Weapon, Params = new Dictionary<string, double> { ["start"] = 0, ["end"] = 6, ["strength"] = 8 } },
                Right = new TriggerConfig { Mode = TriggerMode.Zones, Zones = [1, 2] },
            },
            TriggerResponse = new TriggerResponsePair { Left = new TriggerResponse { Deadzone = 300, Max = 5, Curve = CurveKind.Custom } },
            Sticks = new StickPair { Left = new StickConfig { InnerDeadzone = 2, Exponent = 10 } },
            Rumble = new RumbleConfig { Strength = double.NaN },
        };
        var issues = new List<string>();
        Profile n = Validation.Normalize(p, issues);

        Assert.True(Validation.IsId(n.Id));
        Assert.Equal("Profil", n.Name);
        Assert.Equal(["a.exe"], n.Games);
        Assert.Equal("#3060FF", n.Lightbar.Color);
        Assert.Equal(5, n.Lightbar.Speed);
        Assert.Equal(4, n.PlayerLeds.Number);
        Assert.Equal(0x1F, n.PlayerLeds.Mask);
        Assert.Equal(TriggerMode.Off, n.Triggers.Left.Mode);
        Assert.Equal(TriggerMode.Off, n.Triggers.Right.Mode);
        Assert.Equal(254, n.TriggerResponse.Left.Deadzone);
        Assert.Equal(255, n.TriggerResponse.Left.Max);
        Assert.Equal(CurveKind.Linear, n.TriggerResponse.Left.Curve);
        Assert.Equal(0.9, n.Sticks.Left.InnerDeadzone);
        Assert.Equal(4, n.Sticks.Left.Exponent);
        Assert.Equal(1, n.Rumble.Strength);
        Assert.Equal(3, issues.Count);
    }

    [Fact]
    public void Normalize_settings()
    {
        AppSettings s = Validation.Normalize(new AppSettings
        {
            AutoProfileDelayMs = -5,
            IdleSleepMinutes = 9999,
            LowBatteryWarnings = [10, 0, 20, 10, 150],
            ControllerDefaults = new Dictionary<string, string> { ["aa:bb:cc:dd:ee:ff"] = new string('b', 32), ["x"] = "bad" },
            DefaultProfileId = "nope",
        });

        Assert.Equal(0, s.AutoProfileDelayMs);
        Assert.Equal(240, s.IdleSleepMinutes);
        Assert.Equal([20, 10], s.LowBatteryWarnings);
        Assert.Single(s.ControllerDefaults);
        Assert.True(s.ControllerDefaults.ContainsKey("AA:BB:CC:DD:EE:FF"));
        Assert.Null(s.DefaultProfileId);
    }
}
