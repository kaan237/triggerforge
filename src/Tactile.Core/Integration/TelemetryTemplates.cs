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
}
