using System.Text.Json;
using System.Text.Json.Serialization;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

public enum ActionKind { None, Key, MouseButton, Wheel, Macro, Profile, Run, MicMute, Special }

public enum WheelDirection { Up, Down, Left, Right }

public enum SpecialAction { Screenshot, GameBar, TaskView, Desktop, ShowApp }

public enum TouchpadMode { Off, Mouse, Zones }

public enum ZoneLayout { Halves, Quadrants }

public enum GyroMouseMode { Off, Always, WhileHeld, Toggle, WhileReleased, WhileTouchpad }

public enum GyroAxis { Yaw, Roll }

public enum StickMappingMode { Off, Keys, Mouse, Scroll }

public enum MacroRepeat { Once, WhileHeld, Toggle }

public enum MacroOp { Down, Tap, Up, MouseDown, MouseUp, Click, Wheel, Delay }

/// <summary>Keyboard and mouse output for one profile. File format: docs/contract-phase3.md.</summary>
public sealed record MappingConfig
{
    /// <summary>Master switch; false = no keyboard or mouse output.</summary>
    public bool Enabled { get; init; }
    /// <summary>Button name or "none". While held, buttons use <see cref="Layer"/>.</summary>
    public string LayerButton { get; init; } = "ps";
    /// <summary>Base layer, keyed by camelCase button name.</summary>
    public IReadOnlyDictionary<string, Binding> Buttons { get; init; } = new Dictionary<string, Binding>();
    /// <summary>Second layer; a button missing here uses its base binding.</summary>
    public IReadOnlyDictionary<string, Binding> Layer { get; init; } = new Dictionary<string, Binding>();
    public TouchpadMapping Touchpad { get; init; } = new();
    public GyroMouseConfig GyroMouse { get; init; } = new();
    public StickMappingPair Sticks { get; init; } = new();
}

/// <summary>What one button (or touchpad zone) does: press, hold, double press, turbo, toggle.</summary>
public sealed record Binding
{
    public ActionConfig Press { get; init; } = ActionConfig.None;
    public ActionConfig? Hold { get; init; }
    public ActionConfig? Double { get; init; }
    public ActionConfig? Triple { get; init; }
    public IReadOnlyList<ActionConfig>? Cycle { get; init; }
    /// <summary>150-2000.</summary>
    public int HoldMs { get; init; } = 400;
    /// <summary>100-600.</summary>
    public int DoubleMs { get; init; } = 250;
    public bool Turbo { get; init; }
    /// <summary>1-30.</summary>
    public int TurboHz { get; init; } = 10;
    public bool Toggle { get; init; }
}

/// <summary>One action. <see cref="Kind"/> picks which of the optional fields are used.</summary>
public sealed record ActionConfig
{
    public static ActionConfig None { get; } = new();

    public ActionKind Kind { get; init; }

    /// <summary>key: 1-4 keys, pressed in order, released in reverse.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Key>? Keys { get; init; }

    /// <summary>mouseButton.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MouseButton? Button { get; init; }

    /// <summary>wheel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WheelDirection? Direction { get; init; }

    /// <summary>wheel: notches 1-10.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Amount { get; init; }

    /// <summary>macro: id of a macro in the same profile.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MacroId { get; init; }

    /// <summary>profile: an id, "next" or "previous".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfileId { get; init; }

    /// <summary>run: absolute path, at most 260 characters.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>run: at most 512 characters.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Args { get; init; }

    /// <summary>special.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpecialAction? Special { get; init; }

    /// <summary>A key chord action.</summary>
    public static ActionConfig ForKeys(params Key[] keys) => new() { Kind = ActionKind.Key, Keys = keys };

    /// <summary>A mouse button action.</summary>
    public static ActionConfig ForMouse(MouseButton button) => new() { Kind = ActionKind.MouseButton, Button = button };
}

public sealed record TouchpadMapping
{
    public TouchpadMode Mode { get; init; } = TouchpadMode.Off;
    /// <summary>0.1-5.</summary>
    public double Sensitivity { get; init; } = 1.0;
    /// <summary>0-2.</summary>
    public double Acceleration { get; init; } = 0.5;
    /// <summary>Mouse mode: two fingers scroll.</summary>
    public bool Scroll { get; init; } = true;
    /// <summary>0.1-5.</summary>
    public double ScrollSpeed { get; init; } = 1.0;
    /// <summary>Mouse mode: one-finger tap = left click, two-finger tap = right click.</summary>
    public bool TapToClick { get; init; } = true;
    public ZoneLayout ZoneLayout { get; init; } = ZoneLayout.Halves;
    /// <summary>Zones mode: the pad click inside each zone. 2 for halves, 4 for quadrants.</summary>
    public IReadOnlyList<Binding> Zones { get; init; } = [new(), new()];
    /// <summary>Zones mode only.</summary>
    public SwipeActions Swipes { get; init; } = new();
}

