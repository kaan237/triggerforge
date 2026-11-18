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
