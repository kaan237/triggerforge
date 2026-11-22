using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

[Collection("Localization")]
public class MappingValidationTests
{
    [Fact]
    public void Phase_11_layers_and_chords_are_canonical_and_capped()
    {
        string[] names = ButtonNames.All.Select(ButtonNames.Name).ToArray();
        ButtonChord[] pairs = names.SelectMany((a, i) => names.Skip(i + 1)
            .Select(b => new ButtonChord { Buttons = [a, b], Action = Bind.Keys(Key.A) })).Take(40).ToArray();
        MappingConfig mapping = Normalize(new MappingConfig
        {
            LayerButton = "r3",
            Layers = [new() { Button = "  FN1 ", Name = new string('x', 35) }, new() { Button = "fn1" },
                new() { Button = "unknown" }, new() { Button = "paddleRight" }, new()],
            ChordWindowMs = 999,
            Chords =
            [
                new() { Buttons = ["cross", "CROSS", "unknown"], Action = Bind.Keys(Key.B) },
                new() { Buttons = ["cross", "r1", "L1", "square", "L1"], Action = Bind.Keys(Key.C) },
                new() { Buttons = ["l1", "r1", "cross", "square"], Action = Bind.Keys(Key.D) },
                .. pairs,
            ],
        }, null, out List<string> issues).Mapping;

        Assert.Equal(["fn1", "none", "none", "paddleRight"], mapping.Layers.Select(l => l.Button));
        Assert.Equal("fn1", mapping.LayerButton);
        Assert.Same(mapping.Layers[0].Buttons, mapping.Layer);
        Assert.Equal(32, mapping.Layers[0].Name.Length);
        Assert.Equal(300, mapping.ChordWindowMs);
        Assert.Equal(32, mapping.Chords.Count);
        Assert.All(mapping.Chords, c => Assert.InRange(c.Buttons.Count, 2, 3));
        Assert.Equal(mapping.Chords.Count, mapping.Chords.Select(c => string.Join(",", c.Buttons)).Distinct().Count());
        Assert.Equal(ButtonNames.All.Select(ButtonNames.Name).Where(n => n is "cross" or "l1" or "r1" or "square").Take(3),
            mapping.Chords[0].Buttons);
        Assert.Equal([Key.C], mapping.Chords[0].Action.Keys!);
        Assert.Equal(8, issues.Count);
        Assert.Equal(50, Normalize(new MappingConfig { ChordWindowMs = 0 }, null, out _).Mapping.ChordWindowMs);
    }

    [Fact]
    public void Phase_11_analog_ranges_and_actions_are_normalized()
    {
        MappingConfig mapping = Normalize(new MappingConfig
        {
            Triggers = new() { Right = new() { Mode = (TriggerStageMode)99, SoftThreshold = 9, FullThreshold = 0, Hysteresis = 1, HipFireMs = 0 } },
            Sticks = new()
            {
                Right = new() { RingMode = (StickRingMode)99, RingThreshold = 9, RingHysteresis = 0, RingAction = Bind.Keys(Key.None) },
            },
            FlickStick = new() { CountsPer360 = 0, Threshold = 1, FlickTimeMs = 0, Snap = (FlickSnapMode)99, DeadzoneAngle = 60, Smoothing = double.NaN },
            GyroMouse = new() { UseCalibration = true },
        }, null, out _).Mapping;

        TriggerMapping trigger = mapping.Triggers.Right;
        Assert.Equal(TriggerStageMode.Off, trigger.Mode);
        Assert.Equal(0.9, trigger.SoftThreshold);
        Assert.Equal(trigger.SoftThreshold + 0.05, trigger.FullThreshold);
        Assert.Equal(0.2, trigger.Hysteresis);
        Assert.Equal(20, trigger.HipFireMs);
        Assert.Equal(StickRingMode.Off, mapping.Sticks.Right.RingMode);
        Assert.Equal(0.98, mapping.Sticks.Right.RingThreshold);
        Assert.Equal(0.01, mapping.Sticks.Right.RingHysteresis);
        Assert.Equal(ActionKind.None, mapping.Sticks.Right.RingAction.Kind);
        Assert.Equal(new FlickStickConfig { CountsPer360 = 500, Threshold = 0.98, FlickTimeMs = 30, DeadzoneAngle = 45 }, mapping.FlickStick);
        Assert.True(mapping.GyroMouse.UseCalibration);
    }

