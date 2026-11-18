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
    public void Releasing_the_layer_first_still_releases_the_layer_key()
    {
        using var rig = new MapperRig(Layered());

        rig.Press(Buttons.PS).Press(Buttons.Cross).Release(Buttons.PS);
        Assert.Equal(["B down"], rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Equal(["B up"], rig.Take());
    }

    [Fact]
    public void Pressing_the_layer_mid_press_keeps_the_base_release()
    {
        using var rig = new MapperRig(Layered());

        rig.Press(Buttons.Cross).Press(Buttons.PS).Release(Buttons.Cross).Release(Buttons.PS);
        Assert.Equal(["A down", "A up"], rig.Take());
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
