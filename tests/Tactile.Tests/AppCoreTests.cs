using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class AppCoreTests
{
    private static AppCore Create(TempDir dir, FakeForeground fg, ManualTime time) =>
        new(dir.Path, fg, new ControllerService(autoConnect: false), time, runTimer: false);

    [Fact]
    public void Works_without_a_controller_and_sends_the_profile()
    {
        using var dir = new TempDir();
        var fg = new FakeForeground();
        var time = new ManualTime();
        using AppCore core = Create(dir, fg, time);

        Profile p = core.Profiles.Save(core.ActiveProfile with
        {
            Lightbar = new LightbarConfig { Effect = LightbarEffect.Static, Color = "#112233", Brightness = LedBrightness.Medium },
            PlayerLeds = new PlayerLedConfig { Mode = PlayerLedMode.Number, Number = 2 },
            MicLed = MicLed.On,
            Triggers = new TriggerPair
            {
                Right = new TriggerConfig { Mode = TriggerMode.Weapon, Params = new Dictionary<string, double> { ["start"] = 3, ["end"] = 6, ["strength"] = 8 } },
            },
        });
        core.Tick(time.GetElapsedTime(0));

        OutputState o = core.Controllers.Output;
        Assert.Equal(p.Id, core.ActiveProfile.Id);
        Assert.Equal(new Rgb(0x11, 0x22, 0x33), o.Lightbar);
        Assert.Equal(LedBrightness.Medium, o.Brightness);
        Assert.Equal(0b01010, o.PlayerLeds);
        Assert.Equal(MicLed.On, o.MicLed);
        Assert.Equal(TriggerEffect.Weapon(3, 6, 8), o.RightTrigger);
        Assert.Equal(TriggerEffect.Off, o.LeftTrigger);
        Assert.Equal(0, o.RumbleLeft);
    }

    [Fact]
    public void Switches_profile_with_the_foreground_after_the_delay()
    {
        using var dir = new TempDir();
        var fg = new FakeForeground();
        fg.Set("explorer.exe");
        var time = new ManualTime();
        using AppCore core = Create(dir, fg, time);
        Profile game = core.Profiles.Save(new Profile { Name = "Game", Games = ["game.exe"] });
        int changes = 0;
        core.ActiveProfileChanged += () => changes++;

        fg.Set("game.exe");
        Assert.Equal("default", core.ActiveReason);

        time.Advance(TimeSpan.FromMilliseconds(900));
        core.Refresh();
        Assert.Equal(game.Id, core.ActiveProfile.Id);
        Assert.Equal("game:game.exe", core.ActiveReason);
        Assert.Equal(1, changes);
        Assert.Equal(["game.exe", "explorer.exe"], core.RecentForeground);

        // Manual choice holds while the game stays in front.
        string other = core.Profiles.All.First(p => p.Id != game.Id).Id;
        core.ActivateProfile(other);
        Assert.Equal("manual", core.ActiveReason);
        time.Advance(TimeSpan.FromSeconds(10));
        core.Refresh();
        Assert.Equal(other, core.ActiveProfile.Id);

        Assert.Throws<KeyNotFoundException>(() => core.ActivateProfile("missing"));
    }

    [Fact]
    public void Animated_effect_sends_only_on_change_and_overrides_layer_on_top()
    {
        using var dir = new TempDir();
        var fg = new FakeForeground();
        var time = new ManualTime();
        using AppCore core = Create(dir, fg, time);
        core.Profiles.Save(core.ActiveProfile with
        {
            Lightbar = new LightbarConfig { Effect = LightbarEffect.Strobe, Color = "#FFFFFF", Color2 = "#000000" },
        });

        core.Tick(TimeSpan.FromSeconds(0.1));
        Assert.Equal(new Rgb(255, 255, 255), core.Controllers.Output.Lightbar);
        core.Tick(TimeSpan.FromSeconds(0.3));
        Assert.Equal(new Rgb(0, 0, 0), core.Controllers.Output.Lightbar);

        core.SetOverride("lightbar", o => o with { Lightbar = new Rgb(1, 2, 3), RumbleLeft = 99 });
        Assert.Equal(new Rgb(1, 2, 3), core.Controllers.Output.Lightbar);
        Assert.Equal(99, core.Controllers.Output.RumbleLeft);

        var weapon = new TriggerConfig { Mode = TriggerMode.Weapon, Params = new Dictionary<string, double> { ["start"] = 2, ["end"] = 5, ["strength"] = 4 } };
        core.PreviewTrigger(left: true, weapon);
        Assert.Equal(TriggerEffect.Weapon(2, 5, 4), core.Controllers.Output.LeftTrigger);
        Assert.Throws<ArgumentOutOfRangeException>(() => core.PreviewTrigger(true, weapon with { Params = new Dictionary<string, double> { ["start"] = 0, ["end"] = 5, ["strength"] = 4 } }));

        core.PreviewTrigger(left: true, null);
        Assert.Equal(TriggerEffect.Off, core.Controllers.Output.LeftTrigger);

        core.ClearOverrides();
        Assert.Equal(0, core.Controllers.Output.RumbleLeft);
    }

    [Fact]
    public void Broken_files_are_noticed_on_subscribe()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Profiles);
        File.WriteAllText(Path.Combine(dir.Profiles, new string('e', 32) + ".json"), "{");
        using AppCore core = Create(dir, new FakeForeground(), new ManualTime());

        var notices = new List<Notice>();
        core.Notice += notices.Add;

        Notice n = Assert.Single(notices);
        Assert.Equal(NoticeKinds.BrokenFile, n.Kind);
    }

    [Fact]
    public void Low_battery_and_gyro_calibration_notices()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        using AppCore core = Create(dir, new FakeForeground(), time);
        var notices = new List<Notice>();
        core.Notice += notices.Add;

        core.OnInput(Inputs.Make(battery: 50));
        core.OnInput(Inputs.Make(battery: 20));
        core.OnInput(Inputs.Make(battery: 20));
        Assert.Single(notices, n => n.Kind == NoticeKinds.LowBattery);

        core.StartGyroCalibration();
        Assert.True(core.GyroCalibrating);
        // Identity calibration: 1024 counts per deg/s.
        for (int i = 0; i < 1000 && core.GyroCalibrating; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(4));
            core.OnInput(Inputs.Make(gyro: new Vector3(512, -1024, 0), battery: 20));
        }

        Assert.False(core.GyroCalibrating);
        Assert.Equal(0.5f, core.GyroBias.X, 0.001f);
        Assert.Equal(-1f, core.GyroBias.Y, 0.001f);
        Notice last = notices.Last();
        Assert.Equal(NoticeKinds.GyroCalibration, last.Kind);
        Assert.Equal(1, last.Progress);
    }

    [Fact]
    public void Gyro_calibration_times_out_without_input()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        using AppCore core = Create(dir, new FakeForeground(), time);
        var notices = new List<Notice>();
        core.Notice += notices.Add;

        core.StartGyroCalibration();
        time.Advance(TimeSpan.FromSeconds(5));
        core.Refresh();

        Assert.False(core.GyroCalibrating);
        Assert.Null(notices.Last().Progress);
    }

    [Fact]
    public void Stick_processors_follow_the_active_profile()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        using AppCore core = Create(dir, new FakeForeground(), time);
        core.Profiles.Save(core.ActiveProfile with
        {
            Sticks = new StickPair { Left = new StickConfig { InvertX = true, InnerDeadzone = 0, OuterDeadzone = 0 } },
        });

        Assert.True(core.LeftStick.Config.InvertX);
        Assert.Equal(-1f, core.LeftStick.Process(1, 0).X, 1e-4f);
        Assert.False(core.RightStick.Config.InvertX);
    }
}
