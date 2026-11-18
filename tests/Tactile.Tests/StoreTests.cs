using System.Text.Json;
using Tactile.Core;

namespace Tactile.Tests;

public class StoreTests
{
    [Fact]
    public void Empty_dir_gets_a_default_profile_on_disk()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);

        Profile p = Assert.Single(store.All);
        Assert.Equal(ProfileStore.DefaultName, p.Name);
        Assert.True(File.Exists(Path.Combine(dir.Profiles, p.Id + ".json")));
        Assert.Empty(store.BrokenFiles);
    }

    [Fact]
    public void Save_writes_atomically_and_reloads()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        int changed = 0;
        store.Changed += () => changed++;

        Profile saved = store.Save(new Profile { Name = "Racing", Games = ["forza.exe"], Lightbar = new LightbarConfig { Color = "#ff0000" } });

        Assert.Equal(1, changed);
        Assert.Equal("#FF0000", saved.Lightbar.Color);
        Assert.Empty(Directory.GetFiles(dir.Profiles, "*.tmp"));

        var again = new ProfileStore(dir.Path);
        Profile loaded = again.Get(saved.Id)!;
        Assert.Equal("Racing", loaded.Name);
        Assert.Equal(["forza.exe"], loaded.Games);
        Assert.Equal(2, again.All.Count);
    }

    [Fact]
    public void Save_reports_issues()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Profile p = store.Save(new Profile
        {
            Name = "Bad",
            Triggers = new TriggerPair { Right = new TriggerConfig { Mode = TriggerMode.Feedback } },
        }, out IReadOnlyList<string> issues);

        Assert.Equal(TriggerMode.Off, p.Triggers.Right.Mode);
        Assert.Single(issues);
    }

    [Fact]
    public void Broken_file_is_renamed_and_listed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Profiles);
        string bad = Path.Combine(dir.Profiles, new string('c', 32) + ".json");
        File.WriteAllText(bad, "{ this is not json");

        var store = new ProfileStore(dir.Path);

        Assert.False(File.Exists(bad));
        string broken = Assert.Single(store.BrokenFiles);
        Assert.Contains(".json.broken-", broken);
        Assert.True(File.Exists(broken));
        Assert.Equal(ProfileStore.DefaultName, Assert.Single(store.All).Name);
    }

    [Fact]
    public void Broken_settings_fall_back_to_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "[1,2");

        var settings = new SettingsStore(dir.Path);

        Assert.Equal(800, settings.Current.AutoProfileDelayMs);
        Assert.Single(settings.BrokenFiles);
        Assert.False(File.Exists(Path.Combine(dir.Path, "settings.json")));
    }

    [Fact]
    public void Settings_persist()
    {
        using var dir = new TempDir();
        new SettingsStore(dir.Path).Save(new AppSettings { IdleSleepMinutes = 5, Language = Language.En });

        var again = new SettingsStore(dir.Path);
        Assert.Equal(5, again.Current.IdleSleepMinutes);
        Assert.Equal(Language.En, again.Current.Language);
    }

    [Fact]
    public void Env_var_overrides_data_dir()
    {
        using var dir = new TempDir();
        string? old = Environment.GetEnvironmentVariable("TACTILE_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TACTILE_DATA_DIR", dir.Path);
            var store = new ProfileStore();
            Assert.StartsWith(dir.Path, store.ProfilesDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TACTILE_DATA_DIR", old);
        }
    }

    [Fact]
    public void Last_profile_cannot_be_deleted()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        string first = store.All[0].Id;

        Assert.False(store.Delete(first));
        Assert.False(store.Delete("unknown"));

        Profile second = store.Save(new Profile { Name = "Second" });
        Assert.True(store.Delete(first));
        Assert.False(File.Exists(Path.Combine(dir.Profiles, first + ".json")));
        Assert.False(store.Delete(second.Id));
    }

    [Fact]
    public void Duplicate_gets_new_id()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Profile original = store.Save(new Profile { Name = "A", Games = ["a.exe"] });

        Profile copy = store.Duplicate(original.Id, "A kopya");

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("A kopya", copy.Name);
        Assert.Equal(["a.exe"], copy.Games);
        Assert.Throws<KeyNotFoundException>(() => store.Duplicate("missing", "x"));
    }

    [Fact]
    public void Export_then_import_makes_a_validated_copy()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Profile original = store.Save(new Profile
        {
            Name = "Shooter",
            Triggers = new TriggerPair
            {
                Right = new TriggerConfig { Mode = TriggerMode.Weapon, Params = new Dictionary<string, double> { ["start"] = 3, ["end"] = 6, ["strength"] = 8 } },
            },
        });

        string json = store.Export(original.Id);
        Assert.Contains("\n", json);
        Assert.Contains("\"weapon\"", json);

        Profile imported = store.Import(json);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(TriggerMode.Weapon, imported.Triggers.Right.Mode);

        Assert.Throws<ArgumentException>(() => store.Import("{ nope"));
        Assert.Throws<KeyNotFoundException>(() => store.Export("missing"));
    }

    [Fact]
    public void Import_falls_back_on_invalid_trigger()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Profile p = store.Import("""{ "name": "x", "triggers": { "left": { "mode": "bow", "params": { "start": 5, "end": 2 } } } }""");
        Assert.Equal(TriggerMode.Off, p.Triggers.Left.Mode);
    }

    [Fact]
    public void Concurrent_saves_do_not_corrupt()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Parallel.For(0, 40, i => store.Save(new Profile { Name = "P" + i }));

        Assert.Equal(41, store.All.Count);
        foreach (string f in Directory.GetFiles(dir.Profiles, "*.json"))
        {
            Assert.NotNull(JsonSerializer.Deserialize<Profile>(File.ReadAllText(f), TactileJson.Options));
        }
    }
}
