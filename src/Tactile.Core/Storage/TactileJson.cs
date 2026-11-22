using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tactile.Core;

/// <summary>Serializer settings for every file and message: camelCase, enums as camelCase strings.</summary>
public static class TactileJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Keep Turkish letters readable in files people may open by hand.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new LenientEnumConverterFactory());
        var defaultGyroStick = new GyroStickConfig();
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(EmulationConfig))
            {
                // Preserve the legacy emulation JSON shape until gyro stick is configured.
                info.Properties.Single(p => p.Name == "gyroStick").ShouldSerialize =
                    (_, value) => value is GyroStickConfig gyro && gyro != defaultGyroStick;
            }
        });
        o.TypeInfoResolver = resolver;
        o.MakeReadOnly();
        return o;
    }
}

/// <summary>
/// Enums as camelCase strings. An unknown value (newer file, typo) reads as the enum's default
/// instead of failing the whole file.
/// </summary>
internal sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
        (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(t))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse(reader.GetString(), ignoreCase: true, out T value) && Enum.IsDefined(value))
            {
                return value;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n) &&
                Enum.TryParse(n.ToString(CultureInfo.InvariantCulture), out value) && Enum.IsDefined(value))
            {
                return value;
            }

            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}
