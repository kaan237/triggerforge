using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class TouchpadMappingTests
{
    private static TouchPoint P(ushort x, ushort y, byte id = 1) => new(true, id, x, y);

    private static MapperRig Mouse(TouchpadMapping? pad = null) => new(new MappingConfig
    {
        Buttons = Bind.Map(("touchpad", Bind.Key(Key.C))),
        Touchpad = pad ?? new TouchpadMapping { Mode = TouchpadMode.Mouse, Acceleration = 0 },
    });

    private static MapperRig Zones(ZoneLayout layout = ZoneLayout.Halves) => new(new MappingConfig
    {
        Buttons = Bind.Map(("touchpad", Bind.Key(Key.C))),
        Touchpad = new TouchpadMapping
        {
            Mode = TouchpadMode.Zones,
            ZoneLayout = layout,
            Zones = layout == ZoneLayout.Halves
                ? [Bind.Key(Key.A), Bind.Key(Key.B)]
                : [Bind.Key(Key.Digit1), Bind.Key(Key.Digit2), Bind.Key(Key.Digit3), Bind.Key(Key.Digit4)],
            Swipes = new SwipeActions { Right = Bind.Keys(Key.D), Up = Bind.Keys(Key.W) },
        },
    });

    [Fact]
    public void Full_pad_width_is_900_pixels_at_sensitivity_1()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(100, 100));
        rig.Skip(10);
        rig.Touch(P(292, 100));
        Assert.Equal(["move 90,0"], rig.Take());

        rig.Skip(10);
        rig.Touch(P(292, 196));
        Assert.Equal(["move 0,45"], rig.Take());
    }

    [Fact]
    public void Sensitivity_scales_the_delta()
    {
        using MapperRig rig = Mouse(new TouchpadMapping { Mode = TouchpadMode.Mouse, Acceleration = 0, Sensitivity = 2 });
        rig.Touch(P(100, 100));
        rig.Skip(10);
        rig.Touch(P(292, 100));
        Assert.Equal(180, rig.Fake.Injector.MovedX);
    }

    [Fact]
    public void Acceleration_raises_the_gain_with_speed()
    {
        using MapperRig rig = Mouse(new TouchpadMapping { Mode = TouchpadMode.Mouse, Acceleration = 1 });
        rig.Touch(P(100, 100));
        rig.Skip(10);
        rig.Touch(P(292, 100));
        Assert.True(rig.Fake.Injector.MovedX > 90);
    }

    [Fact]
    public void Moves_keep_the_sub_pixel_remainder()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(1000, 500));
        for (ushort i = 1; i <= 5; i++)
        {
            rig.Skip(5);
            rig.Touch(P((ushort)(1000 + i), 500));
        }

        // 5 × 0.46875 px.
        Assert.Equal(["move 1,0", "move 1,0"], rig.Take());
    }

    [Fact]
    public void Moves_are_sent_at_most_every_4_ms()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(100, 100));
        rig.Skip(10);
        rig.Touch(P(200, 100));
        rig.Skip(1);
        rig.Touch(P(300, 100));
        Assert.Single(rig.Take());
        rig.Advance(4);
        Assert.Single(rig.Take());
        Assert.Equal(93, rig.Fake.Injector.MovedX);
    }

    [Fact]
    public void Tap_clicks_left_and_two_finger_tap_clicks_right()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(500, 500));
        rig.Skip(100);
        rig.Touch(default);
        Assert.Equal(["mouse Left down"], rig.Take());
        rig.Advance(30);
        Assert.Equal(["mouse Left up"], rig.Take());

        rig.Touch(P(500, 500), P(900, 500, 2));
        rig.Skip(100);
        rig.Touch(default);
        rig.Advance(30);
        Assert.Equal(["mouse Right down", "mouse Right up"], rig.Take());
    }

    [Fact]
    public void Long_or_moving_touch_is_not_a_tap()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(500, 500));
        rig.Skip(200);
        rig.Touch(default);
        rig.Run(100);
        Assert.Empty(rig.Take());

        rig.Touch(P(500, 500));
        rig.Skip(20);
        rig.Touch(P(560, 500));
        rig.Skip(20);
        rig.Touch(default);
        rig.Run(100);
        Assert.DoesNotContain(rig.Take(), e => e.StartsWith("mouse", StringComparison.Ordinal));
    }

    [Fact]
    public void Tap_to_click_can_be_off()
    {
        using MapperRig rig = Mouse(new TouchpadMapping { Mode = TouchpadMode.Mouse, TapToClick = false });
        rig.Touch(P(500, 500));
        rig.Skip(50);
        rig.Touch(default);
        rig.Run(100);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Two_fingers_scroll_smoothly()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(500, 300), P(900, 300, 2));
        rig.Skip(10);
        rig.Touch(P(500, 408), P(900, 408, 2));
        Assert.Equal(["wheel -120 v"], rig.Take());

        rig.Skip(10);
        rig.Touch(P(554, 408), P(954, 408, 2));
        Assert.Equal(["wheel 60 h"], rig.Take());
    }

    [Fact]
    public void Mouse_mode_pad_click_uses_the_touchpad_binding()
    {
        using MapperRig rig = Mouse();
        rig.Touch(P(1500, 500));
        rig.Press(Buttons.Touchpad).Release(Buttons.Touchpad);
        Assert.Equal(["C down", "C up"], rig.Take());
    }

    [Fact]
    public void Zone_click_goes_to_the_zone_under_the_finger()
    {
        using (MapperRig rig = Zones())
        {
            rig.Touch(P(1500, 500));
            rig.Press(Buttons.Touchpad).Release(Buttons.Touchpad);
            rig.Touch(default);
            rig.Touch(P(200, 500));
            rig.Press(Buttons.Touchpad);
            // The release goes to the zone that took the press, even after the finger moves.
            rig.Touch(P(1700, 500));
            rig.Release(Buttons.Touchpad);
            Assert.Equal(["B down", "B up", "A down", "A up"], rig.Take());
        }

        using (MapperRig rig = Zones(ZoneLayout.Quadrants))
        {
            rig.Touch(P(300, 900));
            rig.Press(Buttons.Touchpad).Release(Buttons.Touchpad);
            rig.Touch(default);
            rig.Skip(600);
            rig.Touch(P(1800, 100));
            rig.Press(Buttons.Touchpad).Release(Buttons.Touchpad);
            Assert.Equal(["Digit3 down", "Digit3 up", "Digit2 down", "Digit2 up"], rig.Take());
        }
    }

    [Fact]
    public void Swipe_fires_on_lift()
    {
        using MapperRig rig = Zones();
        rig.Touch(P(100, 500));
        rig.Skip(100);
        rig.Touch(P(700, 500));
        Assert.Empty(rig.Take());
        rig.Skip(100);
        rig.Touch(default);
        Assert.Equal(["D down"], rig.Take());
        rig.Advance(30);
        Assert.Equal(["D up"], rig.Take());

        rig.Touch(P(900, 800));
        rig.Skip(100);
        rig.Touch(P(900, 400));
        rig.Touch(default);
        rig.Advance(30);
        Assert.Equal(["W down", "W up"], rig.Take());
    }

    [Fact]
    public void Slow_or_short_swipe_does_nothing()
    {
        using MapperRig rig = Zones();
        rig.Touch(P(100, 500));
        rig.Skip(600);
        rig.Touch(P(700, 500));
        rig.Touch(default);

        rig.Touch(P(100, 500));
        rig.Skip(100);
        rig.Touch(P(500, 500));
        rig.Touch(default);
        rig.Run(100);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Swipe_cancels_a_zone_press_started_during_it()
    {
        using MapperRig rig = Zones();
        rig.Touch(P(100, 500));
        rig.Press(Buttons.Touchpad);
        Assert.Equal(["A down"], rig.Take());
        rig.Skip(100);
        rig.Touch(P(700, 500));
        rig.Touch(default);
        Assert.Equal(["A up", "D down"], rig.Take());
        rig.Release(Buttons.Touchpad);
        rig.Advance(30);
        Assert.Equal(["D up"], rig.Take());
    }
}
