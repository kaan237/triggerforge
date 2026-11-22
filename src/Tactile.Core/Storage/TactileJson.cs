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

/// <summary>
/// The per-game pad overrides. An entry whose mode this version does not know is left out
/// instead of becoming Off, so a typo or a newer file never switches a pad off.
/// </summary>
internal sealed class GameModesConverter : JsonConverter<IReadOnlyDictionary<string, EmulationMode>>
{
    public override IReadOnlyDictionary<string, EmulationMode> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var modes = new Dictionary<string, EmulationMode>();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return modes;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.String && ParseName(reader.GetString()) is { } mode)
            {
                // A repeated key (they differ only in case or spacing after cleaning) keeps the valid one.
                modes[key] = mode;
            }
            else
            {
                reader.Skip();
            }
        }

        return modes;
    }

    /// <summary>Only the written names of a concrete kind; numbers, lists of names and Auto do not count.</summary>
    private static EmulationMode? ParseName(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "off" => EmulationMode.Off,
        "xbox360" => EmulationMode.Xbox360,
        "dualsense" => EmulationMode.DualSense,
        "dualshock4" => EmulationMode.DualShock4,
        _ => null,
    };

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, EmulationMode> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach ((string key, EmulationMode mode) in value)
        {
            writer.WriteString(key, JsonNamingPolicy.CamelCase.ConvertName(mode.ToString()));
        }

        writer.WriteEndObject();
    }
}
