namespace Tactile.Core.Integration;

public static class TelemetryTemplates
{
    public static TelemetryConfig Forza => new()
    {
        Rules =
        [
            new()
            {
                Signal = TelemetrySignal.Brake, Target = TelemetryTarget.LeftTrigger,
                Trigger = new() { Mode = TriggerMode.Feedback, Params = new Dictionary<string, double> { ["start"] = 1, ["strength"] = 1 } },
                Param = "strength", Min = 1, Max = 5,
            },
            new()
            {
                Signal = TelemetrySignal.Abs, From = 0.5, Target = TelemetryTarget.LeftTrigger,
                Trigger = new() { Mode = TriggerMode.Vibration, Params = new Dictionary<string, double>
                    { ["start"] = 1, ["amplitude"] = 6, ["frequency"] = 35 } },
            },
            new()
            {
                Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.RightTrigger,
                Trigger = new() { Mode = TriggerMode.Feedback, Params = new Dictionary<string, double> { ["start"] = 2, ["strength"] = 1 } },
                Param = "strength", Min = 1, Max = 4,
            },
            new()
            {
                Signal = TelemetrySignal.Traction, From = 0.5, Target = TelemetryTarget.RightTrigger,
                Trigger = new() { Mode = TriggerMode.Vibration, Params = new Dictionary<string, double>
                    { ["start"] = 2, ["amplitude"] = 5, ["frequency"] = 40 } },
            },
            new()
            {
                Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.Lightbar,
                ColorFrom = new(0, 255, 0), ColorTo = new(255, 0, 0), BlinkAt = 0.95,
            },
            new() { Signal = TelemetrySignal.Rpm, From = 0.4, Target = TelemetryTarget.PlayerLeds, Max = 5 },
            new() { Signal = TelemetrySignal.RumbleStrip, From = 0.5, To = 0.5, Target = TelemetryTarget.RumbleRight, Max = 120 },
        ],
    };

    public static TelemetryConfig F1 => Build(2, 6, 40, 45, TelemetrySignal.RevLights, 0.90, TelemetrySignal.RevLights, 0.2, 140);

    public static TelemetryConfig Acc => Build(2, 6, 38, 42, TelemetrySignal.Rpm, 0.95, TelemetrySignal.Rpm, 0.4, 130);

    public static TelemetryConfig Ac => Build(1, 5, 35, 40, TelemetrySignal.Rpm, 0.95, TelemetrySignal.Rpm, 0.4, 120);

    public static TelemetryConfig BeamNg => Build(1, 5, 32, 36, TelemetrySignal.RevLights, 0.95, TelemetrySignal.Rpm, 0.4, 120);

    public static bool TryGet(string? name, out TelemetryConfig config)
    {
        TelemetryConfig? found = name?.ToLowerInvariant() switch
        {
            "forza" => Forza,
            "f1" => F1,
            "acc" => Acc,
            "ac" => Ac,
            "beamng" => BeamNg,
            _ => null,
        };
        config = found ?? new();
        return found is not null;
    }

    private static TelemetryConfig Build(
        double brakeMin, double brakeMax, double absFrequency, double tractionFrequency,
        TelemetrySignal lightbar, double blinkAt, TelemetrySignal leds, double ledsFrom, double rumble) => new()
    {
        Rules =
        [
            new()
            {
                Signal = TelemetrySignal.Brake, Target = TelemetryTarget.LeftTrigger,
                Trigger = new() { Mode = TriggerMode.Feedback, Params = new Dictionary<string, double> { ["start"] = 1, ["strength"] = 1 } },
                Param = "strength", Min = brakeMin, Max = brakeMax,
            },
            new()
            {
                Signal = TelemetrySignal.Abs, From = 0.5, Target = TelemetryTarget.LeftTrigger,
                Trigger = new() { Mode = TriggerMode.Vibration, Params = new Dictionary<string, double>
                    { ["start"] = 1, ["amplitude"] = 6, ["frequency"] = absFrequency } },
            },
            new()
            {
                Signal = TelemetrySignal.Rpm, Target = TelemetryTarget.RightTrigger,
                Trigger = new() { Mode = TriggerMode.Feedback, Params = new Dictionary<string, double> { ["start"] = 2, ["strength"] = 1 } },
                Param = "strength", Min = 1, Max = 4,
            },
            new()
            {
                Signal = TelemetrySignal.Traction, From = 0.5, Target = TelemetryTarget.RightTrigger,
                Trigger = new() { Mode = TriggerMode.Vibration, Params = new Dictionary<string, double>
                    { ["start"] = 2, ["amplitude"] = 5, ["frequency"] = tractionFrequency } },
            },
            new()
            {
                Signal = lightbar, Target = TelemetryTarget.Lightbar,
                ColorFrom = new(0, 255, 0), ColorTo = new(255, 0, 0), BlinkAt = blinkAt,
            },
            new() { Signal = leds, From = ledsFrom, Target = TelemetryTarget.PlayerLeds, Max = 5 },
            new() { Signal = TelemetrySignal.RumbleStrip, From = 0.5, To = 0.5, Target = TelemetryTarget.RumbleRight, Max = rumble },
        ],
    };
}