    [Fact]
    public void Missing_layer_buttons_default_by_position_and_legacy_button_is_canonical()
    {
        const string json = """{"layers":[{},{},{},{"button":"unknown"}]}""";
        MappingConfig input = JsonSerializer.Deserialize<MappingConfig>(json, TactileJson.Compact)!;
        MappingConfig mapping = Normalize(input, null, out List<string> issues).Mapping;
        Assert.Equal(["ps", "none", "none", "none"], mapping.Layers.Select(l => l.Button));
        Assert.Contains("unknown", Assert.Single(issues));

        MappingConfig legacy = Normalize(new MappingConfig { LayerButton = " L1 " }, null, out issues).Mapping;
        Assert.Equal("l1", Assert.Single(legacy.Layers).Button);
        Assert.Equal("l1", legacy.LayerButton);
        Assert.Empty(issues);
    }

    private static Profile Normalize(MappingConfig m, IReadOnlyList<Macro>? macros, out List<string> issues)
    {
        issues = [];
        return Validation.Normalize(new Profile { Name = "T", Mapping = m, Macros = macros ?? [] }, issues);
    }

    [Fact]
    public void Ranges_are_clamped()
    {
        var m = new MappingConfig
        {
            Buttons = Bind.Map(("cross", new Binding
            {
                Press = Bind.Wheel(WheelDirection.Up, 50),
                HoldMs = 10,
                DoubleMs = 9999,
                TurboHz = 0,
            })),
            Touchpad = new TouchpadMapping { Sensitivity = 100, Acceleration = -1, ScrollSpeed = double.NaN },
            GyroMouse = new GyroMouseConfig { SensitivityX = 0, SensitivityY = 50, Smoothing = 1, Deadzone = -1 },
            Sticks = new StickMappingPair { Left = new StickMapping { Threshold = 1, Speed = 9 } },
        };

        Profile p = Normalize(m, null, out _);

        Binding b = p.Mapping.Buttons["cross"];
        Assert.Equal(10, b.Press.Amount);
        Assert.Equal(150, b.HoldMs);
        Assert.Equal(600, b.DoubleMs);
        Assert.Equal(1, b.TurboHz);
        Assert.Equal(5, p.Mapping.Touchpad.Sensitivity);
        Assert.Equal(0, p.Mapping.Touchpad.Acceleration);
        Assert.Equal(1, p.Mapping.Touchpad.ScrollSpeed);
        Assert.Equal(0.1, p.Mapping.GyroMouse.SensitivityX);
        Assert.Equal(10, p.Mapping.GyroMouse.SensitivityY);
        Assert.Equal(0.9, p.Mapping.GyroMouse.Smoothing);
        Assert.Equal(0, p.Mapping.GyroMouse.Deadzone);
        Assert.Equal(0.9, p.Mapping.Sticks.Left.Threshold);
        Assert.Equal(5, p.Mapping.Sticks.Left.Speed);
    }

    [Fact]
    public void Button_names_are_normalized_and_unknown_ones_dropped()
    {
        var m = new MappingConfig
        {
            LayerButton = "PS",
            Buttons = Bind.Map(("Cross", Bind.Key(Key.A)), ("paddleleft", Bind.Key(Key.B)), ("jump", Bind.Key(Key.C))),
            Layer = Bind.Map(("L1", Bind.Key(Key.D))),
            GyroMouse = new GyroMouseConfig { Button = "nope" },
        };

        Profile p = Normalize(m, null, out List<string> issues);

        Assert.Equal("ps", p.Mapping.LayerButton);
        Assert.Equal(["cross", "paddleLeft"], p.Mapping.Buttons.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["l1"], p.Mapping.Layer.Keys);
        Assert.Equal("r1", p.Mapping.GyroMouse.Button);
        Assert.Contains(issues, i => i.Contains("jump"));
        Assert.Equal(2, issues.Count);

        Assert.Equal("none", Normalize(new MappingConfig { LayerButton = "NONE" }, null, out _).Mapping.LayerButton);
        Assert.Equal("ps", Normalize(new MappingConfig { LayerButton = "x" }, null, out _).Mapping.LayerButton);
    }

