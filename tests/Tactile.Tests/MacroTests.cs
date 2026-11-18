using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class MacroTests
{
    private static MacroStep Down(Key k) => new() { Op = MacroOp.Down, Key = k };

    private static MacroStep Up(Key k) => new() { Op = MacroOp.Up, Key = k };

    private static MacroStep Tap(Key k) => new() { Op = MacroOp.Tap, Key = k };

    private static MacroStep Delay(int ms) => new() { Op = MacroOp.Delay, Ms = ms };

    private static MapperRig Rig(MacroRepeat repeat, params MacroStep[] steps)
    {
        var macro = new Macro { Id = Validation.NewId(), Name = "M", Repeat = repeat, Steps = steps };
        return new MapperRig(Bind.Buttons(("cross", new Binding { Press = Bind.Macro(macro.Id) })), [macro]);
    }

    [Fact]
    public void Once_runs_to_the_end_and_ignores_presses_while_running()
    {
        using MapperRig rig = Rig(MacroRepeat.Once, Down(Key.LeftShift), Tap(Key.A), Up(Key.LeftShift), Delay(100),
            new MacroStep { Op = MacroOp.Click, Button = MouseButton.Right },
            new MacroStep { Op = MacroOp.Wheel, Direction = WheelDirection.Down, Amount = 2 });

        rig.Press(Buttons.Cross);
        Assert.Equal(["LeftShift down", "A down"], rig.Take());
        rig.Release(Buttons.Cross).Advance(29);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal(["A up", "LeftShift up"], rig.Take());

        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Empty(rig.Take());

        rig.Advance(100);
        Assert.Equal(["mouse Right down"], rig.Take());
        rig.Advance(30);
        Assert.Equal(["mouse Right up", "wheel -240 v"], rig.Take());

        rig.Run(200).Press(Buttons.Cross);
        Assert.Equal(["LeftShift down", "A down"], rig.Take());
    }

    [Fact]
    public void While_held_loops_until_release_then_stops()
    {
        using MapperRig rig = Rig(MacroRepeat.WhileHeld, Tap(Key.A), Delay(50));

        rig.Press(Buttons.Cross).Run(200, step: 1);
        // Rounds of 80 ms: taps start at 0, 80, 160.
        Assert.Equal(3, rig.Take().Count(e => e == "A down"));
        rig.Advance(10).Release(Buttons.Cross);
        rig.Run(500);
        string[] after = rig.Take();
        Assert.DoesNotContain("A down", after);
    }

    [Fact]
    public void While_held_release_mid_tap_finishes_the_tap()
    {
        using MapperRig rig = Rig(MacroRepeat.WhileHeld, Tap(Key.A), Delay(50));

        rig.Press(Buttons.Cross).Advance(10).Release(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(20);
        Assert.Equal(["A up"], rig.Take());
        rig.Run(300);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Toggle_starts_on_one_press_and_stops_on_the_next()
    {
        using MapperRig rig = Rig(MacroRepeat.Toggle, Tap(Key.A), Delay(20));

        rig.Press(Buttons.Cross).Release(Buttons.Cross).Run(500);
        Assert.True(rig.Take().Count(e => e == "A down") >= 9);
        rig.Press(Buttons.Cross).Release(Buttons.Cross).Run(500);
        Assert.True(rig.Take().Count(e => e == "A down") <= 1);
        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
    }

    [Fact]
    public void Loop_without_delay_waits_10_ms_between_rounds()
    {
        using MapperRig rig = Rig(MacroRepeat.WhileHeld, Down(Key.A), Up(Key.A));

        rig.Press(Buttons.Cross).Run(100, step: 1).Release(Buttons.Cross);
        int rounds = rig.Take().Count(e => e == "A down");
        Assert.InRange(rounds, 10, 11);
    }

    [Fact]
    public void Stopping_releases_what_the_macro_holds()
    {
        using MapperRig rig = Rig(MacroRepeat.Toggle, Down(Key.LeftShift), new MacroStep { Op = MacroOp.MouseDown, Button = MouseButton.Left }, Delay(1000));

        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["LeftShift down", "mouse Left down"], rig.Take());
        rig.Advance(100);
        rig.Press(Buttons.Cross);
        Assert.Equal(["LeftShift up", "mouse Left up"], rig.Take().Order());

        rig.Release(Buttons.Cross).Press(Buttons.Cross).Release(Buttons.Cross);
        rig.Take();
        rig.Mapper.SetActive(false);
        Assert.Equal(["LeftShift up", "mouse Left up"], rig.Take().Order());
        rig.Mapper.SetActive(true);
        rig.Run(2000);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Macro_shares_key_counts_with_bindings()
    {
        var macro = new Macro { Id = Validation.NewId(), Name = "M", Repeat = MacroRepeat.Toggle, Steps = [Down(Key.LeftShift), Delay(1000)] };
        using var rig = new MapperRig(
            Bind.Buttons(("cross", new Binding { Press = Bind.Macro(macro.Id) }), ("circle", Bind.Key(Key.LeftShift))), [macro]);

        rig.Press(Buttons.Circle).Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["LeftShift down"], rig.Take());
        rig.Press(Buttons.Cross);
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Circle);
        Assert.Equal(["LeftShift up"], rig.Take());
    }

    [Fact]
    public void Unknown_macro_is_reported()
    {
        using var rig = new MapperRig(new MappingConfig());
        Profile p = rig.Profile with
        {
            Mapping = rig.Profile.Mapping with { Buttons = Bind.Map(("cross", new Binding { Press = Bind.Macro(Validation.NewId()) })) },
        };
        rig.Mapper.Configure(p);

        rig.Press(Buttons.Cross);
        Assert.Single(rig.Reports);
        Assert.Empty(rig.Take());
    }
}
