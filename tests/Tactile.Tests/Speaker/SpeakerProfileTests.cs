using System.Text.Json;
using Tactile.Core;

namespace Tactile.Tests;

public sealed class SpeakerProfileTests
{
    [Fact]
    public void Speaker_fields_round_trip_and_old_haptics_profiles_load_with_defaults()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var config = new HapticsConfig { Speaker = AudioHapticSource.App, SpeakerApp = "Player.exe", SpeakerVolume = 0.42 };
        Profile saved = store.Save(Assert.Single(store.All) with { Haptics = config });
        using var doc = JsonDocument.Parse(store.Export(saved.Id));
        JsonElement fields = doc.RootElement.GetProperty("haptics");
        Assert.Equal("app", fields.GetProperty("speaker").GetString());
        Assert.Equal("Player.exe", fields.GetProperty("speakerApp").GetString());
        Assert.Equal(0.42, fields.GetProperty("speakerVolume").GetDouble());
        Assert.Equal(config, new ProfileStore(dir.Path).Get(saved.Id)?.Haptics);

        Profile old = JsonSerializer.Deserialize<Profile>("""{"haptics":{"audio":"system"}}""", TactileJson.Options)!;
        Assert.Equal(new HapticsConfig { Audio = AudioHapticSource.System }, Validation.Normalize(old).Haptics);
    }

    [Theory]
    [InlineData(double.NaN, 0.8)]
    [InlineData(-0.1, 0)]
    [InlineData(1.1, 1)]
    public void Speaker_validation_normalizes_source_name_and_volume(double volume, double expected)
    {
        var config = new HapticsConfig
        {
            Speaker = (AudioHapticSource)42,
            SpeakerApp = "  " + new string('a', 261) + " ",
            SpeakerVolume = volume,
        };
        Assert.Equal(new HapticsConfig { SpeakerApp = new string('a', 260), SpeakerVolume = expected },
            Validation.Normalize(new Profile { Haptics = config }).Haptics);
    }
}