public sealed record SwipeActions
{
    public ActionConfig Up { get; init; } = ActionConfig.None;
    public ActionConfig Down { get; init; } = ActionConfig.None;
    public ActionConfig Left { get; init; } = ActionConfig.None;
    public ActionConfig Right { get; init; } = ActionConfig.None;
}

public sealed record GyroMouseConfig
{
    public GyroMouseMode Mode { get; init; } = GyroMouseMode.Off;
    /// <summary>For whileHeld and toggle; the button keeps its own binding too.</summary>
    public string Button { get; init; } = "r1";
    /// <summary>Which rotation moves the cursor sideways.</summary>
    public GyroAxis Axis { get; init; } = GyroAxis.Yaw;
    /// <summary>0.1-10; 1 = 10 px per degree.</summary>
    public double SensitivityX { get; init; } = 1.0;
    public double SensitivityY { get; init; } = 1.0;
    public bool InvertX { get; init; }
    public bool InvertY { get; init; }
    /// <summary>0-0.9.</summary>
    public double Smoothing { get; init; } = 0.2;
    /// <summary>deg/s, 0-5.</summary>
    public double Deadzone { get; init; } = 0.5;
}

public sealed record StickMappingPair
{
    public StickMapping Left { get; init; } = new();
    public StickMapping Right { get; init; } = new();
}

public sealed record StickMapping
{
    public StickMappingMode Mode { get; init; } = StickMappingMode.Off;
    public StickKeys Keys { get; init; } = new();
    /// <summary>Keys mode, 0.1-0.9, per axis on the processed value.</summary>
    public double Threshold { get; init; } = 0.5;
    /// <summary>0.1-5. Mouse: full tilt = 1200 px/s × speed; scroll: 15 notches/s × speed.</summary>
    public double Speed { get; init; } = 1.0;
}

public sealed record StickKeys
{
    public Key Up { get; init; } = Key.W;
    public Key Down { get; init; } = Key.S;
    public Key Left { get; init; } = Key.A;
    public Key Right { get; init; } = Key.D;
}

public sealed record Macro
{
    public string Id { get; init; } = "";
    /// <summary>At most 64 characters.</summary>
    public string Name { get; init; } = "";
    public MacroRepeat Repeat { get; init; } = MacroRepeat.Once;
    /// <summary>At most 500.</summary>
    public IReadOnlyList<MacroStep> Steps { get; init; } = [];
}

public sealed record MacroStep
{
    public MacroOp Op { get; init; }

    /// <summary>down, tap, up.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Key? Key { get; init; }

    /// <summary>mouseDown, mouseUp, click.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MouseButton? Button { get; init; }

    /// <summary>wheel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WheelDirection? Direction { get; init; }

    /// <summary>wheel: notches 1-10.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Amount { get; init; }

    /// <summary>delay: 0-60000.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ms { get; init; }
}

/// <summary>Button names used in mapping files: the <see cref="Buttons"/> members in camelCase.</summary>
public static class ButtonNames
{
    /// <summary>"No button", allowed for <see cref="MappingConfig.LayerButton"/>.</summary>
    public const string None = "none";

    private static readonly (Buttons Flag, string Name)[] Table = Enum.GetValues<Buttons>()
        .Where(b => b != Buttons.None)
        .Select(b => (b, JsonNamingPolicy.CamelCase.ConvertName(b.ToString())))
        .ToArray();

    /// <summary>Every single-button flag, in enum order.</summary>
    public static IReadOnlyList<Buttons> All { get; } = Table.Select(t => t.Flag).ToArray();

    /// <summary>camelCase name of a single button flag, or "none".</summary>
    public static string Name(Buttons button)
    {
        foreach ((Buttons flag, string name) in Table)
        {
            if (flag == button)
            {
                return name;
            }
        }

        return None;
    }

    /// <summary>Case-insensitive. "none" is not a button.</summary>
    public static bool TryParse(string? name, out Buttons button)
    {
        foreach ((Buttons flag, string n) in Table)
        {
            if (string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                button = flag;
                return true;
            }
        }

        button = Buttons.None;
        return false;
    }
}
