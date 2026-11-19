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
        UdpState[] states = [state, UdpState.Empty, UdpState.Empty, UdpState.Empty];
        JsonElement.ArrayEnumerator remaining = commands.EnumerateArray();
        int index = 0;
        try
        {
            while (ApplyUntilProfile(ref remaining, states, _ => { }, ref index) is { } id)
            {
                activateProfile(id);
            }

            return (states[0], null);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException)
        {
            return (states[0], ErrorMessage(e));
        }
    }

    public static string ErrorMessage(Exception error) => error is JsonException or ArgumentOutOfRangeException
        ? Text.Pick("Invalid trigger configuration.", "Tetik yapılandırması geçersiz.")
        : error.Message;

    public static string? ApplyUntilProfile(ref JsonElement.ArrayEnumerator remaining, UdpState[] states,
        Action<int> addressed, ref int index, Func<int, bool>? connected = null)
    {
        while (remaining.MoveNext())
        {
            JsonElement command = remaining.Current;
            index = 0;
            string name = ReadString(command, "cmd");
            bool targeted = command.TryGetProperty("controller", out _);
            index = targeted ? ReadByte(command, "controller", 3) : 0;
            if (name == "profile")
            {
                string id = ReadString(command, "id");
                addressed(index);
                return id;
            }

            if (name == "reset" && !targeted)
            {
                Array.Fill(states, UdpState.Empty);
            }
            else
            {
                UdpState next = ApplyCommand(name, command, states[index]);
                if (name is not ("status" or "reset") && index > 0 && connected?.Invoke(index) == false)
                {
                    throw new ArgumentException(Text.Pick("Secondary controller is not connected.", "İkincil kontrolcü bağlı değil."));
                }

                states[index] = next;
            }

            addressed(index);
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
                        _ => throw new ArgumentException(Text.Pick("Invalid microphone LED mode.", "Mikrofon LED modu geçersiz.")),
                    },
                };
            case "rumble":
                return state with { RumbleLeft = ReadByte(command, "left"), RumbleRight = ReadByte(command, "right") };
            case "reset":
                return UdpState.Empty;
            case "status":
                return state;
            default:
                throw new ArgumentException(Text.Pick("Unknown UDP command.", "Bilinmeyen UDP komutu."));
        }
    }

    private static UdpState ApplyTrigger(JsonElement command, UdpState state)
    {
        bool left = ReadString(command, "side") switch
        {
            "left" => true,
            "right" => false,
            _ => throw new ArgumentException(Text.Pick("Invalid trigger side.", "Tetik tarafı geçersiz.")),
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
                throw new ArgumentException(Text.Pick("Invalid trigger configuration.", "Tetik yapılandırması geçersiz."));
            }

            effect = Triggers.ToEffect(config);
        }

        return left ? state with { LeftTrigger = effect } : state with { RightTrigger = effect };
    }

    private static JsonElement Read(JsonElement command, string name) =>
        command.ValueKind == JsonValueKind.Object && command.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new ArgumentException(Text.Pick($"Missing UDP field: {name}.", $"UDP alanı eksik: {name}."));

    private static string ReadString(JsonElement command, string name) =>
        Read(command, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw new ArgumentException(Text.Pick($"Invalid UDP text field: {name}.", $"UDP metin alanı geçersiz: {name}."));

    private static byte ReadByte(JsonElement command, string name, byte max = byte.MaxValue) =>
        Read(command, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetByte(out byte result) && result <= max
            ? result
            : throw new ArgumentException(Text.Pick($"UDP field {name} must be between 0 and {max}.", $"UDP sayı alanı 0–{max} aralığında olmalı: {name}."));
}
