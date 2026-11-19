using System.Text.Json;
using Tactile.Audio;
using Tactile.Core;

namespace Tactile.Tests;

public sealed class HapticProfileTests
{
    [Fact]
    public void Haptics_round_trip_through_profile_store_and_camel_case_json()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var config = new HapticsConfig
        {
            Audio = AudioHapticSource.App,
            App = "Player.exe",
            Mode = HapticMode.Music,
            Strength = 1.75,
            RumbleToHaptics = true,
            Clicks = true,
            ClickStrength = 0.2,
        };
        Profile saved = store.Save(Assert.Single(store.All) with { Haptics = config });
        using JsonDocument doc = JsonDocument.Parse(store.Export(saved.Id));
        JsonElement haptics = doc.RootElement.GetProperty("haptics");
        Assert.Equal("app", haptics.GetProperty("audio").GetString());
        Assert.Equal("music", haptics.GetProperty("mode").GetString());
        Assert.True(haptics.GetProperty("rumbleToHaptics").GetBoolean());
        Assert.Equal(config, new ProfileStore(dir.Path).Get(saved.Id)?.Haptics);
    }

    [Fact]
    public void Haptics_validation_trims_app_and_clamps_ranges_and_unknown_enums()
    {
        var config = new HapticsConfig
        {
            Audio = (AudioHapticSource)20,
            Mode = (HapticMode)20,
            App = "  " + new string('a', 300) + "  ",
            Strength = 3,
            ClickStrength = -1,
        };
        HapticsConfig normalized = Validation.Normalize(new Profile { Haptics = config }).Haptics;
        Assert.Equal(new HapticsConfig { App = new string('a', 260), Strength = 2, ClickStrength = 0 }, normalized);
        const string json = """{"haptics":{"audio":"future","mode":"future","app":"  Player.exe  ","strength":-1,"clickStrength":2}}""";
        Profile read = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!;
        Assert.Equal(new HapticsConfig { App = "Player.exe", Strength = 0, ClickStrength = 1 }, Validation.Normalize(read).Haptics);
    }

    [Fact]
    public void Original_version_one_profile_without_haptics_loads_with_defaults()
    {
        using var dir = new TempDir();
        string id = Validation.NewId();
        Directory.CreateDirectory(dir.Profiles);
        File.WriteAllText(Path.Combine(dir.Profiles, id + ".json"), """{"version":1,"name":"Eski profil"}""");

        Profile loaded = Assert.Single(new ProfileStore(dir.Path).All);
        Assert.Equal(id, loaded.Id);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(new HapticsConfig(), loaded.Haptics);
    }
}
