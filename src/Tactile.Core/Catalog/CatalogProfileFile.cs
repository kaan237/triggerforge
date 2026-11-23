using System.Text;
using System.Text.Json;

namespace Tactile.Core.Catalog;

/// <summary>Checks on the raw bytes of a shared profile file.</summary>
internal static class CatalogProfileFile
{
    /// <summary>
    /// True when any node anywhere in the document has a "kind" that reads as the run action.
    /// Mirrors how the profile reader treats enum values, so no spelling slips through.
    /// </summary>
    /// <exception cref="JsonException">The bytes are not a JSON document without duplicate properties.</exception>
    public static bool ContainsRunAction(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json.ToArray(),
            new JsonDocumentOptions { AllowDuplicateProperties = false });
        return Scan(document.RootElement);
    }

    private static bool Scan(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Equals("kind", StringComparison.OrdinalIgnoreCase) && IsRun(property.Value))
                    {
                        return true;
                    }

                    if (Scan(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (Scan(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool IsRun(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Enum.TryParse(value.GetString(), ignoreCase: true, out ActionKind kind) && kind == ActionKind.Run,
        JsonValueKind.Number => value.TryGetInt32(out int n) && n == (int)ActionKind.Run,
        _ => false,
    };

    public static bool HasBom(ReadOnlySpan<byte> bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <exception cref="DecoderFallbackException">Not UTF-8, or starts with a byte order mark.</exception>
    public static string DecodeStrict(byte[] bytes) =>
        HasBom(bytes) ? throw new DecoderFallbackException("The file starts with a byte order mark.") : new UTF8Encoding(false, true).GetString(bytes);
}
