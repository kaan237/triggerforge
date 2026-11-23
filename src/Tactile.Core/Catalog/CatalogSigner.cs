using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tactile.Core.Catalog;

/// <summary>
/// Signs and checks a catalog repository folder: index.json, index.json.sig and profiles/*.json.
/// Every failure is an <see cref="InvalidDataException"/> or an I/O error.
/// </summary>
public static class CatalogSigner
{
    public const string IndexName = "index.json";
    public const string SignatureName = "index.json.sig";

    /// <summary>
    /// Refreshes size, hash and summary of every entry, raises the revision, stamps the date and signs.
    /// Refuses a profile with a run action, a path outside the folder or a malformed index.
    /// </summary>
    public static void Sign(string repoDir, ECDsa key, DateTime utcNow)
    {
        if (key.KeySize != 256)
        {
            throw new InvalidDataException("The catalog key must be ECDSA P-256.");
        }

        string indexPath = Path.Combine(repoDir, IndexName);
        byte[] body;
        try
        {
            byte[] source = File.ReadAllBytes(indexPath);
            if (CatalogProfileFile.HasBom(source))
            {
                throw new InvalidDataException("index.json must not start with a byte order mark.");
            }

            JsonObject root = JsonNode.Parse(source, null,
                new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                ?? throw new InvalidDataException("index.json must be an object.");
            JsonArray profiles = root["profiles"] as JsonArray ?? throw new InvalidDataException("index.json has no profiles list.");
            foreach (JsonNode? node in profiles)
            {
                JsonObject entry = node as JsonObject ?? throw new InvalidDataException("A catalog entry must be an object.");
                string path = entry["path"]?.GetValue<string>() ?? throw new InvalidDataException("A catalog entry has no path.");
                (byte[] bytes, CatalogProfileSummary summary) = ReadProfile(repoDir, path);
                entry["size"] = bytes.Length;
                entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                entry["summary"] = JsonSerializer.SerializeToNode(summary, TactileJson.Options);
            }

            long revision = 0;
            if (root.ContainsKey("revision"))
            {
                revision = root["revision"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
                    value.TryGetValue(out long previous) && previous >= 0
                    ? previous : throw new InvalidDataException("The revision must be a whole number of 0 or more.");
            }

            root.Remove("profiles");
            root.Remove("revision");
            root.Remove("updated");
            root["revision"] = revision + 1;
            root["updated"] = utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            root["profiles"] = profiles;
            body = Encoding.UTF8.GetBytes(root.ToJsonString(CatalogShareExporter.IndexOptions) + "\n");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("index.json is not a valid catalog: " + e.Message, e);
        }

        if (body.Length > CatalogManifest.IndexLimit)
        {
            throw new InvalidDataException("index.json is larger than the client accepts.");
        }

        try
        {
            CatalogManifest.Parse(body);
        }
        catch (CatalogException e)
        {
            throw new InvalidDataException("index.json breaks the catalog rules: " + e.Message, e);
        }

        byte[] signature = key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllBytes(indexPath, body);
        File.WriteAllText(Path.Combine(repoDir, SignatureName), Convert.ToBase64String(signature));
    }

    /// <summary>Checks the signature, the schema and every profile file against its entry.</summary>
    public static int Verify(string repoDir, byte[] publicKey)
    {
        byte[] body = File.ReadAllBytes(Path.Combine(repoDir, IndexName));
        string signature = File.ReadAllText(Path.Combine(repoDir, SignatureName)).Trim();
        if (body.Length > CatalogManifest.IndexLimit)
        {
            throw new InvalidDataException("index.json is larger than the client accepts.");
        }

        CatalogManifest manifest;
        try
        {
            manifest = CatalogManifest.Verify(body, signature, publicKey);
        }
        catch (CatalogException e)
        {
            throw new InvalidDataException($"The catalog does not verify ({e.Code}).", e);
        }

        foreach (CatalogEntry entry in manifest.Profiles)
        {
            (byte[] bytes, CatalogProfileSummary summary) = ReadProfile(repoDir, entry.Path);
            if (bytes.Length != entry.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), entry.Sha256) ||
                summary != entry.Summary)
            {
                throw new InvalidDataException($"{entry.Path} does not match its catalog entry.");
            }
        }

        return manifest.Profiles.Count;
    }

    private static (byte[] Bytes, CatalogProfileSummary Summary) ReadProfile(string repoDir, string path)
    {
        if (!CatalogManifest.IsValidPath(path))
        {
            throw new InvalidDataException($"Invalid profile path: {path}");
        }

        string root = Path.GetFullPath(repoDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The profile path leaves the repository folder: {path}");
        }

        for (string? current = full; current is not null && current.Length > root.Length; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"The profile path goes through a link: {path}");
                }
            }
        }

        var file = new FileInfo(full);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Profile file not found: {path}", full);
        }

        if (file.Length is < 1 or > CatalogManifest.MaxProfileSize)
        {
            throw new InvalidDataException($"{path} must be between 1 byte and 256 KB.");
        }

        byte[] bytes = File.ReadAllBytes(full);
        try
        {
            if (CatalogProfileFile.ContainsRunAction(bytes))
            {
                throw new InvalidDataException($"{path} contains a run action; it cannot be published.");
            }

            Profile profile = JsonSerializer.Deserialize<Profile>(CatalogProfileFile.DecodeStrict(bytes), TactileJson.Options)
                ?? throw new InvalidDataException($"{path} is not a profile.");
            profile = Validation.StripRunActions(Validation.Normalize(profile), out int removed);
            if (removed != 0)
            {
                throw new InvalidDataException($"{path} contains a run action; it cannot be published.");
            }

            return (bytes, CatalogProfileSummary.FromProfile(profile));
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException($"{path} is not a valid profile file: {e.Message}", e);
        }
    }
}
