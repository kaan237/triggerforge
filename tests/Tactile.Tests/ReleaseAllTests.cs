using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

/// <summary>Rule 8 on the mapper itself, then through <see cref="AppCore"/>.</summary>
public class ReleaseAllTests
{
    private static MappingConfig Everything() => new()
    {
        Enabled = true,
        Buttons = Bind.Map(
            ("cross", Bind.Key(Key.A)),
            ("circle", Bind.Key(Key.B) with { Toggle = true }),
            ("square", Bind.Key(Key.C) with { Turbo = true })),
    };

    [Fact]
    public void Deactivating_releases_held_toggled_and_turbo_keys()
    {
        using var rig = new MapperRig(Everything());
        rig.Press(Buttons.Cross | Buttons.Circle | Buttons.Square);
        rig.Release(Buttons.Circle);
        // Same report: handled in button order (square, cross, circle).
        Assert.Equal(["C down", "A down", "B down"], rig.Take());

        rig.Mapper.SetActive(false);
        Assert.Equal(["A up", "B up", "C up"], rig.Take().Order());

        // Still held, but nothing fires until pressed again.
        rig.Mapper.SetActive(true);
        rig.Run(300).Send();
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Cross).Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
    }

    [Fact]
    public void Another_profile_or_changed_mapping_releases_but_unrelated_saves_do_not()
    {
        using var rig = new MapperRig(Everything());
        rig.Press(Buttons.Cross);
        rig.Take();

        rig.Mapper.Configure(rig.Profile with { Lightbar = new LightbarConfig { Color = "#FF0000" } });
        Assert.Empty(rig.Take());

        Profile changed = rig.Profile with { Mapping = rig.Profile.Mapping with { LayerButton = "none" } };
        rig.Mapper.Configure(changed);
        Assert.Equal(["A up"], rig.Take());

        rig.Release(Buttons.Cross).Press(Buttons.Cross);
        rig.Take();
        rig.Mapper.Configure(changed with { Id = Validation.NewId() });
        Assert.Equal(["A up"], rig.Take());
    }

    [Fact]
    public void Dispose_releases()
    {
        var rig = new MapperRig(Everything());
        rig.Press(Buttons.Cross);
        rig.Take();
        rig.Dispose();
        Assert.Equal(["A up"], rig.Take());
    }

    private static AppCore Core(TempDir dir, ManualTime time, FakePlatform fake, FakeForeground? fg = null) =>
        new(dir.Path, fg ?? new FakeForeground(), new ControllerService(autoConnect: false), time, runTimer: false, fake.Platform);

    [Fact]
    public void AppCore_releases_on_pause_disable_profile_switch_and_disconnect()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, time, fake);
        Profile mapped = core.Profiles.Save(core.ActiveProfile with { Mapping = Everything() });
        Profile other = core.Profiles.Save(new Profile { Name = "Other", Mapping = Everything() });
        core.ActivateProfile(mapped.Id);
        int changes = 0;
        core.MappingChanged += () => changes++;

        Assert.False(core.Mapping.Active);
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.True(core.Mapping.Active);
        Assert.Equal(["A down"], fake.Injector.Take());

        core.MappingPaused = true;
        Assert.Equal(["A up"], fake.Injector.Take());
        Assert.Equal(new MappingStatus(false, true, null), core.Mapping);
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Empty(fake.Injector.Take());
        core.MappingPaused = false;
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());

        core.ActivateProfile(other.Id);
        Assert.Equal(["A up"], fake.Injector.Take());
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());

        core.Profiles.Save(core.ActiveProfile with { Mapping = core.ActiveProfile.Mapping with { Enabled = false } });
        Assert.Equal(["A up"], fake.Injector.Take());
        Assert.False(core.Mapping.Active);

        core.Profiles.Save(core.ActiveProfile with { Mapping = core.ActiveProfile.Mapping with { Enabled = true } });
        core.OnInput(Inputs.Make());
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());
        core.OnConnection(null);
        Assert.Equal(["A up"], fake.Injector.Take());
        Assert.False(core.Mapping.Active);
        Assert.True(changes >= 6);
    }

    [Fact]
    public void AppCore_saving_a_different_mapping_releases()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        var fake = new FakePlatform();
        using AppCore core = Core(dir, time, fake);
        core.Profiles.Save(core.ActiveProfile with { Mapping = Everything() });
        core.OnInput(Inputs.Make(Buttons.Cross));
        fake.Injector.Take();

        core.Profiles.Save(core.ActiveProfile with { Name = "Renamed" });
        Assert.Empty(fake.Injector.Take());

        core.Profiles.Save(core.ActiveProfile with { Macros = [new Macro { Name = "M" }] });
        Assert.Equal(["A up"], fake.Injector.Take());
    }

    [Fact]
    public void AppCore_dispose_releases()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        AppCore core = Core(dir, new ManualTime(), fake);
        core.Profiles.Save(core.ActiveProfile with { Mapping = Everything() });
        core.OnInput(Inputs.Make(Buttons.Cross));
        fake.Injector.Take();
        core.Dispose();
        Assert.Equal(["A up"], fake.Injector.Take());
    }
}
