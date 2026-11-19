using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Multi;

[Collection("Localization")]
public sealed class EdgeShortcutsTests
{
    [Theory]
    [InlineData(Buttons.Fn1, Buttons.R1, (int)EdgeShortcut.Next)]
    [InlineData(Buttons.Fn1, Buttons.L1, (int)EdgeShortcut.Previous)]
    [InlineData(Buttons.Fn2, Buttons.Triangle, (int)EdgeShortcut.Profile1)]
    [InlineData(Buttons.Fn2, Buttons.Circle, (int)EdgeShortcut.Profile2)]
    [InlineData(Buttons.Fn2, Buttons.Cross, (int)EdgeShortcut.Profile3)]
    [InlineData(Buttons.Fn2, Buttons.Square, (int)EdgeShortcut.Profile4)]
    public void Shortcut_fires_only_when_the_action_button_is_pressed_with_fn(Buttons fn, Buttons action, int expected)
    {
        Assert.Equal((EdgeShortcut)expected, EdgeShortcuts.Read(ControllerModel.DualSenseEdge, fn, fn | action));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSenseEdge, fn | action, fn | action));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSenseEdge, action, fn | action));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSenseEdge, Buttons.None, fn | action));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSenseEdge, fn | action, fn));
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualSense, fn, fn | action));
    }

    [Fact]
    public void Promotion_seeds_shortcuts_but_still_maps_buttons_on_the_first_report()
    {
        using var dir = new TempDir();
        using var pads = new FakePads();
        var platform = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, platform.Platform);
        Profile first = core.Profiles.Save(core.ActiveProfile with
        {
            Name = "A First",
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = new Dictionary<string, Binding> { ["r1"] = new() { Press = ActionConfig.ForKeys(Tactile.Input.Key.A) } },
            },
        });
        Profile second = core.Profiles.Save(new Profile { Name = "B Second" });
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = first.Id });
        pads.Service.ScanIfIdle();
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(second.Id, core.ProfileForController(pads.Service.Slots[1]).Id);

        core.SetPrimaryController(FakePads.Mac(2));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(first.Id, core.ActiveProfile.Id);
        Assert.Equal(["A down"], platform.Injector.Take());

        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(second.Id, core.ActiveProfile.Id);
    }

    [Theory]
    [InlineData(Language.En, "Profile")]
    [InlineData(Language.Tr, "Profil")]
    public void Secondary_choice_is_session_only_and_primary_choice_pins_until_foreground_changes(Language language, string label)
    {
        using var dir = new TempDir();
        using var pads = new FakePads();
        var foreground = new FakeForeground();
        foreground.Set("desktop.exe");
        using var core = new AppCore(dir.Path, foreground, pads.Service, new ManualTime(), runTimer: false);
        Profile first = core.Profiles.Save(core.ActiveProfile with { Name = "A First" });
        Profile second = core.Profiles.Save(new Profile { Name = "B Second" });
        core.Settings.Save(core.Settings.Current with { AutoProfileDelayMs = 0, Language = language });
        pads.Service.ScanIfIdle();
        var notices = new List<Notice>();
        core.Notice += notices.Add;
        var input = new List<Buttons>();
        core.SecondaryControllers!.Input += (_, report) => input.Add(report.Buttons);

        pads[2].PublishInput(Inputs.Make(Buttons.Fn2));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn2 | Buttons.Circle));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn2 | Buttons.Circle));
        Assert.Equal(first.Id, core.ActiveProfile.Id);
        Assert.Equal(second.Id, core.ProfileForController(pads.Service.Slots[1]).Id);
        Assert.Equal(Buttons.Fn2 | Buttons.Circle, input[1]);
        Notice secondary = Assert.Single(notices);
        Assert.Equal(2, secondary.Controller);
        Assert.Equal($"{label}: {second.Name}", secondary.Message);
        Assert.Empty(core.Settings.Current.ControllerDefaults);

        core.SetPrimaryController(FakePads.Mac(2));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(second.Id, core.ActiveProfile.Id);
        Assert.Equal(ProfileSelector.Manual, core.ActiveReason);
        Assert.Equal(1, notices[^1].Controller);

        foreground.Set("other.exe");
        Assert.Equal(first.Id, core.ActiveProfile.Id);
        core.Settings.Save(core.Settings.Current with { EdgeShortcuts = false });
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1));
        pads[2].PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(first.Id, core.ActiveProfile.Id);
        Assert.Equal(2, notices.Count);
    }
}