    [Fact]
    public void Key_lists_are_cleaned()
    {
        var m = new MappingConfig
        {
            Buttons = Bind.Map(
                ("cross", Bind.Key(Key.None, Key.A, Key.A, Key.B, Key.C, Key.D, Key.E)),
                ("circle", Bind.Key()),
                ("square", Bind.Key((Key)0x7777))),
        };

        Profile p = Normalize(m, null, out List<string> issues);

        Assert.Equal([Key.A, Key.B, Key.C, Key.D], p.Mapping.Buttons["cross"].Press.Keys!);
        Assert.Equal(ActionKind.None, p.Mapping.Buttons["circle"].Press.Kind);
        Assert.Equal(ActionKind.None, p.Mapping.Buttons["square"].Press.Kind);
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void Unused_fields_are_dropped_and_hold_none_means_unset()
    {
        var m = new MappingConfig
        {
            Buttons = Bind.Map(("cross", new Binding
            {
                Press = new ActionConfig { Kind = ActionKind.Key, Keys = [Key.A], Button = MouseButton.Left, Path = @"C:\x.exe", Amount = 3 },
                Hold = ActionConfig.None,
                Double = new ActionConfig { Kind = ActionKind.MicMute, MacroId = "x" },
            })),
        };

        Binding b = Normalize(m, null, out _).Mapping.Buttons["cross"];

        Assert.Equal(new ActionConfig { Kind = ActionKind.Key, Keys = b.Press.Keys }, b.Press);
        Assert.Null(b.Hold);
        Assert.Equal(new ActionConfig { Kind = ActionKind.MicMute }, b.Double);
    }

    [Fact]
    public void Actions_with_bad_targets_become_none()
    {
        string macroId = Validation.NewId();
        var m = new MappingConfig
        {
            Buttons = Bind.Map(
                ("cross", new Binding { Press = Bind.Macro(Validation.NewId()) }),
                ("circle", new Binding { Press = Bind.Macro(macroId) }),
                ("square", new Binding { Press = new ActionConfig { Kind = ActionKind.Run, Path = "relative\\tool.exe" } }),
                ("triangle", new Binding { Press = new ActionConfig { Kind = ActionKind.Run, Path = @"C:\" + new string('a', 300) } }),
                ("l1", new Binding { Press = new ActionConfig { Kind = ActionKind.Profile, ProfileId = "sideways" } }),
                ("r1", new Binding { Press = new ActionConfig { Kind = ActionKind.Profile, ProfileId = "NEXT" } }),
                ("l2", new Binding { Press = new ActionConfig { Kind = ActionKind.Run, Path = @"C:\ok.exe", Args = new string('x', 600) } })),
        };

        Profile p = Normalize(m, [new Macro { Id = macroId, Name = "M" }], out List<string> issues);
        IReadOnlyDictionary<string, Binding> b = p.Mapping.Buttons;

        Assert.Equal(ActionKind.None, b["cross"].Press.Kind);
        Assert.Equal(macroId, b["circle"].Press.MacroId);
        Assert.Equal(ActionKind.None, b["square"].Press.Kind);
        Assert.Equal(ActionKind.None, b["triangle"].Press.Kind);
        Assert.Equal(ActionKind.None, b["l1"].Press.Kind);
        Assert.Equal("next", b["r1"].Press.ProfileId);
        Assert.Equal(512, b["l2"].Press.Args!.Length);
        Assert.Equal(5, issues.Count);
    }

    [Fact]
    public void Macro_limits_and_steps()
    {
        using var language = new TestLanguage(Language.Tr);
        string dup = Validation.NewId();
        Macro[] macros =
        [
            new Macro { Id = dup, Name = "  ", Steps = Enumerable.Repeat(new MacroStep { Op = MacroOp.Tap, Key = Key.A }, 600).ToArray() },
            new Macro { Id = dup, Name = new string('n', 80) },
            new Macro
            {
                Id = "bad",
                Name = "Steps",
                Repeat = (MacroRepeat)42,
                Steps =
                [
                    new MacroStep { Op = MacroOp.Down, Key = Key.None },
                    new MacroStep { Op = MacroOp.Delay, Ms = 100_000, Key = Key.A },
                    new MacroStep { Op = MacroOp.Wheel, Amount = 0 },
                ],
            },
            .. Enumerable.Range(0, 60).Select(i => new Macro { Id = Validation.NewId(), Name = $"M{i}" }),
        ];

        Profile p = Normalize(new MappingConfig(), macros, out List<string> issues);

        Assert.Equal(50, p.Macros.Count);
        Assert.Equal(500, p.Macros[0].Steps.Count);
        Assert.Equal("Makro", p.Macros[0].Name);
        Assert.Equal(64, p.Macros[1].Name.Length);
        Assert.NotEqual(p.Macros[0].Id, p.Macros[1].Id);
        Assert.True(Validation.IsId(p.Macros[2].Id));
        Assert.Equal(MacroRepeat.Once, p.Macros[2].Repeat);
        Assert.Equal(
            [new MacroStep { Op = MacroOp.Delay, Ms = 60_000 }, new MacroStep { Op = MacroOp.Wheel, Direction = WheelDirection.Up, Amount = 1 }],
            p.Macros[2].Steps);
        Assert.Equal(3, issues.Count);
    }

    [Fact]
    public void Zones_match_the_layout()
    {
        TouchpadMapping quad = Normalize(new MappingConfig { Touchpad = new TouchpadMapping { ZoneLayout = ZoneLayout.Quadrants } }, null, out _).Mapping.Touchpad;
        Assert.Equal(4, quad.Zones.Count);

        TouchpadMapping halves = Normalize(new MappingConfig
        {
            Touchpad = new TouchpadMapping { Zones = [Bind.Key(Key.A), Bind.Key(Key.B), Bind.Key(Key.C)] },
        }, null, out _).Mapping.Touchpad;
        Assert.Equal(2, halves.Zones.Count);
        Assert.Equal([Key.B], halves.Zones[1].Press.Keys!);
    }

    [Fact]
    public void Null_parts_from_json_do_not_throw()
    {
        const string json = """
            { "name": "X", "mapping": { "buttons": { "cross": null, "circle": { "press": null, "hold": { "kind": "teleport" } } },
              "touchpad": { "zones": [null], "swipes": null }, "gyroMouse": null, "sticks": { "left": null } },
              "macros": [null, { "steps": [null, { "op": "fly" }] }] }
            """;
        Profile raw = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!;
        Profile p = Validation.Normalize(raw);

        Assert.Equal(["circle"], p.Mapping.Buttons.Keys);
        Assert.Equal(ActionKind.None, p.Mapping.Buttons["circle"].Press.Kind);
        Assert.Null(p.Mapping.Buttons["circle"].Hold);
        Assert.Equal(2, p.Mapping.Touchpad.Zones.Count);
        Assert.Equal(GyroMouseMode.Off, p.Mapping.GyroMouse.Mode);
        Assert.Equal(Key.W, p.Mapping.Sticks.Left.Keys.Up);
        Macro macro = Assert.Single(p.Macros);
        // Unknown op reads as "down" without a key and is dropped.
        Assert.Empty(macro.Steps);
    }

    [Fact]
    public void Strip_run_actions_counts_every_place()
    {
        var run = new ActionConfig { Kind = ActionKind.Run, Path = @"C:\evil.exe" };
        var m = new MappingConfig
        {
            Buttons = Bind.Map(("cross", new Binding { Press = run, Hold = run })),
            Layer = Bind.Map(("cross", new Binding { Double = run })),
            Touchpad = new TouchpadMapping { Zones = [new Binding { Press = run }, Bind.Key(Key.A)], Swipes = new SwipeActions { Left = run } },
        };

        Profile p = Validation.StripRunActions(Normalize(m, null, out _), out int removed);

        Assert.Equal(5, removed);
        string json = JsonSerializer.Serialize(p, TactileJson.Options);
        Assert.DoesNotContain("\"run\"", json);
        Assert.Equal([Key.A], p.Mapping.Touchpad.Zones[1].Press.Keys!);
    }
}
