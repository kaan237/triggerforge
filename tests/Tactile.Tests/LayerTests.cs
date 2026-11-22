using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class LayerTests
{
    private static MappingConfig Layered() => new()
    {
        LayerButton = "ps",
        Buttons = Bind.Map(("cross", Bind.Key(Key.A)), ("circle", Bind.Key(Key.C)), ("ps", Bind.Key(Key.Z))),
        Layer = Bind.Map(("cross", Bind.Key(Key.B))),
    };

    [Fact]
    public void Layer_binding_is_used_while_the_layer_button_is_held()
    {
        using var rig = new MapperRig(Layered());

        rig.Press(Buttons.PS);
        Assert.Empty(rig.Take());
        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["B down", "B up"], rig.Take());
        rig.Release(Buttons.PS).Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["A down", "A up"], rig.Take());
    }

    [Fact]
    public void Missing_layer_binding_falls_back_to_the_base()
    {
        using var rig = new MapperRig(Layered());

        rig.Press(Buttons.PS).Press(Buttons.Circle).Release(Buttons.Circle).Release(Buttons.PS);
        Assert.Equal(["C down", "C up"], rig.Take());
    }

    [Fact]
    public void Releasing_the_layer_first_releases_the_old_key_immediately()
    {
        using var rig = new MapperRig(Layered());

        rig.Press(Buttons.PS).Press(Buttons.Cross).Release(Buttons.PS);
        Assert.Equal(["B down", "B up"], rig.Take());
        rig.Send();
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Empty(rig.Take());
        rig.Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
    }

    [Fact]
    public void Pressing_the_layer_mid_press_releases_the_base_key_immediately()
    {
        using var rig = new MapperRig(Layered());

        rig.Press(Buttons.Cross).Press(Buttons.PS);
        Assert.Equal(["A down", "A up"], rig.Take());
        rig.Send().Release(Buttons.Cross);
        Assert.Empty(rig.Take());
        rig.Press(Buttons.Cross);
        Assert.Equal(["B down"], rig.Take());
    }

    [Fact]
    public void Four_layers_inherit_from_the_highest_active_binding()
    {
        using var rig = new MapperRig(Bind.Buttons(("cross", Bind.Key(Key.A)), ("circle", Bind.Key(Key.B)),
            ("square", Bind.Key(Key.C)), ("triangle", Bind.Key(Key.D))) with
        {
            Layers =
            [
                new() { Button = "fn1", Buttons = Bind.Map(("cross", Bind.Key(Key.E)), ("square", Bind.Key(Key.F))) },
                new() { Button = "fn2", Mode = LayerMode.Toggle, Buttons = Bind.Map(("cross", Bind.Key(Key.G)), ("circle", Bind.Key(Key.H))) },
                new() { Button = "paddleLeft", Buttons = Bind.Map(("cross", Bind.Key(Key.I))) },
                new() { Button = "paddleRight", Mode = LayerMode.Toggle, Buttons = Bind.Map(("cross", Bind.Key(Key.J))) },
            ],
        });

        rig.Press(Buttons.Fn1 | Buttons.Fn2 | Buttons.PaddleLeft | Buttons.PaddleRight);
        rig.Release(Buttons.Fn2 | Buttons.PaddleRight);
        rig.Press(Buttons.Cross).Release(Buttons.Cross);
        Assert.Equal(["J down", "J up"], rig.Take());
        rig.Press(Buttons.PaddleRight).Release(Buttons.PaddleRight);
        rig.Press(Buttons.Cross).Release(Buttons.Cross).Press(Buttons.Circle).Release(Buttons.Circle);
        rig.Press(Buttons.Square).Release(Buttons.Square).Press(Buttons.Triangle).Release(Buttons.Triangle);
        Assert.Equal(["I down", "I up", "H down", "H up", "F down", "F up", "D down", "D up"], rig.Take());
    }

    [Fact]
    public void Toggle_handover_releases_held_buttons_and_release_all_clears_the_toggle()
    {
        using var rig = new MapperRig(Layered() with
        {
            Layers = [new() { Button = "l1", Mode = LayerMode.Toggle, Buttons = Layered().Layer }],
        });
        rig.Press(Buttons.Cross).Press(Buttons.L1).Release(Buttons.L1);
        Assert.Equal(["A down", "A up"], rig.Take());
        rig.Release(Buttons.Cross).Press(Buttons.Cross).Press(Buttons.L1).Release(Buttons.L1);
        Assert.Equal(["B down", "B up"], rig.Take());
        rig.Release(Buttons.Cross).Press(Buttons.L1).Release(Buttons.L1);
        rig.Mapper.ReleaseAll();
        rig.Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        Assert.Equal(0, rig.ConfirmPulses);
    }

    [Fact]
    public void Activator_ignores_its_bindings_and_chords_and_only_pulses_on_toggle()
    {
        using var rig = new MapperRig(Layered() with
        {
            Layers = [new() { Button = "ps", Mode = LayerMode.Toggle, Buttons = Bind.Map(("ps", Bind.Key(Key.Y))) }],
            Chords = [new() { Buttons = ["ps", "cross"], Action = Bind.Keys(Key.X) }],
        }, confirmPulses: true);
        rig.Press(Buttons.PS).Release(Buttons.PS).Press(Buttons.Cross);
        Assert.Equal(["A down"], rig.Take());
        Assert.Equal(1, rig.ConfirmPulses);
    }

    [Fact]
    public void Layer_button_none_gives_it_its_own_binding()
    {
        using var rig = new MapperRig(Layered() with { LayerButton = "none" });

        rig.Press(Buttons.PS).Press(Buttons.Cross).Release(Buttons.Cross).Release(Buttons.PS);
        Assert.Equal(["Z down", "A down", "A up", "Z up"], rig.Take());
    }

    [Fact]
    public void Same_key_from_two_buttons_goes_up_with_the_last()
    {
        using var rig = new MapperRig(Bind.Buttons(("cross", Bind.Key(Key.LeftShift)), ("circle", Bind.Key(Key.LeftShift, Key.A))));

        rig.Press(Buttons.Cross).Press(Buttons.Circle);
        Assert.Equal(["LeftShift down", "A down"], rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Circle);
        Assert.Equal(["A up", "LeftShift up"], rig.Take());
    }

    [Fact]
    public void Mouse_buttons_are_reference_counted_too()
    {
        Binding left = new() { Press = ActionConfig.ForMouse(MouseButton.Left) };
        using var rig = new MapperRig(Bind.Buttons(("r2", left), ("r1", left)));

        rig.Press(Buttons.R2).Press(Buttons.R1).Release(Buttons.R2);
        Assert.Equal(["mouse Left down"], rig.Take());
        rig.Release(Buttons.R1);
        Assert.Equal(["mouse Left up"], rig.Take());
    }
}
