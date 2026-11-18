using System.Text.Json;
using Tactile.Core;
using Tactile.Input;

namespace Tactile.Tests;

public class MappingValidationTests
{
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
