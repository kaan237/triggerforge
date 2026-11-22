using System.Text.Json.Serialization;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public bool StartWithWindows { get; init; }
    public bool CloseToTray { get; init; } = true;
    public Language Language { get; init; } = Language.En;
    public bool AutoProfile { get; init; } = true;
    /// <summary>The foreground app must stay this long before the profile follows it.</summary>
    public int AutoProfileDelayMs { get; init; } = 800;
    public string? DefaultProfileId { get; init; }
    /// <summary>Controller MAC (upper case, colon separated) to profile id.</summary>
    public IReadOnlyDictionary<string, string> ControllerDefaults { get; init; } = new Dictionary<string, string>();
    public string? PrimaryController { get; init; }
    public bool AutoPlayerLeds { get; init; } = true;
    public bool EdgeShortcuts { get; init; } = true;
    public int MaxControllers { get; init; } = 4;
    /// <summary>0 = never. Bluetooth only.</summary>
    public int IdleSleepMinutes { get; init; } = 15;
    /// <summary>Battery percentages that raise a notice, highest first.</summary>
    public IReadOnlyList<int> LowBatteryWarnings { get; init; } = [20, 10];
    public bool RunAsAdminHint { get; init; }
    public EmulationSettings Emulation { get; init; } = new();
    public IntegrationSettings Integration { get; init; } = new();
    public UpdateSettings Updates { get; init; } = new();
    public bool FirstRunDone { get; init; }
    public IReadOnlyList<string> PauseCombo { get; init; } = [];
    public IReadOnlyList<Key> PauseHotkey { get; init; } = [];
    public bool PauseOnFullscreen { get; init; }
    public int BackupCount { get; init; } = 7;
}

public sealed record UpdateSettings
{
    public bool AutoCheck { get; init; } = true;
}

public sealed record IntegrationSettings
{
    public bool UdpEnabled { get; init; }
    public int UdpPort { get; init; } = 6969;
    public bool Dsx { get; init; } = true;
    public bool DsxPortFile { get; init; } = true;
    public int ReleaseAfterSeconds { get; init; }
    public bool ForzaEnabled { get; init; }
    public int ForzaPort { get; init; } = 5555;
    public bool DsuEnabled { get; init; }
    public int DsuPort { get; init; } = 26760;
}

public sealed record EmulationSettings
{
    /// <summary>Master switch; false never attaches or hides anything.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Hide the real controller from other apps while a virtual pad is attached.</summary>
    public bool HideController { get; init; } = true;
    public bool Audio { get; init; } = true;
    public bool KeepAudioDefaults { get; init; } = true;
    public int HapticBuffer { get; init; } = 64;
    // 0x39 gave no sound on our controller in the 2026-11-19 hardware test; it stays an experimental option.
    public SpeakerPacket SpeakerPacket { get; init; } = SpeakerPacket.Single36;
    /// <summary>Lower-case exe file name to the pad kind Auto uses for it (never Auto). At most 256.</summary>
    [JsonConverter(typeof(GameModesConverter))]
    public IReadOnlyDictionary<string, EmulationMode> GameModes { get; init; } = new Dictionary<string, EmulationMode>();

    // Compared by content, so equal settings stay equal across separately built dictionaries.
    public bool Equals(EmulationSettings? other) =>
        other is not null && Enabled == other.Enabled && HideController == other.HideController && Audio == other.Audio &&
        KeepAudioDefaults == other.KeepAudioDefaults && HapticBuffer == other.HapticBuffer &&
        SpeakerPacket == other.SpeakerPacket && SameModes(GameModes, other.GameModes);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, HideController, Audio, KeepAudioDefaults, HapticBuffer, SpeakerPacket, GameModes?.Count ?? 0);

    private static bool SameModes(IReadOnlyDictionary<string, EmulationMode>? a, IReadOnlyDictionary<string, EmulationMode>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if ((a?.Count ?? 0) != (b?.Count ?? 0))
        {
            return false;
        }

        return a is null || b is null || a.All(pair => b.TryGetValue(pair.Key, out EmulationMode mode) && mode == pair.Value);
    }
}
