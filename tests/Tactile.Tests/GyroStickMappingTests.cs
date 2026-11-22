using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class GyroMouseTests
{
    private static MapperRig Rig(GyroMouseConfig gyro) => new(new MappingConfig { GyroMouse = gyro });

    // Sends the same rate every 10 ms.
    private static void Feed(MapperRig rig, Vector3 rate, int reports)
    {
        for (int i = 0; i < reports; i++)
        {
            rig.Gyro(rate);
            rig.Skip(10);
        }
    }

    [Fact]
    public void While_released_moves_then_discards_the_filter_while_held()
    {
        using MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.WhileReleased, Smoothing = 0.9 });

        Feed(rig, new Vector3(0, 100, 0), 5);
        Assert.NotEmpty(rig.Take());
        rig.Press(Buttons.R1);
        Feed(rig, new Vector3(0, 100, 0), 5);
        Assert.Empty(rig.Take());
        rig.Gyro(default).Release(Buttons.R1);
        Feed(rig, default, 5);
        Assert.Empty(rig.Take());
        Feed(rig, new Vector3(0, 100, 0), 5);
        Assert.NotEmpty(rig.Take());
    }

    [Fact]
    public void While_touchpad_uses_either_finger()
    {
        using MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.WhileTouchpad });
        var rate = new Vector3(0, 20, 0);
        var finger = new TouchPoint(true, 1, 100, 100);
        Feed(rig, rate, 5);
        Assert.Empty(rig.Take());
        rig.Touch(finger);
        Feed(rig, rate, 5);
        Assert.NotEmpty(rig.Take());
        rig.Touch(default, finger);
        Feed(rig, rate, 5);
        Assert.NotEmpty(rig.Take());
        rig.Touch(default);
        Feed(rig, rate, 5);
        Assert.Empty(rig.Take());
    }

    [Fact]
    public void Ten_pixels_per_degree_at_sensitivity_1()
    {
        using MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Smoothing = 0 });

        // 10 deg/s for 100 ms = 1 degree.
        Feed(rig, new Vector3(0, 10, 0), 11);
        Assert.Equal(-10, rig.Fake.Injector.MovedX);
        Assert.Equal(0, rig.Fake.Injector.MovedY);

        using MapperRig pitch = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Smoothing = 0 });
        Feed(pitch, new Vector3(10, 0, 0), 11);
        Assert.Equal(-10, pitch.Fake.Injector.MovedY);
        Assert.Equal(0, pitch.Fake.Injector.MovedX);
    }

    [Fact]
    public void Below_the_deadzone_nothing_moves()
    {
        using MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Smoothing = 0, Deadzone = 0.5 });

        Feed(rig, new Vector3(0.3f, 0.3f, 0), 50);
        Assert.Empty(rig.Take());
        Feed(rig, new Vector3(0, 5, 0), 20);
        Assert.NotEmpty(rig.Take());
    }

    [Theory]
    [InlineData(GyroAxis.Yaw, -1)]
    [InlineData(GyroAxis.Yaw, 1)]
    [InlineData(GyroAxis.Roll, -1)]
    [InlineData(GyroAxis.Roll, 1)]
    public void Gyro_stick_and_mouse_move_in_the_same_horizontal_direction(GyroAxis axis, int direction)
    {
        using MapperRig mouse = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Axis = axis, Smoothing = 0 });
        var stick = new GyroStick(new GyroStickConfig { Mode = GyroMouseMode.Always, Axis = axis, Smoothing = 0 });
        Vector3 rate = axis == GyroAxis.Yaw ? new(0, -60 * direction, 0) : new(0, 0, -60 * direction);
        InputState input = Inputs.Make(right: new Stick(128, 128));

        Feed(mouse, rate, 3);
        var offset = stick.Input(rate, input, TimeSpan.Zero);
        InputState result = EmulationInput.Transform(input, new EmulationConfig(), gyro: offset);
        Assert.Equal(direction, Math.Sign(mouse.Fake.Injector.MovedX));
        Assert.Equal(direction, Math.Sign(result.Right.X - input.Right.X));
    }

    [Fact]
    public void Invert_and_roll_axis()
    {
        using (MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Smoothing = 0, InvertX = true, InvertY = true }))
        {
            Feed(rig, new Vector3(10, 10, 0), 11);
            Assert.Equal(10, rig.Fake.Injector.MovedX);
            Assert.Equal(10, rig.Fake.Injector.MovedY);
        }

        using (MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Smoothing = 0, Axis = GyroAxis.Roll, SensitivityX = 2 }))
        {
            Feed(rig, new Vector3(0, 10, 10), 11);
            Assert.Equal(-20, rig.Fake.Injector.MovedX);
        }
    }

    [Fact]
    public void While_held_and_toggle_follow_the_button()
    {
        using (MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.WhileHeld, Button = "r1", Smoothing = 0 }))
        {
            Feed(rig, new Vector3(0, 10, 0), 11);
            Assert.Empty(rig.Take());
            rig.Press(Buttons.R1);
            Feed(rig, new Vector3(0, 10, 0), 11);
            Assert.Equal(-10, rig.Fake.Injector.MovedX);
        }

        using (MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Toggle, Button = "l1", Smoothing = 0 }))
        {
            rig.Press(Buttons.L1).Release(Buttons.L1);
            Feed(rig, new Vector3(0, 10, 0), 11);
            Assert.Equal(-10, rig.Fake.Injector.MovedX);
            rig.Press(Buttons.L1).Release(Buttons.L1);
            rig.Take();
            Feed(rig, new Vector3(0, 10, 0), 11);
            Assert.Empty(rig.Take());
        }
    }

    [Fact]
    public void Smoothing_eases_in()
    {
        using MapperRig rig = Rig(new GyroMouseConfig { Mode = GyroMouseMode.Always, Smoothing = 0.9, Deadzone = 0 });
        rig.Gyro(default);
        rig.Skip(10);
        rig.Gyro(new Vector3(0, 100, 0));
        rig.Skip(10);
        rig.Gyro(new Vector3(0, 100, 0));
        // Unsmoothed this would be 20 px.
        Assert.InRange(rig.Fake.Injector.MovedX, -5, -1);
    }
}

