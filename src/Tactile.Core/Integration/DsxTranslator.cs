using System.Globalization;
using System.Text.Json;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal static class DsxTranslator
{
    private static readonly byte[] PlayerMasks = [0b00100, 0b01010, 0b10101, 0b11011, 0b11111, 0];

    public static (UdpState State, int Unsupported) Apply(JsonElement instructions, UdpState state)
    {
        int unsupported = 0;
        foreach (JsonElement instruction in instructions.EnumerateArray())
        {
            if (instruction.ValueKind != JsonValueKind.Object ||
                !instruction.TryGetProperty("type", out JsonElement typeValue) ||
                typeValue.ValueKind != JsonValueKind.Number || !typeValue.TryGetInt32(out int type))
            {
                continue;
            }

            if (type is < 1 or > 7)
            {
                unsupported++;
                continue;
            }

            if (!instruction.TryGetProperty("parameters", out JsonElement parameters) ||
                parameters.ValueKind != JsonValueKind.Array || !TryRead(parameters, out double[] values))
            {
                unsupported++;
                continue;
            }

            state = ApplyInstruction(type, values, state, ref unsupported);
        }

        return (state, unsupported);
    }

    private static UdpState ApplyInstruction(int type, double[] values, UdpState state, ref int unsupported)
    {
        byte P(int i, int max = 255) => (byte)Math.Clamp(values[i], 0, max);

        switch (type)
        {
            case 1 when values.Length >= 3 && values[1] is 1 or 2:
            {
                TriggerEffect? effect = TranslateTrigger(values);
                if (effect is null)
                {
                    unsupported++;
                    return state;
                }

                return values[1] == 1 ? state with { LeftTrigger = effect } : state with { RightTrigger = effect };
            }
            case 2 when values.Length >= 4:
                return state with { Lightbar = new Rgb(P(1), P(2), P(3)) };
            case 3 when values.Length >= 6:
            {
                byte mask = 0;
                for (int i = 0; i < 5; i++)
                {
                    mask |= (byte)(P(i + 1, 1) << i);
                }

                return state with { PlayerLeds = mask };
            }
            case 4 when values.Length >= 3 && values[1] is 1 or 2:
                return state;
            case 5 when values.Length >= 2:
                return state with { MicLed = P(1, 2) switch { 0 => MicLed.On, 1 => MicLed.Pulse, _ => MicLed.Off } };
            case 6 when values.Length >= 2:
                return state with { PlayerLeds = PlayerMasks[P(1, 5)] };
            case 7 when values.Length >= 1:
                return UdpState.Empty;
            default:
                unsupported++;
                return state;
        }
    }

    private static TriggerEffect? TranslateTrigger(double[] values)
    {
        int P(int i, int min, int max) => (int)Math.Clamp(values[i], min, max);

        if (values[2] is < 0 or > 18 || values[2] != Math.Truncate(values[2]))
        {
            return null;
        }

        switch ((int)values[2])
        {
            case 0: return TriggerEffect.Off;
            case 1: return TriggerEffect.Feedback(8, 8);
            case 2: return TriggerEffect.Feedback(0, 1);
            case 3: return TriggerEffect.Feedback(0, 2);
            case 4: return TriggerEffect.Feedback(0, 5);
            case 5: return TriggerEffect.Feedback(0, 6);
            case 6: return TriggerEffect.Feedback(0, 7);
            case 7: return TriggerEffect.Feedback(0, 8);
            case 8 when values.Length >= 4: return TriggerEffect.Vibration(0, 8, P(3, 0, 255));
            case 9: return TriggerEffect.Feedback([0, 6, 0, 6, 0, 6, 0, 6, 0, 6]);
            case 10: return TriggerEffect.Feedback(0, 4);
            case 11: return TriggerEffect.Vibration(0, 4, 8);
            case 12 when values.Length >= 4:
                return CustomTrigger(values);
            case 13 when values.Length >= 5:
                return TriggerEffect.Feedback(P(3, 0, 9), P(4, 0, 8));
            case 14 when values.Length >= 7:
            {
                int start = P(3, 0, 7);
                return TriggerEffect.Bow(start, P(4, start + 1, 8), P(5, 0, 8), P(6, 0, 8));
            }
            case 15 when values.Length >= 8:
            {
                int start = P(3, 0, 8);
                int first = P(5, 0, 6);
                return TriggerEffect.Galloping(start, P(4, start + 1, 9), first, P(6, first + 1, 7), P(7, 0, 255));
            }
            case 16 when values.Length >= 6:
            {
                int start = P(3, 2, 7);
                return TriggerEffect.Weapon(start, P(4, start + 1, 8), P(5, 0, 8));
            }
            case 17 when values.Length >= 6:
                return TriggerEffect.Vibration(P(3, 0, 9), P(4, 0, 8), P(5, 0, 255));
            case 18 when values.Length >= 9:
            {
                int start = P(3, 0, 8);
                return TriggerEffect.Machine(start, P(4, start + 1, 9), P(5, 0, 7), P(6, 0, 7), P(7, 0, 255), P(8, 0, 255));
            }
            default:
                return null;
        }
    }

    private static TriggerEffect? CustomTrigger(double[] values)
    {
        double mode = values[3];
        if (mode is < 0 or > 16 || mode != Math.Truncate(mode))
        {
            return null;
        }

        if (mode == 0)
        {
            return TriggerEffect.Off;
        }

        if (values.Length < 11)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[TriggerEffect.Size];
        bytes.Clear();
        bytes[0] = mode switch { <= 4 => 0x01, <= 8 => 0x02, _ => 0x06 };
        for (int i = 0; i < 7; i++)
        {
            bytes[i + 1] = (byte)Math.Clamp(values[i + 4], 0, 255);
        }

        return TriggerEffect.FromBytes(bytes);
    }

    private static bool TryRead(JsonElement parameters, out double[] values)
    {
        values = new double[parameters.GetArrayLength()];
        int i = 0;
        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            double number = parameter.ValueKind switch
            {
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.Number when parameter.TryGetDouble(out double value) => value,
                JsonValueKind.String when double.TryParse(parameter.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) => value,
                _ => double.NaN,
            };

            if (!double.IsFinite(number))
            {
                return false;
            }

            values[i++] = number;
        }

        return true;
    }
}
