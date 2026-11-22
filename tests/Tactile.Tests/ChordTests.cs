using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class ChordTests
{
    private static ButtonChord Chord(Key key, params string[] buttons) => new() { Buttons = buttons, Action = Bind.Keys(key) };

    private static MappingConfig Mapping(params ButtonChord[] chords) =>
        Bind.Buttons(("l1", Bind.Key(Key.A)), ("cross", Bind.Key(Key.B)), ("circle", Bind.Key(Key.C))) with { Chords = chords };

    [Fact]
    public void Unrelated_buttons_fire_without_waiting()
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "l1", "cross")));
        rig.Press(Buttons.L1).Press(Buttons.Circle);
        Assert.Equal(["C down"], rig.Take());
        Assert.Equal(TimeSpan.Zero, rig.Now);
    }

    [Fact]
    public void Chord_suppresses_components_and_releases_on_the_first_up()
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "l1", "cross"), Chord(Key.F6, "cross", "circle")));
        rig.Press(Buttons.L1).Advance(20).Press(Buttons.Cross);
        Assert.Equal(["F5 down"], rig.Take());
        rig.Release(Buttons.L1).Press(Buttons.Circle).Advance(100);
        Assert.Equal(["F5 up", "C down"], rig.Take());
        rig.Release(Buttons.Circle | Buttons.Cross).Press(Buttons.Circle | Buttons.Cross);
        Assert.Equal(["C up", "F6 down"], rig.Take());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Solo_component_flushes_on_timeout_or_release(bool release)
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "l1", "cross")));
        rig.Press(Buttons.L1);
        Assert.Empty(rig.Take());
        if (release)
        {
            rig.Advance(15).Release(Buttons.L1);
            Assert.Equal(["A down", "A up"], rig.Take());
        }
        else
        {
            rig.Advance(51);
            Assert.Equal(["A down"], rig.Take());
        }

        rig.Press(Buttons.Cross).Advance(50);
        Assert.Equal(["B down"], rig.Take());
    }

    [Fact]
    public void A_buffered_button_keeps_its_hold_and_double_press_timing()
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "l1", "cross")) with
        {
            Buttons = Bind.Map(("l1", new Binding
            {
                Press = Bind.Keys(Key.A), Hold = Bind.Keys(Key.B), Double = Bind.Keys(Key.C), HoldMs = 150, DoubleMs = 100,
            })),
        });
        rig.Press(Buttons.L1).Advance(149);
        Assert.Empty(rig.Take());
        rig.Advance(1).Release(Buttons.L1);
        Assert.Equal(["B down", "B up"], rig.Take());
        rig.Press(Buttons.L1).Advance(5).Release(Buttons.L1).Advance(90).Press(Buttons.L1).Advance(50);
        Assert.Equal(["C down"], rig.Take());
    }

    [Theory]
    [InlineData("third", "F6")]
    [InlineData("timeout", "F5")]
    [InlineData("release", "F5")]
    public void Three_button_chord_defers_its_subset(string next, string expected)
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "l1", "r1"), Chord(Key.F6, "l1", "r1", "cross")));
        rig.Press(Buttons.L1).Advance(20).Press(Buttons.R1);
        Assert.Empty(rig.Take());

        if (next == "release")
        {
            rig.Release(Buttons.L1);
        }
        else if (next == "third")
        {
            rig.Skip(30);
            rig.Press(Buttons.Cross);
        }
        else
        {
            rig.Advance(30);
        }

        Assert.Equal(next == "release" ? [$"{expected} down", $"{expected} up"] : new[] { $"{expected} down" }, rig.Take());
        rig.Release(Buttons.L1 | Buttons.R1 | Buttons.Cross);
        Assert.Equal(next == "release" ? [] : new[] { $"{expected} up" }, rig.Take());
    }

    [Fact]
    public void Equal_size_conflicts_follow_configuration_order()
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F6, "cross", "circle"), Chord(Key.F5, "l1", "cross")));
        rig.Press(Buttons.L1 | Buttons.Cross | Buttons.Circle);
        Assert.Equal(["F6 down"], rig.Take());
    }

    [Fact]
    public void Touchpad_chord_suppresses_its_zone_but_a_solo_click_reaches_the_zone()
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "touchpad", "cross")) with
        {
            Touchpad = new() { Mode = TouchpadMode.Zones, Zones = [Bind.Key(Key.X), Bind.Key(Key.Y)] },
        });
        rig.Touch(new TouchPoint(true, 1, 1800, 100)).Press(Buttons.Touchpad).Press(Buttons.Cross);
        Assert.Equal(["F5 down"], rig.Take());
        rig.Release(Buttons.Touchpad | Buttons.Cross).Press(Buttons.Touchpad).Release(Buttons.Touchpad);
        Assert.Equal(["F5 up", "Y down", "Y up"], rig.Take());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Simultaneous_release_handles_active_chord_and_flushed_singles(bool flushSingles)
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "l1", "cross")));
        rig.Press(Buttons.L1);
        if (flushSingles)
        {
            rig.Advance(50);
        }

        rig.Press(Buttons.Cross).Advance(50);
        Assert.Equal(flushSingles ? ["A down", "B down"] : new[] { "F5 down" }, rig.Take());
        rig.Release(Buttons.L1 | Buttons.Cross);
        Assert.Equal(flushSingles ? ["B up", "A up"] : new[] { "F5 up" }, rig.Take());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_pad_click_in_mouse_mode_suppresses_tap_even_before_chord_resolution(bool completeChord)
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "touchpad", "cross")) with
        {
            Touchpad = new() { Mode = TouchpadMode.Mouse },
        });
        rig.Touch(new TouchPoint(true, 1, 400, 100)).Press(Buttons.Touchpad);
        if (completeChord)
        {
            rig.Press(Buttons.Cross);
        }

        rig.Release(Buttons.Touchpad | Buttons.Cross).Touch(default).Advance(50);
        Assert.Equal(completeChord ? ["F5 down", "F5 up"] : Array.Empty<string>(), rig.Take());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deferred_pad_zone_is_the_zone_at_physical_press(bool releaseBeforeTimeout)
    {
        using var rig = new MapperRig(Mapping(Chord(Key.F5, "touchpad", "cross")) with
        {
            Touchpad = new() { Mode = TouchpadMode.Zones, Zones = [Bind.Key(Key.X), Bind.Key(Key.Y)] },
        });
        rig.Touch(new TouchPoint(true, 1, 950, 100)).Press(Buttons.Touchpad)
            .Advance(10).Touch(new TouchPoint(true, 1, 970, 100));
        if (!releaseBeforeTimeout)
        {
            rig.Advance(40);
            Assert.Equal(["X down"], rig.Take());
        }

        rig.Release(Buttons.Touchpad);
        Assert.Equal(releaseBeforeTimeout ? ["X down", "X up"] : new[] { "X up" }, rig.Take());
    }
}
