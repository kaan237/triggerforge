using System.Text.Json;
using Tactile.Core;
using Tactile.Input;

namespace Tactile.Tests;

public class MappingJsonTests
{
    private const string Full = """
        {
          "version": 1,
          "id": "3f2a00000000000000000000000000bb",
          "name": "Mapped",
          "mapping": {
            "enabled": true,
            "layerButton": "l1",
            "buttons": {
              "cross": { "press": { "kind": "key", "keys": ["leftCtrl", "c"] }, "hold": { "kind": "mouseButton", "button": "x1" },
                         "double": null, "holdMs": 500, "doubleMs": 200, "turbo": false, "turboHz": 12, "toggle": true },
              "circle": { "press": { "kind": "wheel", "direction": "left", "amount": 3 } },
              "square": { "press": { "kind": "macro", "macroId": "3f2a00000000000000000000000000cc" } },
              "triangle": { "press": { "kind": "run", "path": "C:\\Tools\\obs.exe", "args": "--x" } },
              "options": { "press": { "kind": "profile", "profileId": "next" } },
              "mic": { "press": { "kind": "micMute" } },
              "create": { "press": { "kind": "special", "special": "screenshot" } }
            },
            "layer": { "cross": { "press": { "kind": "key", "keys": ["numpadEnter"] } } },
            "touchpad": {
              "mode": "zones", "sensitivity": 1.5, "acceleration": 0.2, "scroll": false, "scrollSpeed": 2,
              "tapToClick": false, "zoneLayout": "quadrants",
              "zones": [ { "press": { "kind": "key", "keys": ["digit1"] } }, { "press": { "kind": "key", "keys": ["f1"] } },
                         { "press": { "kind": "key", "keys": ["intlBackslash"] } }, { "press": { "kind": "key", "keys": ["backquote"] } } ],
              "swipes": { "up": { "kind": "key", "keys": ["volumeUp"] }, "down": { "kind": "none" },
                          "left": { "kind": "special", "special": "taskView" }, "right": { "kind": "special", "special": "showApp" } }
            },
            "gyroMouse": { "mode": "whileHeld", "button": "r2", "axis": "roll", "sensitivityX": 2, "sensitivityY": 3,
                           "invertX": true, "invertY": false, "smoothing": 0.5, "deadzone": 1 },
            "sticks": {
              "left": { "mode": "keys", "keys": { "up": "up", "down": "down", "left": "none", "right": "right" }, "threshold": 0.3, "speed": 1 },
              "right": { "mode": "mouse", "speed": 2.5 }
            }
          },
          "macros": [
            { "id": "3f2a00000000000000000000000000cc", "name": "Combo 1", "repeat": "whileHeld",
              "steps": [ { "op": "down", "key": "leftShift" }, { "op": "tap", "key": "a" }, { "op": "up", "key": "leftShift" },
                         { "op": "mouseDown", "button": "left" }, { "op": "mouseUp", "button": "left" },
                         { "op": "click", "button": "right" }, { "op": "wheel", "direction": "down", "amount": 1 },
                         { "op": "delay", "ms": 50 } ] }
          ]
        }
        """;

    [Fact]
    public void Reads_the_full_mapping_sample()
    {
        Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(Full, TactileJson.Options)!);
        MappingConfig m = p.Mapping;

