using System.Reflection;
using System.Text.Json;

namespace Tactile.Core;

/// <summary>
/// Turns the profile's Auto mode into a concrete pad kind for the game in front:
/// the user's per-exe overrides first, then the embedded list of native DualSense games, else Xbox 360.
/// </summary>
internal static class AutoEmulationResolver
{
    private const string ResourceName = "dualsense-games.json";

    // Windows shell and our own window: being in front of one of them says nothing about the game.
    private static readonly HashSet<string> Ambient = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "searchhost.exe", "searchapp.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "applicationframehost.exe", "textinputhost.exe", "lockapp.exe", "taskmgr.exe", "dwm.exe",
    };

    private static readonly string? OwnExe = Path.GetFileName(Environment.ProcessPath);

    public static IReadOnlySet<string> BuiltInDualSenseGames { get; } = Load();

    public static EmulationMode Resolve(EmulationMode configured, string? settledExe,
        IReadOnlyDictionary<string, EmulationMode>? gameModes)
    {
        if (configured != EmulationMode.Auto)
        {
            return configured;
        }

        if (NormalizeExe(settledExe) is not { } name)
        {
            return EmulationMode.Xbox360;
        }

        if (Override(gameModes, name) is { } chosen)
        {
            return chosen;
        }

        return BuiltInDualSenseGames.Contains(name) ? EmulationMode.DualSense : EmulationMode.Xbox360;
    }

    /// <summary>True for an exe in the built-in list or with a user override.</summary>
    public static bool IsKnownGame(string? exe, IReadOnlyDictionary<string, EmulationMode>? gameModes) =>
        NormalizeExe(exe) is { } name && (BuiltInDualSenseGames.Contains(name) || Override(gameModes, name) is not null);

    /// <summary>True for no window, the Windows shell and TriggerForge itself.</summary>
    public static bool IsAmbient(string? exe) =>
        NormalizeExe(exe) is not { } name || Ambient.Contains(name) ||
        string.Equals(name, OwnExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-case file name with an .exe suffix; null when nothing usable is left.</summary>
    public static string? NormalizeExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        string name = Path.GetFileName(exe.Trim()).Trim().ToLowerInvariant();
        if (name.Length == 0 || name == ".exe" || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    /// <summary>Only a concrete kind counts as an override; Auto and unknown values are ignored.</summary>
    public static bool IsOverrideMode(EmulationMode mode) =>
        mode is EmulationMode.Off or EmulationMode.Xbox360 or EmulationMode.DualSense or EmulationMode.DualShock4;

    private static EmulationMode? Override(IReadOnlyDictionary<string, EmulationMode>? gameModes, string name)
    {
        if (gameModes is null || gameModes.Count == 0)
        {
            return null;
        }

        if (gameModes.TryGetValue(name, out EmulationMode mode))
        {
            return IsOverrideMode(mode) ? mode : null;
        }

        // Keys that did not pass through Validation may still carry other casing or spaces.
        foreach ((string key, EmulationMode value) in gameModes)
        {
            if (IsOverrideMode(value) && NormalizeExe(key) == name)
            {
                return value;
            }
        }

        return null;
    }

    private static HashSet<string> Load()
    {
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using Stream? stream = typeof(AutoEmulationResolver).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                using JsonDocument doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("executables", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim().ToLowerInvariant() is { Length: > 0 } name)
                        {
                            games.Add(name);
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // A damaged list only means every game falls back to Xbox 360.
        }

        return games;
    }
}
