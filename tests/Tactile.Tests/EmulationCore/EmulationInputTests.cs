using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests.EmulationCore;

public class EmulationInputTests
{
    private static EmulationConfig Remap(params (string From, string To)[] pairs) =>
        new() { Remap = pairs.ToDictionary(p => p.From, p => p.To) };

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