        Assert.True(m.Enabled);
        Assert.Equal("l1", m.LayerButton);
        Assert.Equal([Key.LeftCtrl, Key.C], m.Buttons["cross"].Press.Keys!);
        Assert.Equal(MouseButton.X1, m.Buttons["cross"].Hold!.Button);
        Assert.True(m.Buttons["cross"].Toggle);
        Assert.Equal(WheelDirection.Left, m.Buttons["circle"].Press.Direction);
        Assert.Equal(@"C:\Tools\obs.exe", m.Buttons["triangle"].Press.Path);
        Assert.Equal(SpecialAction.Screenshot, m.Buttons["create"].Press.Special);
        Assert.Equal([Key.NumpadEnter], m.Layer["cross"].Press.Keys!);
        Assert.Equal(TouchpadMode.Zones, m.Touchpad.Mode);
        Assert.Equal([Key.IntlBackslash], m.Touchpad.Zones[2].Press.Keys!);
        Assert.Equal(SpecialAction.ShowApp, m.Touchpad.Swipes.Right.Special);
        Assert.Equal(GyroAxis.Roll, m.GyroMouse.Axis);
        Assert.Equal("r2", m.GyroMouse.Button);
        Assert.Equal(Key.None, m.Sticks.Left.Keys.Left);
        Assert.Equal(Key.Up, m.Sticks.Left.Keys.Up);
        Assert.Equal(StickMappingMode.Mouse, m.Sticks.Right.Mode);
        Macro macro = Assert.Single(p.Macros);
        Assert.Equal(MacroRepeat.WhileHeld, macro.Repeat);
        Assert.Equal(8, macro.Steps.Count);
        Assert.Equal(m.Buttons["square"].Press.MacroId, macro.Id);
    }

    [Fact]
    public void Round_trips_a_full_mapping_profile()
    {
        Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(Full, TactileJson.Options)!);
        string once = JsonSerializer.Serialize(p, TactileJson.Options);
        Profile again = Validation.Normalize(JsonSerializer.Deserialize<Profile>(once, TactileJson.Options)!);
        string twice = JsonSerializer.Serialize(again, TactileJson.Options);

        Assert.Equal(once, twice);
        Assert.Contains("\"leftCtrl\"", once);
        Assert.Contains("\"kind\": \"micMute\"", once);
        // Fields a kind does not use are not written.
        Assert.DoesNotContain("\"macroId\": null", once);
    }

    [Fact]
    public void Old_profile_without_mapping_loads_with_defaults()
    {
        const string old = """{ "version": 1, "name": "Phase 2", "lightbar": { "effect": "static" } }""";
        Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(old, TactileJson.Options)!);

        Assert.False(p.Mapping.Enabled);
        Assert.Equal("ps", p.Mapping.LayerButton);
        Assert.Empty(p.Mapping.Buttons);
        Assert.Empty(p.Mapping.Layer);
        Assert.Equal(TouchpadMode.Off, p.Mapping.Touchpad.Mode);
        Assert.Equal(ZoneLayout.Halves, p.Mapping.Touchpad.ZoneLayout);
        Assert.Equal(2, p.Mapping.Touchpad.Zones.Count);
        Assert.True(p.Mapping.Touchpad.TapToClick);
        Assert.Equal(GyroMouseMode.Off, p.Mapping.GyroMouse.Mode);
        Assert.Equal("r1", p.Mapping.GyroMouse.Button);
        Assert.Equal(StickMappingMode.Off, p.Mapping.Sticks.Left.Mode);
        Assert.Equal(new StickKeys { Up = Key.W, Down = Key.S, Left = Key.A, Right = Key.D }, p.Mapping.Sticks.Right.Keys);
        Assert.Empty(p.Macros);
    }

    [Fact]
    public void Unknown_enum_values_read_as_defaults()
    {
        const string json = """
            { "name": "X", "mapping": { "touchpad": { "mode": "hologram" },
              "buttons": { "cross": { "press": { "kind": "key", "keys": ["a", "superKey"] } } } } }
            """;
        Profile p = Validation.Normalize(JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!);

        Assert.Equal(TouchpadMode.Off, p.Mapping.Touchpad.Mode);
        Assert.Equal([Key.A], p.Mapping.Buttons["cross"].Press.Keys!);
    }

    [Fact]
    public void Import_replaces_run_actions_and_counts_them()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);

        Profile imported = store.Import(Full, out int removed);

        Assert.Equal(1, removed);
        Assert.Equal(ActionKind.None, imported.Mapping.Buttons["triangle"].Press.Kind);
        Assert.Equal(ActionKind.Key, imported.Mapping.Buttons["cross"].Press.Kind);
        Assert.NotEqual("3f2a00000000000000000000000000bb", imported.Id);
        Assert.Equal(imported, store.Get(imported.Id));

        Profile plain = store.Import(Full);
        Assert.Equal(ActionKind.None, plain.Mapping.Buttons["triangle"].Press.Kind);
    }
}
