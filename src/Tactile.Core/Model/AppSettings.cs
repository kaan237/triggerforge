namespace Tactile.Core;

public enum Language { Tr, En }

public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public bool StartWithWindows { get; init; }
    public bool CloseToTray { get; init; } = true;
    public Language Language { get; init; } = Language.Tr;
    public bool AutoProfile { get; init; } = true;
    /// <summary>The foreground app must stay this long before the profile follows it.</summary>
    public int AutoProfileDelayMs { get; init; } = 800;
    public string? DefaultProfileId { get; init; }
    /// <summary>Controller MAC (upper case, colon separated) to profile id.</summary>
    public IReadOnlyDictionary<string, string> ControllerDefaults { get; init; } = new Dictionary<string, string>();
    /// <summary>0 = never. Bluetooth only.</summary>
    public int IdleSleepMinutes { get; init; } = 15;
    /// <summary>Battery percentages that raise a notice, highest first.</summary>
    public IReadOnlyList<int> LowBatteryWarnings { get; init; } = [20, 10];
    public bool RunAsAdminHint { get; init; }
}
