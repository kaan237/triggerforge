using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Drivers;

namespace Tactile.Tests.EmulationCore;

public class EmulationModelTests
{
    [Fact]
    public void Profile_emulation_round_trips_in_camel_case()
    {
        var p = new Profile
        {
            Name = "x",
            Emulation = new EmulationConfig
            {
                Mode = EmulationMode.DualSense,
                Remap = new Dictionary<string, string> { ["cross"] = "circle", ["l2"] = "none" },
                SwapSticks = true,
                HideMappedButtons = false,
            },
        };

        string json = JsonSerializer.Serialize(p, TactileJson.Compact);
        Assert.Contains("\"emulation\":{\"mode\":\"dualSense\",\"remap\":{\"cross\":\"circle\",\"l2\":\"none\"},\"swapSticks\":true,\"hideMappedButtons\":false}", json);

        Profile back = Validation.Normalize(JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!);
        Assert.Equal(EmulationMode.DualSense, back.Emulation.Mode);
        Assert.Equal(p.Emulation.Remap, back.Emulation.Remap);
        Assert.True(back.Emulation.SwapSticks);
        Assert.False(back.Emulation.HideMappedButtons);
        Assert.Contains("\"mode\":\"xbox360\"", JsonSerializer.Serialize(new EmulationConfig { Mode = EmulationMode.Xbox360 }, TactileJson.Compact));
    }

    [Fact]
    public void Missing_or_null_emulation_takes_defaults()
    {
        foreach (string json in new[] { """{ "name": "x" }""", """{ "name": "x", "emulation": null }""", """{ "name": "x", "emulation": {} }""" })
        {
            Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!);
            Assert.Equal(EmulationMode.Off, p.Emulation.Mode);
            Assert.Empty(p.Emulation.Remap);
            Assert.False(p.Emulation.SwapSticks);
            Assert.True(p.Emulation.HideMappedButtons);
        }
    }

    [Fact]
    public void Bad_values_are_dropped_with_issues()
    {
        const string json = """
            { "name": "x", "emulation": { "mode": "ps2", "remap": { "Cross": "CIRCLE", "jump": "cross", "square": "fly", "circle": "circle", "r2": "None" } } }
            """;
        var issues = new List<string>();
        Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!, issues);

        Assert.Equal(EmulationMode.Off, p.Emulation.Mode);
        Assert.Equal(new Dictionary<string, string> { ["cross"] = "circle", ["r2"] = "none" }, p.Emulation.Remap);
        Assert.Equal(2, issues.Count(i => i.StartsWith("Sanal kontrolcü", StringComparison.Ordinal)));
    }

    [Fact]
    public void Settings_emulation_round_trips_and_defaults_on()
    {
        AppSettings d = Validation.Normalize(JsonSerializer.Deserialize<AppSettings>("{}", TactileJson.Options)!);
        Assert.True(d.Emulation.Enabled);
        Assert.True(d.Emulation.HideController);

        AppSettings nulled = Validation.Normalize(JsonSerializer.Deserialize<AppSettings>("""{ "emulation": null }""", TactileJson.Options)!);
        Assert.True(nulled.Emulation.Enabled);

        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        store.Save(store.Current with { Emulation = new EmulationSettings { Enabled = false, HideController = false } });
        Assert.Contains("\"emulation\"", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
        AppSettings loaded = new SettingsStore(dir.Path).Current;
        Assert.False(loaded.Emulation.Enabled);
        Assert.False(loaded.Emulation.HideController);
    }

    [Fact]
    public void Import_and_store_keep_emulation()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        Profile saved = store.Save(new Profile
        {
            Name = "Oyun",
            Emulation = new EmulationConfig { Mode = EmulationMode.Xbox360, Remap = new Dictionary<string, string> { ["triangle"] = "square" } },
        });

        Profile imported = store.Import(store.Export(saved.Id));
        Assert.Equal(EmulationMode.Xbox360, imported.Emulation.Mode);
        Assert.Equal("square", imported.Emulation.Remap["triangle"]);

        Profile reloaded = new ProfileStore(dir.Path).Get(saved.Id)!;
        Assert.Equal(EmulationMode.Xbox360, reloaded.Emulation.Mode);
    }
}

public class EmulationAppCoreTests
{
    private static AppCore Create(TempDir dir, ManualTime time, EmulationRig? rig = null) =>
        new(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), time, runTimer: false,
            drivers: rig?.Platform, server: rig?.Server, steamRunning: () => false);

    [Fact]
    public void Output_layer_is_sent_at_once_without_a_tick()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        using AppCore core = Create(dir, time);
        core.Tick(TimeSpan.Zero);
        Rgb profileColor = core.Controllers.Output.Lightbar;

        core.SetOutputLayerNow(EmulationService.GameLayer, o => o with { Lightbar = new Rgb(1, 2, 3), RumbleLeft = 9 });
        Assert.Equal(new Rgb(1, 2, 3), core.Controllers.Output.Lightbar);
        Assert.Equal(9, core.Controllers.Output.RumbleLeft);

        // The next tick keeps it; clearing hands back to the profile.
        core.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(new Rgb(1, 2, 3), core.Controllers.Output.Lightbar);
        core.SetOutputLayerNow(EmulationService.GameLayer, null);
        Assert.Equal(profileColor, core.Controllers.Output.Lightbar);
    }

    [Fact]
    public void Without_drivers_emulation_is_unavailable_and_restore_only_turns_it_off()
    {
        using var dir = new TempDir();
        using AppCore core = Create(dir, new ManualTime());
        Assert.Null(core.Emulation);

        Assert.False(core.RestoreEmulation());
        Assert.False(core.Settings.Current.Emulation.Enabled);
    }

    [Fact]
    public void Unclean_start_restores_and_queues_a_notice_for_the_first_listener()
    {
        using var dir = new TempDir();
        var rig = new EmulationRig();
        rig.Recovery.UncleanShutdown = true;
        using AppCore core = Create(dir, new ManualTime(), rig);
        var notices = new List<Notice>();
        core.Notice += notices.Add;

        Assert.Equal("restoreAll", rig.Log[0]);
        Assert.Contains(notices, n => n.Kind == NoticeKinds.Emulation);
    }

    [Fact]
    public void Restore_turns_the_switch_off_and_replays_the_journal()
    {
        using var dir = new TempDir();
        var rig = new EmulationRig();
        using AppCore core = Create(dir, new ManualTime(), rig);

        Assert.True(core.RestoreEmulation());
        Assert.False(core.Settings.Current.Emulation.Enabled);
        Assert.Contains("restoreAll", rig.Log);
        Assert.False(core.Emulation!.Status.Enabled);
    }

    [Fact]
    public void Dispose_marks_a_clean_exit()
    {
        using var dir = new TempDir();
        var rig = new EmulationRig();
        AppCore core = Create(dir, new ManualTime(), rig);
        core.Dispose();
        Assert.Equal("cleanExit", rig.Log[^1]);
        Assert.True(rig.Server.Disposed);
    }

    [Fact]
    public void Virtual_pads_are_skipped_by_the_scan_filter()
    {
        var tree = new FakeTree();
        tree.Virtual.Add("virtual");
        using var withFilter = new ControllerService(autoConnect: false, tree.IsVirtual);
        Assert.True(withFilter.IsVirtual("virtual"));
        Assert.False(withFilter.IsVirtual(EmulationRig.RealPath));

        using var throwing = new ControllerService(autoConnect: false, _ => throw new InvalidOperationException());
        Assert.False(throwing.IsVirtual("x"));
    }
}
