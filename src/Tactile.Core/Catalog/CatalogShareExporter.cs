using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tactile.Core.Catalog;

/// <summary>What the UI shows after "export for sharing".</summary>
public sealed record CatalogSharePackage(string Json, string FileName, string EntryJson, int RemovedRunActions);

public static class CatalogShareExporter
{
    private static readonly JsonSerializerOptions FileOptions = new(TactileJson.Options) { NewLine = "\n" };

    internal static readonly JsonSerializerOptions IndexOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Strips every run action and prepares the file and an index.json entry draft with its exact size and SHA-256.</summary>
    public static CatalogSharePackage BuildSharePackage(Profile profile)
    {
        Profile cleaned = Validation.StripRunActions(profile with { Id = "" }, out int removed);
        string json = JsonSerializer.Serialize(cleaned, FileOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string slug = Slug(cleaned.Name);
        var entry = new JsonObject
        {
            ["id"] = slug,
            ["name"] = cleaned.Name,
            ["games"] = new JsonArray((cleaned.Games ?? []).Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
            ["controller"] = "any",
            ["tags"] = new JsonArray(),
            ["author"] = "your-name",
            ["version"] = "1.0.0",
            ["path"] = $"profiles/{slug}.json",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["size"] = bytes.Length,
            ["description"] = new JsonObject { ["en"] = "Describe this profile.", ["tr"] = "Bu profili anlatın." },
            ["summary"] = JsonSerializer.SerializeToNode(CatalogProfileSummary.FromProfile(Validation.Normalize(cleaned)), TactileJson.Options),
        };
        return new CatalogSharePackage(json, slug + ".json", entry.ToJsonString(IndexOptions), removed);
    }

    internal static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (char c in (name ?? "").ToLowerInvariant())
        {
            char mapped = c switch
            {
                'ı' or 'İ' => 'i', 'ç' => 'c', 'ğ' => 'g', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                _ => c,
            };
            if (char.IsAsciiLetterLower(mapped) || char.IsAsciiDigit(mapped))
            {
                text.Append(mapped);
            }
            else if (text.Length > 0 && text[^1] != '-')
            {
                text.Append('-');
            }
        }

        string slug = text.ToString().Trim('-');
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }

        return slug.Length switch
        {
            0 => "profile",
            < 3 => slug + "-profile",
            _ => slug,
        };
    }
}
