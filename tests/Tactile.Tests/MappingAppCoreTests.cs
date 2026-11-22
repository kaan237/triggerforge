using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class MappingAppCoreTests
{
    private static AppCore Core(TempDir dir, FakePlatform fake, FakeForeground? fg = null) =>
        new(dir.Path, fg ?? new FakeForeground(), new ControllerService(autoConnect: false), new ManualTime(), runTimer: false, fake.Platform);

    private static Profile Map(AppCore core, params (string Button, ActionConfig Action)[] items) =>
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = items.ToDictionary(i => i.Button, i => new Binding { Press = i.Action }),
            },
        });

    private static void Click(AppCore core, Buttons b)
    {
        core.OnInput(Inputs.Make(b));
        core.OnInput(Inputs.Make());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Controller_pause_toggles_once_per_combo_edge_and_keeps_normal_bindings(bool threeButtons)
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("r1", Bind.Keys(Key.B)), ("cross", Bind.Keys(Key.A)));
        core.Settings.Save(core.Settings.Current with
        {
            PauseCombo = threeButtons ? ["l1", "r1", "options"] : ["l1", "r1"],
        });
        Buttons partial = Buttons.L1 | (threeButtons ? Buttons.Options : Buttons.None);
        Buttons combo = partial | Buttons.R1;
        core.OnInput(Inputs.Make(Buttons.Cross | partial));
        Assert.False(core.UserMappingPaused);
        Assert.Equal(["A down"], fake.Injector.Take());

        core.OnInput(Inputs.Make(Buttons.Cross | combo));
        Assert.True(core.UserMappingPaused);
        Assert.Equal("shortcut", core.Mapping.PauseReason);
        Assert.Equal(["B down", "A up", "B up"], fake.Injector.Take());
        core.OnInput(Inputs.Make(combo));
        Assert.True(core.UserMappingPaused);
        Assert.Empty(fake.Injector.Take());

        core.OnInput(Inputs.Make(partial));
        core.OnInput(Inputs.Make(combo));
        Assert.False(core.UserMappingPaused);
        Assert.True(core.Mapping.Active);
        Assert.Equal(["B down"], fake.Injector.Take());
    }

    [Fact]
    public void Pause_sources_are_independent_and_ui_capture_has_priority()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var foreground = new FakeForeground();
        using AppCore core = Core(dir, fake, foreground);
        Map(core, ("cross", Bind.Keys(Key.A)));
        core.Settings.Save(core.Settings.Current with { PauseOnFullscreen = true });

        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());
        core.UiMappingPaused = true;
        Assert.Equal(["A up"], fake.Injector.Take());
        Assert.Equal("ui", core.Mapping.PauseReason);

        foreground.IsFullscreen = true;
        foreground.Set("browser.exe");
        int notifications = 0;
        core.MappingChanged += () => notifications++;
        core.ToggleUserPause();
        Assert.Equal(1, notifications);
        Assert.True(core.FullscreenPaused);
        Assert.True(core.UserMappingPaused);
        Assert.Equal("ui", core.Mapping.PauseReason);
        core.UiMappingPaused = false;
        Assert.Equal("shortcut", core.Mapping.PauseReason);
        core.ToggleUserPause();
        Assert.Equal("fullscreen", core.Mapping.PauseReason);
        Assert.False(core.Mapping.Active);

        foreground.IsFullscreen = false;
        core.Refresh();
        Assert.Null(core.Mapping.PauseReason);
        Assert.True(core.Mapping.Active);
        Assert.Empty(fake.Injector.Take());
    }

    [Fact]
    public void Fullscreen_immediately_releases_desktop_mapping_and_exempts_a_game_rule()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var foreground = new FakeForeground();
        using AppCore core = Core(dir, fake, foreground);
        Profile desktop = Map(core, ("cross", Bind.Keys(Key.A)));
        Profile game = core.Profiles.Save(desktop with { Id = "", Name = "Game", Games = ["game.exe"] });
        core.Settings.Save(core.Settings.Current with
        {
            DefaultProfileId = desktop.Id, AutoProfileDelayMs = 0, PauseOnFullscreen = true,
        });

        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());
        foreground.IsFullscreen = true;
        foreground.Set("browser.exe");
        Assert.Equal(["A up"], fake.Injector.Take());
        Assert.Equal("fullscreen", core.Mapping.PauseReason);

        core.OnInput(Inputs.Make());
        foreground.Set("game.exe");
        Assert.Equal(game.Id, core.ActiveProfile.Id);
        Assert.False(core.FullscreenPaused);
        Assert.Null(core.Mapping.PauseReason);
        Click(core, Buttons.Cross);
        Assert.Equal(["A down", "A up"], fake.Injector.Take());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Profile_layer_macro_and_user_pause_emit_confirmation_only_when_enabled(bool enabled)
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var time = new ManualTime();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            time, runTimer: false, fake.Platform);
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = core.ActiveProfile.Id });
        string macroId = Validation.NewId();
        Profile profile = core.Profiles.Save(new Profile
        {
            Name = "Confirmation",
            Haptics = new HapticsConfig { ConfirmPulses = enabled },
            Mapping = Bind.Buttons(("cross", new Binding { Press = Bind.Macro(macroId) })) with { Enabled = true },
            Macros = [new Macro { Id = macroId, Steps = [new MacroStep { Op = MacroOp.Tap, Key = Key.A }] }],
        });
        core.OnInput(Inputs.Make());

        void CheckPulse(Action trigger)
        {
            Assert.Equal(0, core.Controllers.Output.RumbleLeft);
            trigger();
            Assert.Equal(enabled ? 64 : 0, core.Controllers.Output.RumbleLeft);
            Assert.Equal(enabled ? 160 : 0, core.Controllers.Output.RumbleRight);
            time.Advance(TimeSpan.FromMilliseconds(59));
            core.Refresh();
            Assert.Equal(enabled ? 64 : 0, core.Controllers.Output.RumbleLeft);
            time.Advance(TimeSpan.FromMilliseconds(1));
            core.Refresh();
            Assert.Equal(0, core.Controllers.Output.RumbleLeft);
            Assert.Equal(0, core.Controllers.Output.RumbleRight);
        }

        CheckPulse(() => core.ActivateProfile(profile.Id));
        CheckPulse(() => core.OnInput(Inputs.Make(Buttons.PS)));
        CheckPulse(() => core.OnInput(Inputs.Make()));
        CheckPulse(() => Click(core, Buttons.Cross));
        Assert.Equal(["A down", "A up"], fake.Injector.Take());
        CheckPulse(core.ToggleUserPause);
    }

    [Fact]
    public void Fullscreen_exemption_tracks_the_live_exe_during_profile_selection_delay()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        var foreground = new FakeForeground();
        using var core = new AppCore(dir.Path, foreground, new ControllerService(autoConnect: false),
            time, runTimer: false, new FakePlatform().Platform);
        Profile desktop = Map(core, ("cross", Bind.Keys(Key.A)));
        Profile game = core.Profiles.Save(desktop with { Id = "", Name = "Game", Games = ["GAME.EXE"] });
        core.Settings.Save(core.Settings.Current with
        {
            DefaultProfileId = desktop.Id, AutoProfileDelayMs = 800, PauseOnFullscreen = true,
        });
        foreground.Set("desktop.exe");
        foreground.IsFullscreen = true;
        foreground.FullscreenExe = "game.exe";
        core.Refresh();
        Assert.Equal(desktop.Id, core.ActiveProfile.Id);
        Assert.False(core.FullscreenPaused);

        foreground.Set("game.exe");
        time.Advance(TimeSpan.FromMilliseconds(800));
        core.Refresh();
        Assert.Equal(game.Id, core.ActiveProfile.Id);

        foreground.FullscreenExe = "other.exe";
        core.Refresh();
        Assert.Equal(game.Id, core.ActiveProfile.Id);
        Assert.True(core.FullscreenPaused);
        foreground.Set("other.exe");
        Assert.Equal(game.Id, core.ActiveProfile.Id);
        Assert.True(core.FullscreenPaused);
        time.Advance(TimeSpan.FromMilliseconds(800));
        core.Refresh();
        Assert.Equal(desktop.Id, core.ActiveProfile.Id);
        Assert.True(core.FullscreenPaused);
    }

    [Fact]
    public void Pause_hotkey_matches_one_trigger_and_optional_modifiers_in_any_order()
    {
        Assert.True(PauseShortcut.TryGetHotkey([Key.P, Key.RightShift, Key.LeftCtrl], out uint modifiers, out Key key));
        Assert.Equal(6u, modifiers);
        Assert.Equal(Key.P, key);
        Assert.True(PauseShortcut.TryGetHotkey([Key.F10], out modifiers, out key));
        Assert.Equal(0u, modifiers);
        Assert.Equal(Key.F10, key);
        Assert.False(PauseShortcut.TryGetHotkey([Key.A, Key.B], out _, out _));
        Assert.False(PauseShortcut.TryGetHotkey([Key.LeftCtrl], out _, out _));
    }

    [Fact]
    public void Restore_keeps_the_active_id_or_falls_back_to_the_default()
    {
        using var dir = new TempDir();
        using AppCore core = Core(dir, new FakePlatform());
        Profile original = core.ActiveProfile;
        Profile selected = core.Profiles.Save(new Profile { Name = "Selected" });
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = original.Id });
        var day = new DateOnly(2025, 1, 1);
        ProfileBackup.EnsureDailyBackup(dir.Path, dir.Profiles, 7, day);
        core.ActivateProfile(selected.Id);
        core.RestoreProfileBackup("2025-01-01");
        Assert.Equal(selected.Id, core.ActiveProfile.Id);
        Assert.Equal(ProfileSelector.Manual, core.ActiveReason);

        Profile outsideSnapshot = core.Profiles.Save(new Profile { Name = "Extra" });
        core.ActivateProfile(outsideSnapshot.Id);
        core.RestoreProfileBackup("2025-01-01");
        Assert.Equal(original.Id, core.ActiveProfile.Id);
        Assert.Equal(ProfileSelector.Default, core.ActiveReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_uses_the_restored_game_rule_without_creating_a_manual_pin(bool manual)
    {
        using var dir = new TempDir();
        var foreground = new FakeForeground();
        using AppCore core = Core(dir, new FakePlatform(), foreground);
        Profile original = core.ActiveProfile;
        Profile game = core.Profiles.Save(new Profile { Name = "Game", Games = ["game.exe"] });
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = original.Id, AutoProfileDelayMs = 0 });
        ProfileBackup.EnsureDailyBackup(dir.Path, dir.Profiles, 7, new DateOnly(2025, 1, 1));
        core.Profiles.Save(game with { Games = [] });
        foreground.Set("game.exe");
        if (manual)
        {
            Profile extra = core.Profiles.Save(new Profile { Name = "Outside snapshot" });
            core.ActivateProfile(extra.Id);
        }

        core.RestoreProfileBackup("2025-01-01");
        Assert.Equal(game.Id, core.ActiveProfile.Id);
        Assert.Equal("game:game.exe", core.ActiveReason);
    }

    [Fact]
    public void Profile_action_switches_next_and_previous()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        var next = new ActionConfig { Kind = ActionKind.Profile, ProfileId = "next" };
        var previous = new ActionConfig { Kind = ActionKind.Profile, ProfileId = "previous" };
        // Names sort as "A first", "B second".
        Profile a = Map(core, ("cross", next), ("circle", previous)) ;
        a = core.Profiles.Save(a with { Name = "A first" });
        Profile b = core.Profiles.Save(a with { Id = "", Name = "B second" });

        Click(core, Buttons.Cross);
        Assert.Equal(b.Id, core.ActiveProfile.Id);
        Click(core, Buttons.Cross);
        Assert.Equal(a.Id, core.ActiveProfile.Id);
        Click(core, Buttons.Circle);
        Assert.Equal(b.Id, core.ActiveProfile.Id);

        Map(core, ("cross", new ActionConfig { Kind = ActionKind.Profile, ProfileId = Validation.NewId() }));
        var notices = new List<Notice>();
        core.Notice += notices.Add;
        Click(core, Buttons.Cross);
        Assert.Contains(notices, n => n.Kind == NoticeKinds.Mapping);
    }

    [Fact]
    public void Mic_mute_toggles_and_drives_the_mic_led()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("mic", new ActionConfig { Kind = ActionKind.MicMute }));

        Click(core, Buttons.Mic);
        Assert.Equal(true, fake.Microphone.IsMuted);
        Assert.Equal(MicLed.On, core.Controllers.Output.MicLed);
        Click(core, Buttons.Mic);
        Assert.Equal(MicLed.Off, core.Controllers.Output.MicLed);

        // Unmuted gives the LED back to the profile, not a forced "off".
        core.Profiles.Save(core.ActiveProfile with { MicLed = MicLed.Pulse });
        core.Refresh();
        Assert.Equal(MicLed.Pulse, core.Controllers.Output.MicLed);

        fake.Microphone.Present = false;
        var notices = new List<Notice>();
        core.Notice += notices.Add;
        Click(core, Buttons.Mic);
        Assert.Single(notices, n => n.Kind == NoticeKinds.Mapping);
    }

    [Fact]
    public void Show_app_special_and_win_chords()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core,
            ("options", new ActionConfig { Kind = ActionKind.Special, Special = SpecialAction.ShowApp }),
            ("create", new ActionConfig { Kind = ActionKind.Special, Special = SpecialAction.GameBar }));
        int shown = 0;
        core.ShowAppRequested += () => shown++;

        Click(core, Buttons.Options);
        Assert.Equal(1, shown);

        Click(core, Buttons.Create);
        Assert.Equal(["LeftWin down", "G down"], fake.Injector.Take());
        core.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(["G up", "LeftWin up"], fake.Injector.Take());
    }

    [Fact]
    public void Run_starts_the_program_and_reports_failures()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, fake);
        Map(core, ("triangle", new ActionConfig { Kind = ActionKind.Run, Path = @"C:\Tools\obs.exe", Args = "--minimize" }));
        var notices = new List<Notice>();
        core.Notice += notices.Add;

        Click(core, Buttons.Triangle);
        Assert.Equal([(@"C:\Tools\obs.exe", "--minimize")], fake.Launcher.Runs);

        fake.Launcher.Fail = true;
        Click(core, Buttons.Triangle);
        Assert.Single(notices, n => n.Kind == NoticeKinds.Mapping);
    }

    [Fact]
    public void Elevated_foreground_blocks_once_per_exe()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var fg = new FakeForeground();
        using AppCore core = Core(dir, fake, fg);
        var notices = new List<Notice>();
        core.Notice += notices.Add;
        Map(core, ("cross", Bind.Keys(Key.A)));

        // Inactive mapping: not asked.
        fake.Elevation.Blocks = true;
        fg.Set("admin.exe");
        Assert.Null(core.Mapping.BlockedBy);

        core.OnInput(Inputs.Make());
        fg.Set("other.exe");
        fg.Set("admin.exe");
        Assert.Equal("admin.exe", core.Mapping.BlockedBy);
        fake.Elevation.Blocks = false;
        fg.Set("explorer.exe");
        Assert.Null(core.Mapping.BlockedBy);
        fake.Elevation.Blocks = true;
        fg.Set("admin.exe");
        Assert.Equal("admin.exe", core.Mapping.BlockedBy);

        Assert.Equal(2, notices.Count(n => n.Kind == NoticeKinds.Elevated));
        Assert.Single(notices, n => n.Kind == NoticeKinds.Elevated && n.Message.Contains("admin.exe"));
    }

    [Fact]
    public void Elevated_app_already_in_front_is_found_when_mapping_turns_on()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var fg = new FakeForeground();
        using AppCore core = Core(dir, fake, fg);
        fake.Elevation.Blocks = true;
        fg.Set("admin.exe");
        core.OnInput(Inputs.Make());
        Assert.Null(core.Mapping.BlockedBy);

        // The game's profile arrives after the foreground change (auto-profile delay).
        Map(core, ("cross", Bind.Keys(Key.A)));
        core.Refresh();
        Assert.Equal("admin.exe", core.Mapping.BlockedBy);
    }

    [Fact]
    public void Default_platform_and_disabled_mapping_send_nothing()
    {
        using var dir = new TempDir();
        using AppCore core = new(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), new ManualTime(), runTimer: false);
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.False(core.Mapping.Active);
        Assert.False(core.ActiveProfile.Mapping.Enabled);
    }
}
