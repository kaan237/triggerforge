using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class StoreTests
{
    [Fact]
    public void Legacy_shift_layer_migrates_executes_and_round_trips_with_its_mirror()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Profiles);
        string id = Validation.NewId();
        File.WriteAllText(Path.Combine(dir.Profiles, id + ".json"), """
            {
              "version": 1, "name": "Legacy",
              "mapping": {
                "enabled": true,
                "layerButton": "l1",
                "buttons": { "cross": { "press": { "kind": "key", "keys": ["a"] } } },
                "layer": { "cross": { "press": { "kind": "key", "keys": ["b"] } } }
              }
            }
            """);
        var store = new ProfileStore(dir.Path);
        Profile profile = Assert.Single(store.All);
        MappingLayer layer = Assert.Single(profile.Mapping.Layers);
        Assert.Equal(LayerMode.Hold, layer.Mode);
        Assert.Equal("l1", layer.Button);
        Assert.Equal("l1", profile.Mapping.LayerButton);
        Assert.Same(layer.Buttons, profile.Mapping.Layer);
        Assert.Equal([Key.B], layer.Buttons["cross"].Press.Keys!);

        store.Save(profile);
        Profile reloaded = Assert.Single(new ProfileStore(dir.Path).All);
        Assert.Equal(store.Export(profile.Id), new ProfileStore(dir.Path).Export(reloaded.Id));
        Assert.Same(reloaded.Mapping.Layers[0].Buttons, reloaded.Mapping.Layer);
        using var rig = new MapperRig(reloaded.Mapping);
        rig.Press(Buttons.L1).Press(Buttons.Cross).Release(Buttons.Cross).Release(Buttons.L1);
        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["B down", "B up", "A down", "A up"], rig.Take());
    }

    [Fact]
    public void Import_strips_every_phase_11_run_without_counting_the_legacy_mirror_twice()
    {
        using var dir = new TempDir();
        var run = new ActionConfig { Kind = ActionKind.Run, Path = @"C:\Tools\example.exe" };
        var binding = new Binding { Press = run };
        var trigger = new TriggerMapping { Mode = TriggerStageMode.Additive, Soft = run, Full = run };
        var ring = new StickMapping { RingMode = StickRingMode.Outer, RingAction = run };
        var profile = new Profile
        {
            Mapping = new MappingConfig
            {
                Buttons = Bind.Map(("circle", binding)),
                Layer = Bind.Map(("cross", binding)),
                Layers =
                [
                    new() { Button = "l1", Buttons = Bind.Map(("cross", binding)) },
                    new() { Button = "r1", Buttons = Bind.Map(("cross", binding)) },
                ],
                Chords =
                [
                    new() { Buttons = ["cross", "square"], Action = run },
                    new() { Buttons = ["cross", "triangle"], Action = Bind.Keys(Key.B) },
                ],
                Triggers = new() { Left = trigger, Right = trigger },
                Sticks = new() { Left = ring, Right = ring },
            },
        };
        MappingConfig imported = new ProfileStore(dir.Path).Import(JsonSerializer.Serialize(profile, TactileJson.Options), out int removed).Mapping;
        Assert.Equal(10, removed);
        Assert.Equal(ActionKind.None, imported.Buttons["circle"].Press.Kind);
        Assert.All(imported.Layers, l => Assert.Equal(ActionKind.None, l.Buttons["cross"].Press.Kind));
        Assert.Same(imported.Layers[0].Buttons, imported.Layer);
        Assert.Equal(ActionKind.Key, Assert.Single(imported.Chords).Action.Kind);
        foreach (TriggerMapping item in new[] { imported.Triggers.Left, imported.Triggers.Right })
        {
            Assert.Equal(ActionKind.None, item.Soft.Kind);
            Assert.Equal(ActionKind.None, item.Full.Kind);
        }

        Assert.Equal(ActionKind.None, imported.Sticks.Left.RingAction.Kind);
        Assert.Equal(ActionKind.None, imported.Sticks.Right.RingAction.Kind);
    }

    [Fact]
    public void Phase_8_files_load_with_phase_10_defaults_and_keep_existing_behavior()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Profiles);
        string id = Validation.NewId();
        File.WriteAllText(Path.Combine(dir.Profiles, id + ".json"), """
            {
              "version": 1, "name": "Legacy", "games": ["game.exe"],
              "mapping": { "enabled": true, "buttons": { "cross": { "press": { "kind": "key", "keys": ["a"] } } },
                "gyroMouse": { "mode": "toggle" } },
              "emulation": { "mode": "xbox360", "swapSticks": true },
              "haptics": { "clicks": true }
            }
            """);
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), """
            { "version": 1, "edgeShortcuts": false, "closeToTray": false, "autoProfileDelayMs": 100 }
            """);

        Profile profile = Assert.Single(new ProfileStore(dir.Path).All);
        AppSettings settings = new SettingsStore(dir.Path).Current;
        var expected = Validation.Normalize(new Profile
        {
            Id = id, Name = "Legacy", Games = ["game.exe"],
            Mapping = Bind.Buttons(("cross", Bind.Key(Tactile.Input.Key.A))) with
            {
                Enabled = true, GyroMouse = new GyroMouseConfig { Mode = GyroMouseMode.Toggle },
            },
            Emulation = new EmulationConfig { Mode = EmulationMode.Xbox360, SwapSticks = true },
            Haptics = new HapticsConfig { Clicks = true },
        });
        var expectedSettings = Validation.Normalize(new AppSettings
        {
            EdgeShortcuts = false, CloseToTray = false, AutoProfileDelayMs = 100,
        });
        Assert.Equal(JsonSerializer.Serialize(expected, TactileJson.Options), JsonSerializer.Serialize(profile, TactileJson.Options));
        Assert.Equal(JsonSerializer.Serialize(expectedSettings, TactileJson.Options), JsonSerializer.Serialize(settings, TactileJson.Options));
        Assert.Equal(new GyroStickConfig(), profile.Emulation.GyroStick);
        Assert.False(profile.Haptics.ConfirmPulses);
        Assert.Null(profile.Mapping.Buttons["cross"].Triple);
        Assert.Null(profile.Mapping.Buttons["cross"].Cycle);
        Assert.Empty(settings.PauseCombo);
        Assert.Empty(settings.PauseHotkey);
        Assert.False(settings.PauseOnFullscreen);
        Assert.Equal(7, settings.BackupCount);
        using var rig = new MapperRig(profile.Mapping);
        rig.Press(Tactile.Device.DualSense.Buttons.Cross).Release(Tactile.Device.DualSense.Buttons.Cross);
        Assert.Equal(["A down", "A up"], rig.Take());
    }

    [Fact]
    public void Import_strips_run_in_triple_and_cycles_and_counts_each_removed_entry()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var run = new ActionConfig { Kind = ActionKind.Run, Path = @"C:\Tools\example.exe" };
        ActionConfig key = Bind.Keys(Tactile.Input.Key.A);
        var profile = new Profile
        {
            Mapping = new MappingConfig
            {
                Buttons = Bind.Map(("cross", new Binding { Triple = run, Cycle = [run, key, run] })),
                Layer = Bind.Map(("square", new Binding { Cycle = [run] })),
                Touchpad = new TouchpadMapping { Zones = [new Binding { Triple = run }, new Binding()] },
            },
        };
        Profile imported = store.Import(JsonSerializer.Serialize(profile, TactileJson.Options), out int removed);

        Assert.Equal(5, removed);
        Assert.Null(imported.Mapping.Buttons["cross"].Triple);
        Assert.Equal(ActionKind.Key, Assert.Single(imported.Mapping.Buttons["cross"].Cycle!).Kind);
        Assert.Null(imported.Mapping.Layer["square"].Cycle);
        Assert.Null(imported.Mapping.Touchpad.Zones[0].Triple);
    }

    [Fact]
    public void Phase_10_validation_filters_and_caps_new_settings_and_cycle_actions()
    {
        AppSettings settings = Validation.Normalize(new AppSettings
        {
            PauseCombo = [" R1 ", "r1", "invalid", "PS", "Fn1", "l2"],
            PauseHotkey = [Tactile.Input.Key.None, Tactile.Input.Key.A, Tactile.Input.Key.A, Tactile.Input.Key.B,
                Tactile.Input.Key.C, Tactile.Input.Key.D, Tactile.Input.Key.E],
            BackupCount = 31,
        });
        Assert.Equal(["r1", "ps", "fn1"], settings.PauseCombo);
        Assert.Equal(4, settings.PauseHotkey.Count);
        Assert.Equal(30, settings.BackupCount);
        Assert.Empty(Validation.Normalize(settings with { PauseCombo = ["r1", "R1"] }).PauseCombo);

        var binding = new Binding
        {
            Triple = ActionConfig.None,
            Cycle = [ActionConfig.None, .. Enumerable.Repeat(Bind.Keys(Tactile.Input.Key.A), 9)],
        };
        var issues = new List<string>();
        Profile profile = Validation.Normalize(new Profile { Mapping = Bind.Buttons(("cross", binding)) }, issues);
        Assert.Equal(8, profile.Mapping.Buttons["cross"].Cycle!.Count);
        Assert.Null(profile.Mapping.Buttons["cross"].Triple);
        Assert.Single(issues);

        var emulation = new EmulationConfig
        {
            GyroStick = new GyroStickConfig { Mode = GyroMouseMode.WhileTouchpad, AntiDeadzone = 0.2 },
        };
        string json = JsonSerializer.Serialize(emulation, TactileJson.Compact);
        Assert.Contains("\"gyroStick\":{", json);
        Assert.Equal(emulation.GyroStick, JsonSerializer.Deserialize<EmulationConfig>(json, TactileJson.Options)!.GyroStick);
    }

    [Fact]
    public void Daily_backups_capture_before_changes_once_per_date_and_prune_only_daily_folders()
    {
        using var dir = new TempDir();
        var today = new DateOnly(2026, 1, 1);
        var store = new ProfileStore(dir.Path, () => 2, () => today);
        Profile initial = Assert.Single(store.All);
        string root = Path.Combine(dir.Path, "backups");
        string day1 = Path.Combine(root, "2026-01-01");
        Directory.CreateDirectory(Path.Combine(root, "keep-me"));
        Directory.CreateDirectory(Path.Combine(root, "pre-restore-20251231-100000"));
        string saved = Path.Combine(day1, initial.Id + ".json");
        string original = File.ReadAllText(saved);

        Profile changed = store.Save(initial with { Name = "Updated" });
        Assert.Equal(original, File.ReadAllText(saved));
        today = today.AddDays(1);
        Profile second = store.Save(new Profile { Name = "Second" });
        string day2 = Path.Combine(root, "2026-01-02");
        Assert.Single(Directory.GetFiles(day2, "*.json"));
        Assert.Equal(changed.Name, JsonSerializer.Deserialize<Profile>(
            File.ReadAllText(Path.Combine(day2, changed.Id + ".json")), TactileJson.Options)!.Name);

        today = today.AddDays(1);
        Assert.True(store.Delete(second.Id));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "2026-01-03"), "*.json").Length);
        Assert.False(Directory.Exists(day1));
        Assert.True(Directory.Exists(Path.Combine(root, "keep-me")));
        Assert.Equal(["2026-01-03", "2026-01-02", "pre-restore-20251231-100000"], store.ListBackups().Select(entry => entry.Id));
    }

    [Fact]
    public void Restore_replaces_the_set_fires_changed_and_saves_pre_restore_without_stripping_run()
    {
        using var dir = new TempDir();
        var today = new DateOnly(2026, 1, 1);
        var store = new ProfileStore(dir.Path, () => 7, () => today);
        var run = new ActionConfig { Kind = ActionKind.Run, Path = @"C:\Tools\example.exe" };
        Profile original = store.Save(store.All[0] with { Mapping = Bind.Buttons(("cross", new Binding { Triple = run, Cycle = [run] })) });
        today = today.AddDays(1);
        Profile extra = store.Save(new Profile { Name = "Extra" });
        store.Save(original with { Name = "Changed", Mapping = new MappingConfig() });
        int changes = 0;
        store.Changed += () => changes++;

        Assert.Equal(1, store.RestoreBackup("2026-01-02"));
        Assert.Equal(1, changes);
        Profile restored = Assert.Single(store.All);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(ActionKind.Run, restored.Mapping.Buttons["cross"].Triple!.Kind);
        Assert.Equal(ActionKind.Run, Assert.Single(restored.Mapping.Buttons["cross"].Cycle!).Kind);
        Assert.Null(store.Get(extra.Id));

        ProfileBackupEntry before = Assert.Single(store.ListBackups(), entry => entry.Id.StartsWith("pre-restore-", StringComparison.Ordinal));
        Assert.Equal(2, before.ProfileCount);
        Assert.Equal(2, store.RestoreBackup(before.Id));
        Assert.Equal("Extra", store.Get(extra.Id)!.Name);
        Assert.Equal("Changed", store.Get(original.Id)!.Name);
        store.RestoreBackup("2026-01-02");
        store.RestoreBackup("2026-01-02");
        Assert.Equal(3, store.ListBackups().Count(entry => entry.Id.StartsWith("pre-restore-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(@"..\x")]
    [InlineData(@"2026-13-99\..\..")]
    [InlineData(@"C:\2026-01-01")]
    [InlineData("2026-01-01\n")]
    public void Restore_rejects_unrecognized_ids(string id)
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Assert.Throws<ArgumentException>(() => store.RestoreBackup(id));
        Assert.DoesNotContain(store.ListBackups(), entry => entry.Id.StartsWith("pre-restore-", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_restore_rolls_back_disk_and_memory_and_keeps_the_pre_restore_copy()
    {
        using var dir = new TempDir();
        var today = new DateOnly(2026, 1, 1);
        var store = new ProfileStore(dir.Path, () => 7, () => today);
        Profile original = Assert.Single(store.All);
        Profile changed = store.Save(original with { Name = "Updated" });
        Profile added = store.Save(new Profile { Name = "Added" });
        int changes = 0;
        store.Changed += () => changes++;
        string snapshot = Path.Combine(dir.Path, "backups", "2026-01-01");
        File.WriteAllText(Path.Combine(snapshot, original.Id + ".json"), "{ broken");

        string[] older = ["pre-restore-20250101-000000", "pre-restore-20250102-000000", "pre-restore-20250103-000000"];
        foreach (string id in older)
        {
            Directory.CreateDirectory(Path.Combine(dir.Path, "backups", id));
        }

        JsonException error = Assert.Throws<JsonException>(() => store.RestoreBackup("2026-01-01"));
        var translation = Assert.IsType<LocalizedMessage>(error.Data[typeof(LocalizedMessage)]);
        Assert.Contains("Corrupt backup file", translation.En);
        Assert.Contains("Bozuk yedek dosyası", translation.Tr);
        Assert.Equal(0, changes);
        Assert.Same(changed, store.Get(changed.Id));
        Assert.Same(added, store.Get(added.Id));
        var reloaded = new ProfileStore(dir.Path, () => 0);
        Assert.Equal("Updated", reloaded.Get(changed.Id)!.Name);
        Assert.Equal("Added", reloaded.Get(added.Id)!.Name);
        Assert.Contains(store.ListBackups(), entry =>
            entry.Id.StartsWith("pre-restore-", StringComparison.Ordinal) && entry.ProfileCount == 2);
        Assert.All(older, id => Assert.Contains(store.ListBackups(), entry => entry.Id == id));
    }

    [Fact]
    public void Delete_keeps_profile_when_backup_directory_denies_writes()
    {
        using var dir = new TempDir();
        var today = new DateOnly(2026, 1, 1);
        var notices = new List<Notice>();
        var store = new ProfileStore(dir.Path, () => 7, () => today, notices.Add);
        Profile extra = store.Save(new Profile { Name = "Keep" });
        string path = Path.Combine(dir.Profiles, extra.Id + ".json");
        var backups = new DirectoryInfo(Path.Combine(dir.Path, "backups"));
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var deny = new FileSystemAccessRule(identity.User!, FileSystemRights.CreateDirectories, AccessControlType.Deny);
        DirectorySecurity restricted = backups.GetAccessControl();
        restricted.AddAccessRule(deny);
        backups.SetAccessControl(restricted);
        try
        {
            today = today.AddDays(1);
            Assert.Throws<UnauthorizedAccessException>(() => store.Delete(extra.Id));
            Assert.Same(extra, store.Get(extra.Id));
            Assert.True(File.Exists(path));
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(deny);
            backups.SetAccessControl(restricted);
        }

        Assert.True(store.Delete(extra.Id));
        Assert.Contains(store.ListBackups(), entry => entry.Id == "2026-01-02" && entry.ProfileCount == 2);
    }

    [Fact]
    public void Numeric_pause_hotkeys_load_and_ignore_invalid_values()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), """
            { "pauseHotkey": [30, 57416, -65506, 65566, 2147483648, 0, 1.5, {}, [], "bad", 30, "leftCtrl"] }
            """);
        var settings = new SettingsStore(dir.Path);

        Assert.Empty(settings.BrokenFiles);
        Assert.Equal([Key.A, Key.Up, Key.LeftCtrl], settings.Current.PauseHotkey);
    }

    [Fact]
    public void Standalone_profile_store_keeps_seven_daily_backups_without_reading_settings()
    {
        using var dir = new TempDir();
        string settings = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(settings, "{ broken");
        for (int day = 1; day <= 7; day++)
        {
            Directory.CreateDirectory(Path.Combine(dir.Path, "backups", $"2000-01-0{day}"));
        }

        var store = new ProfileStore(dir.Path);

        Assert.Equal("{ broken", File.ReadAllText(settings));
        Assert.Equal(7, store.ListBackups().Count);
        Assert.DoesNotContain(store.ListBackups(), entry => entry.Id == "2000-01-01");
    }

    [Fact]
    public void Zero_retention_disables_automatic_backup()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path, () => 0);
        store.Save(new Profile { Name = "Extra" });
        Assert.Empty(store.ListBackups());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "backups")));
    }

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
