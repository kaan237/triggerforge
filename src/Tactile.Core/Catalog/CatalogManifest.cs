using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tactile.Core.Catalog;

public enum CatalogController { DualSense, Edge, Any }

public sealed record CatalogProfileSummary(
    TriggerMode LeftTrigger, TriggerMode RightTrigger, string Emulation, bool MappingEnabled, int LayerCount,
    int ChordCount, int MacroCount, GyroMouseMode GyroMouse, GyroMouseMode GyroStick, bool FlickStick,
    bool AudioHaptics, int TelemetryRules)
{
    internal static readonly string[] Fields =
    [
        "leftTrigger", "rightTrigger", "emulation", "mappingEnabled", "layerCount", "chordCount", "macroCount",
        "gyroMouse", "gyroStick", "flickStick", "audioHaptics", "telemetryRules",
    ];

    private static readonly string[] Emulations = ["off", "xbox360", "dualSense", "dualShock4", "auto"];

    public static CatalogProfileSummary FromProfile(Profile p)
    {
        MappingConfig mapping = p.Mapping ?? new();
        int layers = mapping.Layers is { Count: > 0 } ? mapping.Layers.Count : mapping.Layer is { Count: > 0 } ? 1 : 0;
        return new CatalogProfileSummary(
            p.Triggers?.Left?.Mode ?? TriggerMode.Off,
            p.Triggers?.Right?.Mode ?? TriggerMode.Off,
            JsonNamingPolicy.CamelCase.ConvertName((p.Emulation?.Mode ?? EmulationMode.Off).ToString()),
            mapping.Enabled,
            layers,
            mapping.Chords?.Count ?? 0,
            p.Macros?.Count ?? 0,
            mapping.GyroMouse?.Mode ?? GyroMouseMode.Off,
            p.Emulation?.GyroStick?.Mode ?? GyroMouseMode.Off,
            mapping.FlickStick?.Enabled ?? false,
            (p.Haptics?.Audio ?? AudioHapticSource.Off) != AudioHapticSource.Off,
            p.Telemetry?.Rules?.Count ?? 0);
    }

    /// <summary>Reads the summary object with its exact field names and value kinds. Null when anything is off.</summary>
    internal static CatalogProfileSummary? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int count = 0;
        foreach (JsonProperty property in e.EnumerateObject())
        {
            count++;
            if (Array.IndexOf(Fields, property.Name) < 0)
            {
                return null;
            }
        }

        if (count != Fields.Length)
        {
            return null;
        }

        if (!ReadEnum(e, "leftTrigger", out TriggerMode left) || !ReadEnum(e, "rightTrigger", out TriggerMode right) ||
            !ReadEnum(e, "gyroMouse", out GyroMouseMode gyroMouse) || !ReadEnum(e, "gyroStick", out GyroMouseMode gyroStick) ||
            e.GetProperty("emulation").ValueKind != JsonValueKind.String ||
            !Bool(e, "mappingEnabled", out bool mapping) || !Bool(e, "flickStick", out bool flick) ||
            !Bool(e, "audioHaptics", out bool audio) ||
            !Int(e, "layerCount", out int layers) || !Int(e, "chordCount", out int chords) ||
            !Int(e, "macroCount", out int macros) || !Int(e, "telemetryRules", out int rules))
        {
            return null;
        }

        var summary = new CatalogProfileSummary(left, right, e.GetProperty("emulation").GetString()!, mapping, layers,
            chords, macros, gyroMouse, gyroStick, flick, audio, rules);
        return summary.IsValid() ? summary : null;
    }

    private static bool ReadEnum<T>(JsonElement e, string name, out T value) where T : struct, Enum
    {
        value = default;
        JsonElement v = e.GetProperty(name);
        string? text = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        // Exactly the camelCase names the app writes; nothing Enum.TryParse would tolerate beyond that.
        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(text, JsonNamingPolicy.CamelCase.ConvertName(candidate.ToString()), StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool Bool(JsonElement e, string name, out bool value)
    {
        JsonElement v = e.GetProperty(name);
        value = v.ValueKind == JsonValueKind.True;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool Int(JsonElement e, string name, out int value)
    {
        value = 0;
        JsonElement v = e.GetProperty(name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    internal bool IsValid() =>
        Array.IndexOf(Emulations, Emulation) >= 0 &&
        LayerCount is >= 0 and <= 100 && ChordCount is >= 0 and <= 1000 && MacroCount is >= 0 and <= 1000 &&
        TelemetryRules is >= 0 and <= 1000;
}

public sealed record CatalogEntry(
    string Id, string Name, IReadOnlyList<string> Games, CatalogController Controller, IReadOnlyList<string> Tags,
    string Author, string Version, string Path, byte[] Sha256, string Sha256Hex, long Size,
    LocalizedMessage Description, CatalogProfileSummary Summary)
{
    /// <summary>The value used on the wire and in index.json.</summary>
    public string ControllerName => Controller switch
    {
        CatalogController.DualSense => "dualsense",
        CatalogController.Edge => "edge",
        _ => "any",
    };
}

public sealed record CatalogManifest(string Updated, long Revision, IReadOnlyList<CatalogEntry> Profiles)
{
    public const int MaxProfiles = 500;
    public const long MaxProfileSize = 262_144;
    public const int IndexLimit = 512 * 1024;

    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]\z", RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern = new(@"^profiles/[a-z0-9][a-z0-9._-]{0,80}\.json\z", RegexOptions.CultureInvariant);

    internal static bool IsValidPath(string path) =>
        PathPattern.IsMatch(path) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\');

    /// <summary>Checks the signature first; the body is not parsed until it verified.</summary>
    internal static CatalogManifest Verify(byte[] body, string signature, byte[] publicKey)
    {
        Span<byte> bytes = stackalloc byte[64];
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
            if (consumed != publicKey.Length || key.KeySize != 256 ||
                !Convert.TryFromBase64String(signature, bytes, out int written) || written != bytes.Length ||
                !key.VerifyData(body, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new CatalogException(CatalogError.BadSignature, "Catalog signature did not verify.");
            }
        }
        catch (CryptographicException e)
        {
            throw new CatalogException(CatalogError.BadSignature, "Catalog signature could not be verified.", e);
        }

        return Parse(body);
    }

    internal static CatalogManifest Parse(byte[] body)
    {
        if (CatalogProfileFile.HasBom(body))
        {
            throw new CatalogException(CatalogError.BadManifest, "The catalog must not start with a byte order mark.");
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement root = json.RootElement;
            string updated = RequiredString(root, "updated");
            long revision = root.GetProperty("revision").GetInt64();
            if (root.GetProperty("schema").GetInt32() != 1 || revision < 1 ||
                !DateOnly.TryParseExact(updated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                throw new CatalogException(CatalogError.BadManifest, "Invalid catalog schema, revision or date.");
            }

            JsonElement profiles = root.GetProperty("profiles");
            if (profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() > MaxProfiles)
            {
                throw new CatalogException(CatalogError.BadManifest, "Invalid catalog profile list.");
            }

            var entries = new List<CatalogEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in profiles.EnumerateArray())
            {
                CatalogEntry entry = ReadEntry(item);
                if (!ids.Add(entry.Id))
                {
                    throw new CatalogException(CatalogError.BadManifest, "Duplicate catalog id.");
                }

                entries.Add(entry);
            }

            return new CatalogManifest(updated, revision, entries);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CatalogException(CatalogError.BadManifest, "Invalid catalog fields.", e);
        }
    }

    private static CatalogEntry ReadEntry(JsonElement e)
    {
        string id = RequiredString(e, "id");
        string name = RequiredString(e, "name");
        string author = RequiredString(e, "author");
        string version = RequiredString(e, "version");
        string path = RequiredString(e, "path");
        string sha256 = RequiredString(e, "sha256");
        long size = e.GetProperty("size").GetInt64();
        CatalogController controller = RequiredString(e, "controller") switch
        {
            "dualsense" => CatalogController.DualSense,
            "edge" => CatalogController.Edge,
            "any" => CatalogController.Any,
            _ => throw new CatalogException(CatalogError.BadManifest, "Invalid catalog controller."),
        };
        string[] games = StringArray(e, "games", 16, IsExeName);
        string[] tags = StringArray(e, "tags", 8, t => t.Length is >= 1 and <= 24);
        JsonElement description = e.GetProperty("description");
        string en = RequiredString(description, "en");
        string tr = RequiredString(description, "tr");
        CatalogProfileSummary? summary = CatalogProfileSummary.Read(e.GetProperty("summary"));
        if (!IdPattern.IsMatch(id) || string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            string.IsNullOrWhiteSpace(author) || author.Length > 40 ||
            version.Length == 0 || version.Any(c => !char.IsAsciiDigit(c) && c != '.') ||
            !System.Version.TryParse(version, out _) ||
            !IsValidPath(path) || size is < 1 or > MaxProfileSize ||
            sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit) ||
            en.Length is < 1 or > 500 || tr.Length is < 1 or > 500 ||
            summary is null)
        {
            throw new CatalogException(CatalogError.BadManifest, "Invalid catalog entry.");
        }

        return new CatalogEntry(id, name, games, controller, tags, author, version, path,
            Convert.FromHexString(sha256), sha256.ToLowerInvariant(), size, new LocalizedMessage(en, tr), summary);
    }

    private static bool IsExeName(string g) =>
        g.Length is >= 5 and <= 128 && g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        g.IndexOfAny(['/', '\\', ':']) < 0 && System.IO.Path.GetFileName(g) == g;

    private static string[] StringArray(JsonElement parent, string name, int max, Func<string, bool> valid)
    {
        JsonElement array = parent.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > max)
        {
            throw new CatalogException(CatalogError.BadManifest, $"Invalid {name}.");
        }

        string[] items = array.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new CatalogException(CatalogError.BadManifest, $"Invalid {name}."))
            .ToArray();
        if (!items.All(valid))
        {
            throw new CatalogException(CatalogError.BadManifest, $"Invalid {name}.");
        }

        return items;
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new CatalogException(CatalogError.BadManifest, $"Missing {name}.");
}
