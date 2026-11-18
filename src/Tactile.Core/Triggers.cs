using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Turns a profile's <see cref="TriggerConfig"/> into the bytes the controller takes.</summary>
public static class Triggers
{
    /// <exception cref="ArgumentException">A parameter is missing or out of range.</exception>
    public static TriggerEffect ToEffect(TriggerConfig config)
    {
        int P(string name)
        {
            if (!config.Params.TryGetValue(name, out double v))
            {
                if (name == "frequency" && config.Frequency != 0)
                {
                    return config.Frequency;
                }

                throw new ArgumentException($"Tetik parametresi eksik: {name}.");
            }

            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                throw new ArgumentException($"Tetik parametresi geçersiz: {name}.");
            }

            return (int)Math.Round(v);
        }

        return config.Mode switch
        {
            TriggerMode.Off => TriggerEffect.Off,
            TriggerMode.Feedback => TriggerEffect.Feedback(P("start"), P("strength")),
            TriggerMode.Zones => TriggerEffect.Feedback(ZonesOf(config)),
            TriggerMode.Slope => TriggerEffect.Slope(P("start"), P("end"), P("startStrength"), P("endStrength")),
            TriggerMode.Weapon => TriggerEffect.Weapon(P("start"), P("end"), P("strength")),
            TriggerMode.Vibration => TriggerEffect.Vibration(P("start"), P("amplitude"), P("frequency")),
            TriggerMode.ZonesVibration => TriggerEffect.Vibration(ZonesOf(config), config.Frequency),
            TriggerMode.Bow => TriggerEffect.Bow(P("start"), P("end"), P("strength"), P("snap")),
            TriggerMode.Galloping => TriggerEffect.Galloping(P("start"), P("end"), P("firstFoot"), P("secondFoot"), P("frequency")),
            TriggerMode.Machine => TriggerEffect.Machine(P("start"), P("end"), P("amplitudeA"), P("amplitudeB"), P("frequency"), P("period")),
            _ => throw new ArgumentException($"Bilinmeyen tetik modu: {config.Mode}."),
        };
    }

    public static bool TryToEffect(TriggerConfig config, out TriggerEffect effect, out string? error)
    {
        try
        {
            effect = ToEffect(config);
            error = null;
            return true;
        }
        catch (ArgumentException e)
        {
            effect = TriggerEffect.Off;
            error = e.Message;
            return false;
        }
    }

    private static int[] ZonesOf(TriggerConfig config) =>
        config.Zones is { Length: TriggerEffect.Zones } z
            ? z
            : throw new ArgumentException($"Bölge listesi {TriggerEffect.Zones} değer olmalı.");
}
