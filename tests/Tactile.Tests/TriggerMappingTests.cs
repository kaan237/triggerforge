using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

public class TriggerMappingTests
{
    private static TriggerMapping Stages(TriggerStageMode mode) => new()
    {
        Mode = mode, SoftThreshold = 0.4, FullThreshold = 0.8, Hysteresis = 0.1,
        Soft = Bind.Keys(Key.A), Full = Bind.Keys(Key.B),
    };

    private static MapperRig Rig(TriggerStageMode mode) => new(Bind.Buttons(("r2", Bind.Key(Key.C))) with
    {
        Triggers = new() { Right = Stages(mode) },
    });

    private static void Pull(MapperRig rig, byte travel, Buttons buttons = Buttons.None) =>
        rig.Mapper.OnInput(Inputs.Make(buttons, r2: travel), default, rig.Now);

    [Fact]
    public void Additive_stages_use_independent_hysteresis_and_replace_the_digital_binding()
    {
        using MapperRig rig = Rig(TriggerStageMode.Additive);
        Pull(rig, 101, Buttons.R2);
        Assert.Empty(rig.Take());
        Pull(rig, 102, Buttons.R2);
        Pull(rig, 204, Buttons.R2);
        Assert.Equal(["A down", "B down"], rig.Take());
        Pull(rig, 179);
        Assert.Empty(rig.Take());
        Pull(rig, 178);
        Assert.Equal(["B up"], rig.Take());
        Pull(rig, 77);
        Assert.Empty(rig.Take());
        Pull(rig, 76);
        Assert.Equal(["A up"], rig.Take());
    }

    [Fact]
    public void Exclusive_full_replaces_soft_until_a_fresh_pull()
    {
        using MapperRig rig = Rig(TriggerStageMode.Exclusive);
        Pull(rig, 102);
        Pull(rig, 204);
        Assert.Equal(["A down", "A up", "B down"], rig.Take());
        Pull(rig, 160);
        Pull(rig, 110);
        Assert.Equal(["B up"], rig.Take());
        Pull(rig, 0);
        Pull(rig, 102);
        Assert.Equal(["A down"], rig.Take());
    }

    [Fact]
    public void Hip_fire_fast_full_skips_soft_and_slow_full_adds_to_soft()
    {
        using MapperRig rig = Rig(TriggerStageMode.HipFire);
        Pull(rig, 102);
        rig.Advance(79);
        Pull(rig, 204);
        Assert.Equal(["B down"], rig.Take());
        rig.Advance(200);
        Pull(rig, 100);
        Pull(rig, 0);
        Assert.Equal(["B up"], rig.Take());

        Pull(rig, 102);
        rig.Advance(80);
        Assert.Equal(["A down"], rig.Take());
        Pull(rig, 204);
        Pull(rig, 0);
        Assert.Equal(["B down", "B up", "A up"], rig.Take());
    }

    [Fact]
    public void Hip_fire_quick_soft_release_taps_soft()
    {
        using MapperRig rig = Rig(TriggerStageMode.HipFire);
        Pull(rig, 102);
        rig.Advance(20);
        Assert.Empty(rig.Take());
        Pull(rig, 0);
        Assert.Equal(["A down"], rig.Take());
        rig.Advance(30);
        Assert.Equal(["A up"], rig.Take());
    }

    [Theory]
    [InlineData(79, false)]
    [InlineData(79, true)]
    [InlineData(80, false)]
    [InlineData(80, true)]
    [InlineData(81, false)]
    [InlineData(81, true)]
    public void Hip_fire_deadline_is_independent_of_tick_order(int fullAtMs, bool tickFirst)
    {
        using MapperRig rig = Rig(TriggerStageMode.HipFire);
        Pull(rig, 102);
        rig.Skip(fullAtMs);
        if (tickFirst)
        {
            rig.Advance(0);
        }

        Pull(rig, 204);
        rig.Advance(80);
        Assert.Equal(fullAtMs < 80 ? ["B down"] : new[] { "A down", "B down" }, rig.Take());
    }

    [Fact]
    public void Analog_stages_use_shaping_and_yield_to_chord_members_and_activators()
    {
        using var shaped = new MapperRig(new MappingConfig { Triggers = new() { Right = Stages(TriggerStageMode.Additive) } },
            triggerResponse: new() { Right = new() { Deadzone = 100, Max = 200 } });
        Pull(shaped, 110);
        Assert.Empty(shaped.Take());
        Pull(shaped, 200);
        Assert.Equal(["A down", "B down"], shaped.Take());

        using var rig = new MapperRig(new MappingConfig
        {
            Layers = [new() { Button = "l2", Buttons = Bind.Map(("circle", Bind.Key(Key.E))) }],
            Triggers = new() { Left = Stages(TriggerStageMode.Additive), Right = Stages(TriggerStageMode.Additive) },
            Chords = [new() { Buttons = ["r2", "cross"], Action = Bind.Keys(Key.D) }],
        });
        rig.Mapper.OnInput(Inputs.Make(Buttons.L2 | Buttons.R2 | Buttons.Cross | Buttons.Circle, l2: 255, r2: 255), default, rig.Now);
        Assert.Equal(["D down", "E down"], rig.Take());
    }

    [Fact]
    public void Layer_change_releases_trigger_and_active_chord_and_discards_pending_chords()
    {
        using var rig = new MapperRig(Bind.Buttons(("square", Bind.Key(Key.C))) with
        {
            Chords =
            [
                new() { Buttons = ["cross", "circle"], Action = Bind.Keys(Key.D) },
                new() { Buttons = ["square", "triangle"], Action = Bind.Keys(Key.E) },
            ],
            Triggers = new() { Right = Stages(TriggerStageMode.Additive) },
        });
        Buttons held = Buttons.Cross | Buttons.Circle | Buttons.Square;
        Pull(rig, 255, held);
        Assert.Equal(["D down", "A down", "B down"], rig.Take());
        Pull(rig, 255, held | Buttons.PS);
        Assert.Equal(["D up", "A up", "B up"], rig.Take());
        rig.Advance(300);
        Pull(rig, 255, held | Buttons.PS | Buttons.Triangle);
        rig.Advance(300);
        Assert.Empty(rig.Take());
        Pull(rig, 0, Buttons.PS);
        Pull(rig, 255, Buttons.PS);
        Assert.Equal(["A down", "B down"], rig.Take());
    }
}
