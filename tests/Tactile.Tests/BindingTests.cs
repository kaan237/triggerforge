using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class BindingTests
{
    private static MapperRig Rig(Binding cross) => new(Bind.Buttons(("cross", cross)));

    [Fact]
    public void Plain_held_key_follows_the_button()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A));

        rig.Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        rig.Run(500);
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Equal(["A up"], rig.Take());
    }

    [Fact]
    public void Chord_presses_in_order_and_releases_in_reverse()
    {
        using MapperRig rig = Rig(Bind.Key(Key.LeftCtrl, Key.C));

        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["LeftCtrl down", "C down", "C up", "LeftCtrl up"], rig.Take());
    }

    [Fact]
    public void Plain_mouse_button_is_held()
    {
        using MapperRig rig = Rig(new Binding { Press = ActionConfig.ForMouse(MouseButton.Right) });

        rig.Press(Buttons.Cross);
        Assert.Equal(["mouse Right down"], rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Equal(["mouse Right up"], rig.Take());
    }

    [Fact]
    public void Plain_one_shot_fires_on_button_down_only()
    {
        using MapperRig rig = Rig(new Binding { Press = Bind.Wheel(WheelDirection.Down, 2) });

        rig.Press(Buttons.Cross);
        Assert.Equal(["wheel -240 v"], rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Turbo_taps_at_the_rate_with_half_duty()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with { Turbo = true, TurboHz = 10 });

        rig.Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(49);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal(["A up"], rig.Take());
        rig.Advance(50);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(50);
        Assert.Equal(["A up"], rig.Take());
        rig.Advance(50);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(10).Release(Buttons.Cross);
        Assert.Equal(["A up"], rig.Take());
        rig.Run(300);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Turbo_one_shot_fires_once_per_period()
    {
        using MapperRig rig = Rig(new Binding { Press = Bind.Wheel(WheelDirection.Up), Turbo = true, TurboHz = 20 });

        rig.Press(Buttons.Cross).Run(200, step: 1).Release(Buttons.Cross);
        // 50 ms period: at 0, 50, 100, 150, 200.
        Assert.Equal(5, rig.Take().Count(e => e == "wheel 120 v"));
    }

    [Fact]
    public void Toggle_latches_held_actions_and_repeats_one_shots()
    {
        using (MapperRig rig = Rig(Bind.Key(Key.A) with { Toggle = true }))
        {
            rig.Press(Buttons.Cross).Release(Buttons.Cross);
            Assert.Equal(["A down"], rig.Take());
            rig.Run(100);
            rig.Press(Buttons.Cross);
            Assert.Equal(["A up"], rig.Take());
            rig.Release(Buttons.Cross).Press(Buttons.Cross).Release(Buttons.Cross);
            Assert.Equal(["A down"], rig.Take());
        }

        using (MapperRig rig = Rig(new Binding { Press = Bind.Wheel(WheelDirection.Up), Toggle = true }))
        {
            rig.Press(Buttons.Cross).Release(Buttons.Cross).Press(Buttons.Cross).Release(Buttons.Cross);
            Assert.Equal(["wheel 120 v", "wheel 120 v"], rig.Take());
        }
    }

    [Fact]
    public void Turbo_wins_over_toggle()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with { Turbo = true, Toggle = true, TurboHz = 10 });

        rig.Press(Buttons.Cross).Advance(50).Advance(50).Release(Buttons.Cross);
        Assert.Equal(["A down", "A up", "A down", "A up"], rig.Take());
        rig.Run(300);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Hold_fires_after_the_threshold_and_short_press_taps()
    {
        Binding b = Bind.Key(Key.A) with { Hold = Bind.Keys(Key.B), HoldMs = 400, Turbo = true };
        using MapperRig rig = Rig(b);

        // Long press: nothing at down, hold goes down at 400 ms and up at release.
        rig.Press(Buttons.Cross);
        Assert.Empty(rig.Take());
        rig.Advance(399);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal(["B down"], rig.Take());
        rig.Advance(500).Release(Buttons.Cross);
        Assert.Equal(["B up"], rig.Take());

        // Short press: press taps at once on release (no double set), turbo ignored.
        rig.Press(Buttons.Cross).Advance(100).Release(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(29);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal(["A up"], rig.Take());
    }

    [Fact]
    public void Hold_one_shot_fires_once()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with { Hold = Bind.Wheel(WheelDirection.Down), HoldMs = 150 });

        rig.Press(Buttons.Cross).Run(1000).Release(Buttons.Cross).Run(100);
        Assert.Equal(["wheel -120 v"], rig.Take());
    }

    [Fact]
    public void Double_press_fires_double_and_a_single_press_taps_after_the_window()
    {
        Binding b = Bind.Key(Key.A) with { Double = Bind.Keys(Key.B), DoubleMs = 250 };
        using MapperRig rig = Rig(b);

        // Two presses in time: double is held with the second press.
        rig.Press(Buttons.Cross).Advance(50).Release(Buttons.Cross).Advance(100).Press(Buttons.Cross);
        Assert.Equal(["B down"], rig.Take());
        rig.Advance(300);
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Equal(["B up"], rig.Take());

        // One press: press taps once the window closes.
        rig.Advance(1000).Press(Buttons.Cross).Advance(50).Release(Buttons.Cross);
        rig.Advance(250);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(30);
        Assert.Equal(["A up"], rig.Take());
    }

    [Fact]
    public void Hold_and_double_together()
    {
        Binding b = Bind.Key(Key.A) with { Hold = Bind.Keys(Key.H), Double = Bind.Keys(Key.D), HoldMs = 300, DoubleMs = 200 };
        using MapperRig rig = Rig(b);

        rig.Press(Buttons.Cross).Advance(300);
        Assert.Equal(["H down"], rig.Take());
        rig.Release(Buttons.Cross).Run(500);
        Assert.Equal(["H up"], rig.Take());

        rig.Press(Buttons.Cross).Advance(100).Release(Buttons.Cross).Advance(100).Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["D down", "D up"], rig.Take());
        rig.Run(500);
        Assert.Empty(rig.Take());

        rig.Press(Buttons.Cross).Advance(100).Release(Buttons.Cross).Run(300);
        Assert.Equal(["A down", "A up"], rig.Take());
    }

    [Fact]
    public void One_shot_double_fires_on_the_second_press()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with { Double = Bind.Wheel(WheelDirection.Up) });

        rig.Press(Buttons.Cross).Release(Buttons.Cross).Advance(50).Press(Buttons.Cross);
        Assert.Equal(["wheel 120 v"], rig.Take());
        rig.Release(Buttons.Cross).Run(500);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Triple_fires_and_holds_only_on_the_third_press()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with
        {
            Double = Bind.Keys(Key.B), Triple = Bind.Keys(Key.C), Turbo = true, Toggle = true,
        });

        rig.Press(Buttons.Cross).Release(Buttons.Cross).Advance(250)
            .Press(Buttons.Cross).Release(Buttons.Cross).Advance(250);
        Assert.Empty(rig.Take());
        rig.Press(Buttons.Cross);
        Assert.Equal(["C down"], rig.Take());
        rig.Release(Buttons.Cross).Advance(500);
        Assert.Equal(["C up"], rig.Take());
    }

    [Theory]
    [InlineData(true, "B")]
    [InlineData(false, "A")]
    public void Second_release_waits_and_taps_double_or_press_on_timeout(bool hasDouble, string key)
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with
        {
            Double = hasDouble ? Bind.Keys(Key.B) : null, Triple = Bind.Keys(Key.C),
        });

        rig.Press(Buttons.Cross).Release(Buttons.Cross).Press(Buttons.Cross).Release(Buttons.Cross).Advance(250);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal([$"{key} down"], rig.Take());
        rig.Advance(30);
        Assert.Equal([$"{key} up"], rig.Take());
    }

    [Fact]
    public void Second_press_can_fire_hold_and_cancel_the_triple_window()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A) with
        {
            Hold = Bind.Keys(Key.H), Double = Bind.Keys(Key.B), Triple = Bind.Keys(Key.C),
        });

        rig.Press(Buttons.Cross).Release(Buttons.Cross).Press(Buttons.Cross).Advance(399);
        Assert.Empty(rig.Take());
        rig.Advance(1);
        Assert.Equal(["H down"], rig.Take());
        rig.Release(Buttons.Cross).Advance(500);
        Assert.Equal(["H up"], rig.Take());
    }

    [Fact]
    public void Cycle_wraps_releases_and_resets_ignoring_other_press_modes()
    {
        using MapperRig rig = Rig(Bind.Key(Key.P) with
        {
            Cycle = [Bind.Keys(Key.A), Bind.Keys(Key.B), Bind.Keys(Key.C)],
            Hold = Bind.Keys(Key.H), Double = Bind.Keys(Key.D), Triple = Bind.Keys(Key.T),
            Turbo = true, Toggle = true,
        });

        foreach (string key in new[] { "A", "B", "C", "A" })
        {
            rig.Press(Buttons.Cross).Advance(500);
            Assert.Equal([$"{key} down"], rig.Take());
            rig.Release(Buttons.Cross);
            Assert.Equal([$"{key} up"], rig.Take());
        }

        rig.Press(Buttons.Cross);
        rig.Mapper.ReleaseAll();
        Assert.Equal(["B down", "B up"], rig.Take());
        rig.Release(Buttons.Cross).Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
    }

    [Fact]
    public void Cycle_positions_are_independent_across_layers_and_reset_together()
    {
        using var rig = new MapperRig(new MappingConfig
        {
            LayerButton = "ps",
            Buttons = Bind.Map(("cross", new Binding { Cycle = [Bind.Keys(Key.A), Bind.Keys(Key.B)] }),
                ("circle", new Binding { Cycle = [Bind.Keys(Key.C), Bind.Keys(Key.D)] })),
            Layer = Bind.Map(("cross", new Binding { Cycle = [Bind.Keys(Key.X), Bind.Keys(Key.Y)] })),
        });

        void Click(Buttons button, string key)
        {
            rig.Press(button).Release(button);
            Assert.Equal([$"{key} down", $"{key} up"], rig.Take());
        }

        Click(Buttons.Cross, "A");
        Click(Buttons.Circle, "C");
        rig.Press(Buttons.PS);
        Click(Buttons.Cross, "X");
        Click(Buttons.Circle, "D");
        rig.Release(Buttons.PS);
        Click(Buttons.Cross, "B");
        rig.Press(Buttons.PS);
        Click(Buttons.Cross, "Y");
        Click(Buttons.Cross, "X");
        rig.Mapper.ReleaseAll();
        Click(Buttons.Cross, "X");
    }

    [Fact]
    public void Pause_combo_report_suppresses_only_its_layer_pulse()
    {
        using var rig = new MapperRig(Bind.Buttons(("r1", Bind.Key(Key.A))) with { LayerButton = "ps" });
        rig.Mapper.OnInput(Inputs.Make(Buttons.PS | Buttons.R1), default, rig.Now, suppressLayerPulse: true);
        Assert.Equal(0, rig.ConfirmPulses);
        Assert.Equal(["A down"], rig.Take());

        rig.Mapper.OnInput(Inputs.Make(), default, rig.Now);
        Assert.Equal(1, rig.ConfirmPulses);
        Assert.Equal(["A up"], rig.Take());
    }

    [Fact]
    public void Unbound_buttons_do_nothing()
    {
        using MapperRig rig = Rig(Bind.Key(Key.A));

        rig.Press(Buttons.Circle).Release(Buttons.Circle).Run(100);
        Assert.Empty(rig.Take());
    }
}
