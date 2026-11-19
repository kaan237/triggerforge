using System.Text.Json;
using System.Text.Json.Serialization;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal static class NativeCommands
{
    private static readonly JsonSerializerOptions TriggerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<TriggerMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static (UdpState State, string? Error) Apply(
        JsonElement commands, UdpState state, Action<string> activateProfile)
    {
        JsonElement.ArrayEnumerator remaining = commands.EnumerateArray();
        try
        {
            while (ApplyUntilProfile(ref remaining, ref state) is { } id)
            {
                activateProfile(id);
            }

            return (state, null);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException)
        {
            return (state, ErrorMessage(e));
        }
    }

    public static string ErrorMessage(Exception error) => error is JsonException or ArgumentOutOfRangeException
        ? "Tetik yapılandırması geçersiz."
        : error.Message;

    public static string? ApplyUntilProfile(ref JsonElement.ArrayEnumerator remaining, ref UdpState state)
    {
        while (remaining.MoveNext())
        {
            JsonElement command = remaining.Current;
            string name = ReadString(command, "cmd");
            if (name == "profile")
            {
                return ReadString(command, "id");
            }

            state = ApplyCommand(name, command, state);
        }

        return null;
    }

    private static UdpState ApplyCommand(string name, JsonElement command, UdpState state)
    {
        switch (name)
        {
            case "trigger":
                return ApplyTrigger(command, state);
            case "lightbar":
                return state with { Lightbar = new Rgb(ReadByte(command, "r"), ReadByte(command, "g"), ReadByte(command, "b")) };
            case "playerLeds":
                return state with { PlayerLeds = ReadByte(command, "mask", 31) };
            case "micLed":
                return state with
                {
                    MicLed = ReadString(command, "mode") switch
                    {
                        "off" => MicLed.Off,
                        "on" => MicLed.On,
                        "pulse" => MicLed.Pulse,
                        _ => throw new ArgumentException("Mikrofon LED modu geçersiz."),
                    },
                };
            case "rumble":
                return state with { RumbleLeft = ReadByte(command, "left"), RumbleRight = ReadByte(command, "right") };
            case "reset":
                return UdpState.Empty;
            case "status":
                return state;
            default:
                throw new ArgumentException("Bilinmeyen UDP komutu.");
        }
    }

    private static UdpState ApplyTrigger(JsonElement command, UdpState state)
    {
        bool left = ReadString(command, "side") switch
        {
            "left" => true,
            "right" => false,
            _ => throw new ArgumentException("Tetik tarafı geçersiz."),
        };

        JsonElement value = Read(command, "config");
        TriggerEffect? effect = null;
        if (value.ValueKind != JsonValueKind.Null)
        {
            string mode = ReadString(value, "mode");
            TriggerConfig? config = value.Deserialize<TriggerConfig>(TriggerJson);
            if (config?.Params is null ||
                !string.Equals(mode, JsonNamingPolicy.CamelCase.ConvertName(config.Mode.ToString()), StringComparison.OrdinalIgnoreCase) ||
                config.Params.Values.Any(p => !double.IsFinite(p) || p < int.MinValue || p > int.MaxValue))
            {
                throw new ArgumentException("Tetik yapılandırması geçersiz.");
            }

            effect = Triggers.ToEffect(config);
        }

        return left ? state with { LeftTrigger = effect } : state with { RightTrigger = effect };
    }

    private static JsonElement Read(JsonElement command, string name) =>
        command.ValueKind == JsonValueKind.Object && command.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new ArgumentException($"UDP alanı eksik: {name}.");

    private static string ReadString(JsonElement command, string name) =>
        Read(command, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw new ArgumentException($"UDP metin alanı geçersiz: {name}.");

    private static byte ReadByte(JsonElement command, string name, byte max = byte.MaxValue) =>
        Read(command, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetByte(out byte result) && result <= max
            ? result
            : throw new ArgumentException($"UDP sayı alanı 0–{max} aralığında olmalı: {name}.");
}
