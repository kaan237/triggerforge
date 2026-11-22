using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests.EmulationCore;

public class EmulationInputTests
{
    private static EmulationConfig Remap(params (string From, string To)[] pairs) =>
        new() { Remap = pairs.ToDictionary(p => p.From, p => p.To) };

    [Fact]
    public void Gyro_stick_applies_deadzone_anti_deadzone_and_independent_sensitivities()
    {
        var gyro = new GyroStick(new GyroStickConfig
        {
            Mode = GyroMouseMode.Always, Deadzone = 1, AntiDeadzone = 0.25, Smoothing = 0,
            SensitivityX = 2, SensitivityY = 0.5,
        });
        InputState input = Inputs.Make();
        Assert.Equal((0f, 0f), gyro.Input(new Vector3(1, 0.5f, 0), input, TimeSpan.Zero));
        Assert.Equal((-0.625f, 0.4375f), gyro.Input(new Vector3(60, 30, 0), input, TimeSpan.Zero));
        Assert.Equal((-1f, -1f), gyro.Input(new Vector3(-500, 500, 0), input, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(GyroAxis.Yaw, -0.5, -0.25)]
    [InlineData(GyroAxis.Roll, 1, 0.5)]
    public void Gyro_stick_selects_yaw_or_roll_inverts_both_axes_and_smooths(GyroAxis axis, double x, double nextX)
    {
        var gyro = new GyroStick(new GyroStickConfig
        {
            Mode = GyroMouseMode.Always, Axis = axis, Smoothing = 0.5, InvertX = true, InvertY = true,
        });
        InputState input = Inputs.Make();
        Assert.Equal(((float)x, -0.25f), gyro.Input(new Vector3(30, -60, 120), input, TimeSpan.Zero));
        Assert.Equal(((float)nextX, -0.125f), gyro.Input(default, input, TimeSpan.FromMilliseconds(10)));
    }

    [Theory]
    [InlineData(GyroMouseMode.Off, false, false, false, false, false, false)]
    [InlineData(GyroMouseMode.WhileHeld, false, true, true, false, false, true)]
    [InlineData(GyroMouseMode.WhileReleased, true, false, false, true, true, false)]
    [InlineData(GyroMouseMode.Toggle, false, true, true, true, true, false)]
    [InlineData(GyroMouseMode.WhileTouchpad, false, false, false, true, true, false)]
    public void Gyro_stick_activation_tracks_buttons_and_both_touches(GyroMouseMode mode,
        bool initial, bool held, bool stillHeld, bool finger1, bool finger2, bool nextPress)
    {
        var gyro = new GyroStick(new GyroStickConfig { Mode = mode });
        var rate = new Vector3(0, 120, 0);
        var touch = new TouchPoint(true, 1, 100, 100);
        (InputState Input, bool Active)[] samples =
        [
            (Inputs.Make(), initial), (Inputs.Make(Buttons.L2), held), (Inputs.Make(Buttons.L2), stillHeld),
            (Inputs.Make(touch1: touch), finger1), (Inputs.Make(touch2: touch), finger2), (Inputs.Make(Buttons.L2), nextPress),
        ];
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i].Active ? -1f : 0f, gyro.Input(rate, samples[i].Input, TimeSpan.FromMilliseconds(i * 10)).X);
        }
    }

    [Fact]
    public void Gyro_stick_restarts_smoothing_after_inactive_input()
    {
        var gyro = new GyroStick(new GyroStickConfig { Mode = GyroMouseMode.WhileHeld, Smoothing = 0.9 });
        Assert.Equal(-1f, gyro.Input(new Vector3(0, 120, 0), Inputs.Make(Buttons.L2), TimeSpan.Zero).X);
        Assert.Equal(0f, gyro.Input(default, Inputs.Make(), TimeSpan.FromMilliseconds(5)).X);
        Assert.Equal(0.5f, gyro.Input(new Vector3(0, -60, 0), Inputs.Make(Buttons.L2), TimeSpan.FromMilliseconds(10)).X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gyro_is_added_to_raw_virtual_right_stick_after_swap_and_clamped(bool swap)
    {
        InputState input = Inputs.Make(left: new Stick(200, 200), right: new Stick(230, 230));
        InputState transformed = EmulationInput.Transform(input, new EmulationConfig { SwapSticks = swap },
            gyro: (0.8f, -0.8f));
        Assert.Equal(new Stick(255, 255), transformed.Right);
        Assert.Equal(swap ? input.Right : input.Left, transformed.Left);

        transformed = EmulationInput.Transform(input, new EmulationConfig { SwapSticks = swap },
            gyro: (0.2f, -0.2f));
        Assert.Equal(swap ? new Stick(226, 226) : new Stick(255, 255), transformed.Right);
    }

    [Fact]
    public void Triple_and_cycle_alone_mark_the_virtual_buttons_as_mapped()
    {
        ActionConfig action = ActionConfig.ForKeys(Tactile.Input.Key.A);
        var profile = new Profile
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = Bind.Map(("cross", new Binding { Triple = action }), ("circle", new Binding { Cycle = [action] })),
            },
        };

        Assert.Equal(Buttons.Cross | Buttons.Circle, EmulationInput.MappedButtons(profile));
    }

    [Fact]
    public void Without_config_input_passes_through()
    {
        InputState s = Inputs.Make(Buttons.Cross | Buttons.L1, new Stick(10, 20), new Stick(30, 40), l2: 100, r2: 200);
        Assert.Equal(s, EmulationInput.Transform(s, new EmulationConfig()));
    }

    [Fact]
    public void Remap_moves_listed_buttons_and_none_drops_them()
    {
        EmulationConfig c = Remap(("cross", "circle"), ("square", "none"));
        InputState s = EmulationInput.Transform(Inputs.Make(Buttons.Cross | Buttons.Square | Buttons.Triangle), c);
        Assert.Equal(Buttons.Circle | Buttons.Triangle, s.Buttons);
    }

    [Fact]
    public void Swapped_buttons_swap()
    {
        EmulationConfig c = Remap(("cross", "circle"), ("circle", "cross"));
        Assert.Equal(Buttons.Circle, EmulationInput.Transform(Inputs.Make(Buttons.Cross), c).Buttons);
        Assert.Equal(Buttons.Cross, EmulationInput.Transform(Inputs.Make(Buttons.Circle), c).Buttons);
    }

    [Fact]
    public void Trigger_to_trigger_carries_the_analog_value()
    {
        EmulationConfig c = Remap(("l2", "r2"), ("r2", "l2"));
        InputState s = EmulationInput.Transform(Inputs.Make(Buttons.L2, l2: 90, r2: 0), c);
        Assert.Equal(Buttons.R2, s.Buttons);
        Assert.Equal(0, s.L2);
        Assert.Equal(90, s.R2);
    }

    [Fact]
    public void Two_sources_on_one_trigger_take_the_larger_value()
    {
        InputState s = EmulationInput.Transform(Inputs.Make(Buttons.L2 | Buttons.R2, l2: 120, r2: 60), Remap(("l2", "r2")));
        Assert.Equal(Buttons.R2, s.Buttons);
        Assert.Equal(0, s.L2);
        Assert.Equal(120, s.R2);
    }

    [Fact]
    public void Trigger_to_button_zeroes_the_analog_and_presses_while_the_digital_bit_is_down()
    {
        EmulationConfig c = Remap(("l2", "cross"));
        InputState pressed = EmulationInput.Transform(Inputs.Make(Buttons.L2, l2: 200), c);
        Assert.Equal(Buttons.Cross, pressed.Buttons);
        Assert.Equal(0, pressed.L2);

        // Partly pulled, digital bit not yet set: nothing.
        InputState light = EmulationInput.Transform(Inputs.Make(Buttons.None, l2: 20), c);
        Assert.Equal(Buttons.None, light.Buttons);
        Assert.Equal(0, light.L2);
    }

    [Fact]
    public void Button_to_trigger_pulls_it_fully()
    {
        InputState s = EmulationInput.Transform(Inputs.Make(Buttons.Cross, l2: 0), Remap(("cross", "l2")));
        Assert.Equal(Buttons.L2, s.Buttons);
        Assert.Equal(255, s.L2);
    }

    [Fact]
    public void Swap_sticks_exchanges_the_two_sticks_only()
    {
        InputState s = EmulationInput.Transform(Inputs.Make(Buttons.L3, new Stick(1, 2), new Stick(3, 4)), new EmulationConfig { SwapSticks = true });
        Assert.Equal(new Stick(3, 4), s.Left);
        Assert.Equal(new Stick(1, 2), s.Right);
        Assert.Equal(Buttons.L3, s.Buttons);
    }

    [Fact]
    public void Hidden_buttons_are_released_before_remap()
    {
        EmulationConfig c = Remap(("cross", "circle"));
        InputState s = EmulationInput.Transform(Inputs.Make(Buttons.Cross | Buttons.Circle | Buttons.R2, r2: 180), c, Buttons.Cross | Buttons.R2);
        Assert.Equal(Buttons.Circle, s.Buttons);
        Assert.Equal(0, s.R2);
    }

    [Fact]
    public void Mapped_buttons_come_from_both_layers_while_mapping_is_on()
    {
        var bound = new Binding { Press = ActionConfig.ForKeys(Tactile.Input.Key.A) };
        var profile = new Profile
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                LayerButton = "ps",
                Buttons = new Dictionary<string, Binding> { ["cross"] = bound, ["circle"] = new Binding() },
                Layer = new Dictionary<string, Binding> { ["square"] = new Binding { Hold = ActionConfig.ForMouse(Tactile.Input.MouseButton.Left) } },
                Touchpad = new TouchpadMapping { Mode = TouchpadMode.Zones },
            },
        };

        // Circle has only a "none" binding; PS is taken because the layer is used.
        Assert.Equal(Buttons.Cross | Buttons.Square | Buttons.PS | Buttons.Touchpad, EmulationInput.MappedButtons(profile));
        Assert.Equal(Buttons.None, EmulationInput.MappedButtons(profile with { Mapping = profile.Mapping with { Enabled = false } }));

        var baseOnly = profile with { Mapping = profile.Mapping with { Layer = new Dictionary<string, Binding>(), Touchpad = new TouchpadMapping() } };
        Assert.Equal(Buttons.Cross, EmulationInput.MappedButtons(baseOnly));
    }
}

public class LatencyStatsTests
{
    [Fact]
    public void Percentiles_over_the_window()
    {
        var stats = new LatencyStats();
        Assert.Equal(LatencySummary.Empty, stats.Summary());

        for (int i = 1; i <= 100; i++)
        {
            stats.Add(i);
        }

        LatencySummary s = stats.Summary();
        Assert.Equal(50, s.P50Ms);
        Assert.Equal(95, s.P95Ms);
        Assert.Equal(100, s.Samples);
    }

    [Fact]
    public void Old_samples_roll_out_and_bad_values_are_ignored()
    {
        var stats = new LatencyStats();
        for (int i = 0; i < LatencyStats.Window; i++)
        {
            stats.Add(100);
        }

        for (int i = 0; i < LatencyStats.Window; i++)
        {
            stats.Add(2);
        }

        stats.Add(double.NaN);
        stats.Add(-1);
        LatencySummary s = stats.Summary();
        Assert.Equal(2, s.P95Ms);
        Assert.Equal(LatencyStats.Window, s.Samples);
    }
}