public class StickMappingTests
{
    private static readonly Stick Center = new(128, 128);

    private static MapperRig Rig(StickMapping left, StickShape shape = StickShape.Round) =>
        new(new MappingConfig { Sticks = new StickMappingPair { Left = left } },
            sticks: new StickPair { Left = new StickConfig { Shape = shape } });

    [Fact]
    public void Keys_mode_presses_directions_and_diagonals()
    {
        using MapperRig rig = Rig(new StickMapping { Mode = StickMappingMode.Keys });

        rig.Sticks(left: new Stick(255, 128));
        Assert.Equal(["D down"], rig.Take());
        rig.Sticks(left: new Stick(255, 0));
        Assert.Equal(["W down"], rig.Take());
        rig.Sticks(left: new Stick(128, 0));
        Assert.Equal(["D up"], rig.Take());
        rig.Sticks(left: new Stick(0, 255));
        Assert.Equal(["W up", "S down", "A down"], rig.Take());
        rig.Sticks(left: Center);
        Assert.Equal(["S up", "A up"], rig.Take());
    }

    [Fact]
    public void Threshold_applies_per_axis()
    {
        using MapperRig rig = Rig(new StickMapping { Mode = StickMappingMode.Keys, Threshold = 0.8 });

        // Full diagonal is about 0.707 per axis on a round stick.
        rig.Sticks(left: new Stick(255, 0));
        Assert.Empty(rig.Take());
        rig.Sticks(left: new Stick(128, 0));
        Assert.Equal(["W down"], rig.Take());
    }

    [Fact]
    public void None_direction_and_shared_keys_are_counted()
    {
        using var rig = new MapperRig(new MappingConfig
        {
            Buttons = Bind.Map(("cross", Bind.Key(Key.W))),
            Sticks = new StickMappingPair { Left = new StickMapping { Mode = StickMappingMode.Keys, Keys = new StickKeys { Left = Key.None } } },
        });

        rig.Sticks(left: new Stick(0, 128));
        Assert.Empty(rig.Take());
        rig.Sticks(left: new Stick(128, 0));
        rig.Press(Buttons.Cross);
        Assert.Equal(["W down"], rig.Take());
        rig.Sticks(left: Center);
        Assert.Empty(rig.Take());
        rig.Release(Buttons.Cross);
        Assert.Equal(["W up"], rig.Take());
    }

    [Fact]
    public void Keys_follow_the_profile_stick_processing()
    {
        using var rig = new MapperRig(
            new MappingConfig { Sticks = new StickMappingPair { Left = new StickMapping { Mode = StickMappingMode.Keys } } },
            sticks: new StickPair { Left = new StickConfig { InvertX = true } });

        rig.Sticks(left: new Stick(255, 128));
        Assert.Equal(["A down"], rig.Take());
    }

    [Fact]
    public void Mouse_mode_moves_1200_px_per_second_at_full_tilt()
    {
        // Square shape: the off axis is exactly 0 and full tilt exactly 1.
        using MapperRig rig = Rig(new StickMapping { Mode = StickMappingMode.Mouse }, StickShape.Square);

        rig.Sticks(left: new Stick(255, 128));
        rig.Run(100, step: 10);
        Assert.InRange(rig.Fake.Injector.MovedX, 119, 120);
        Assert.Equal(0, rig.Fake.Injector.MovedY);
    }

    [Fact]
    public void Scroll_mode_scrolls_15_notches_per_second_times_speed()
    {
        using MapperRig rig = Rig(new StickMapping { Mode = StickMappingMode.Scroll, Speed = 2 }, StickShape.Square);

        rig.Sticks(left: new Stick(128, 0));
        rig.Run(100, step: 10);
        int total = rig.Take().Where(e => e.EndsWith(" v", StringComparison.Ordinal)).Sum(e => int.Parse(e.Split(' ')[1]));
        // 30 notches/s × 120 × 0.1 s.
        Assert.InRange(total, 359, 360);
    }
}
