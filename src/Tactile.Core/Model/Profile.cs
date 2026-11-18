using Tactile.Device.DualSense;

namespace Tactile.Core;

public enum LightbarEffect { Off, Static, Breathe, Strobe, Rainbow, Battery, PlayerColor }

public enum PlayerLedMode { Off, Number, Custom }

public enum TriggerMode { Off, Feedback, Zones, Slope, Weapon, Vibration, ZonesVibration, Bow, Galloping, Machine }

public enum CurveKind { Linear, Exponential, Custom }

public enum StickShape { Round, Square }

public enum EmulationMode { Off, Xbox360, DualSense }

/// <summary>Everything one profile sets. File format: docs/contract.md.</summary>
public sealed record Profile
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Process file names, case-insensitive. Empty = manual only.</summary>
    public IReadOnlyList<string> Games { get; init; } = [];
    public LightbarConfig Lightbar { get; init; } = new();
    public PlayerLedConfig PlayerLeds { get; init; } = new();
    public MicLed MicLed { get; init; } = MicLed.Off;
    public TriggerPair Triggers { get; init; } = new();
    public TriggerResponsePair TriggerResponse { get; init; } = new();
    public StickPair Sticks { get; init; } = new();
    public GyroConfig Gyro { get; init; } = new();
    public RumbleConfig Rumble { get; init; } = new();
    /// <summary>Keyboard and mouse output (phase 3).</summary>
    public MappingConfig Mapping { get; init; } = new();
    /// <summary>At most 50.</summary>
    public IReadOnlyList<Macro> Macros { get; init; } = [];
    /// <summary>Virtual controller (phase 4).</summary>
    public EmulationConfig Emulation { get; init; } = new();
}

/// <summary>What the virtual controller looks like for this profile. File format: docs/contract-phase4.md.</summary>
public sealed record EmulationConfig
{
    public EmulationMode Mode { get; init; } = EmulationMode.Off;
    /// <summary>Source button name to target button name or "none". Unlisted buttons stay as they are.</summary>
    public IReadOnlyDictionary<string, string> Remap { get; init; } = new Dictionary<string, string>();
    public bool SwapSticks { get; init; }
    /// <summary>Buttons bound in <see cref="Profile.Mapping"/> are released on the virtual pad.</summary>
    public bool HideMappedButtons { get; init; } = true;
}

public sealed record LightbarConfig
{
    public LightbarEffect Effect { get; init; } = LightbarEffect.Static;
    public string Color { get; init; } = "#3060FF";
    /// <summary>Second colour for breathe and strobe.</summary>
    public string Color2 { get; init; } = "#000000";
    /// <summary>0.1-5, scales the effect's cycles per second.</summary>
    public double Speed { get; init; } = 1.0;
    public LedBrightness Brightness { get; init; } = LedBrightness.High;
}

public sealed record PlayerLedConfig
{
    public PlayerLedMode Mode { get; init; } = PlayerLedMode.Number;
    /// <summary>1-4.</summary>
    public int Number { get; init; } = 1;
    /// <summary>5 bits, bit 0 = leftmost LED.</summary>
    public int Mask { get; init; }
}

public sealed record TriggerConfig
{
    public TriggerMode Mode { get; init; } = TriggerMode.Off;
    /// <summary>Mode parameters by name (see the contract table). Values are rounded to integers.</summary>
    public IReadOnlyDictionary<string, double> Params { get; init; } = new Dictionary<string, double>();
    /// <summary>10 values 0-8, used by <see cref="TriggerMode.Zones"/> and <see cref="TriggerMode.ZonesVibration"/>.</summary>
    public int[]? Zones { get; init; }
    /// <summary>Hz, used by <see cref="TriggerMode.ZonesVibration"/>.</summary>
    public int Frequency { get; init; }

    public static TriggerConfig Off { get; } = new();
}

public sealed record TriggerPair
{
    public TriggerConfig Left { get; init; } = new();
    public TriggerConfig Right { get; init; } = new();
}

/// <summary>Analog L2/R2 shaping.</summary>
public sealed record TriggerResponse
{
    /// <summary>Raw values at or below this read as 0.</summary>
    public int Deadzone { get; init; }
    /// <summary>Raw values at or above this read as 255.</summary>
    public int Max { get; init; } = 255;
    public CurveKind Curve { get; init; } = CurveKind.Linear;
    /// <summary>0.3-4, for <see cref="CurveKind.Exponential"/>.</summary>
    public double Exponent { get; init; } = 1.0;
    /// <summary>5 [x, y] pairs for <see cref="CurveKind.Custom"/>.</summary>
    public double[][]? Points { get; init; }
}

public sealed record TriggerResponsePair
{
    public TriggerResponse Left { get; init; } = new();
    public TriggerResponse Right { get; init; } = new();
}

public sealed record StickConfig
{
    /// <summary>Fraction of travel from the centre that reads as 0.</summary>
    public double InnerDeadzone { get; init; } = 0.05;
    /// <summary>Fraction of travel at the edge that already reads as full.</summary>
    public double OuterDeadzone { get; init; } = 0.02;
    public StickShape Shape { get; init; } = StickShape.Round;
    public CurveKind Curve { get; init; } = CurveKind.Linear;
    public double Exponent { get; init; } = 1.0;
    public double[][]? Points { get; init; }
    /// <summary>Smallest output once outside the inner deadzone, for games with their own deadzone.</summary>
    public double AntiDeadzone { get; init; }
    public bool InvertX { get; init; }
    public bool InvertY { get; init; }
}

public sealed record StickPair
{
    public StickConfig Left { get; init; } = new();
    public StickConfig Right { get; init; } = new();
}

public sealed record GyroConfig
{
    public double Sensitivity { get; init; } = 1.0;
    public double Smoothing { get; init; } = 0.2;
    public bool InvertX { get; init; }
    public bool InvertY { get; init; }
}

public sealed record RumbleConfig
{
    /// <summary>0-2 multiplier.</summary>
    public double Strength { get; init; } = 1.0;
}
