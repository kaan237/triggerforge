using System.Text.Json;
using System.Text.Json.Serialization;
using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

public enum TelemetrySignal
{
    Unknown, RaceOn, SpeedKmh, Rpm, Gas, Brake, Clutch, Handbrake, Gear, Steer,
    SlipFront, SlipRear, Surface, RumbleStrip, Boost, Abs, Traction, RevLights, Drs,
}

public enum TelemetryTarget { Unknown, LeftTrigger, RightTrigger, Lightbar, PlayerLeds, RumbleLeft, RumbleRight }

public sealed record TelemetryConfig
{
    public IReadOnlyList<TelemetryRule> Rules { get; init; } = [];
}

public sealed record TelemetryRule
{
    public TelemetrySignal Signal { get; init; }
    public double From { get; init; }
    public double To { get; init; } = 1;
    public double Exponent { get; init; } = 1;
    public TelemetryTarget Target { get; init; }
    [JsonConverter(typeof(TelemetryTriggerConverter))]
    public TriggerConfig? Trigger { get; init; }
    public string? Param { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public TelemetryColor? ColorFrom { get; init; }
    public TelemetryColor? ColorTo { get; init; }
    public double? BlinkAt { get; init; }
}

public readonly record struct TelemetryColor(int R, int G, int B);

internal sealed class TelemetryTriggerConverter : JsonConverter<TriggerConfig>
{
    private static readonly JsonSerializerOptions TriggerJson = CreateOptions();

    public override TriggerConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        try
        {
            return document.RootElement.Deserialize<TriggerConfig>(TriggerJson);
        }
        catch (JsonException)
        {
            // An invalid template is dropped with its rule during normalization.
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, TriggerConfig value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(TactileJson.Compact);
        options.Converters.Insert(0, new JsonStringEnumConverter<TriggerMode>(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly();
        return options;
    }
}

internal static class TelemetryRules
{
    internal static bool BlinkOn(TimeSpan time) => time.Ticks / (TimeSpan.TicksPerSecond / 16) % 2 == 0;

    public static UdpState Evaluate(TelemetryConfig config, TelemetryValues values, TimeSpan time)
    {
        UdpState state = UdpState.Empty;
        if (values.RaceOn == 0)
        {
            return state;
        }

        foreach (TelemetryRule rule in config.Rules)
        {
            double value = values.Get(rule.Signal);
            if (!(value >= rule.From))
            {
                continue;
            }

            double t = rule.To <= rule.From ? 1 :
                Math.Pow(Math.Clamp((value - rule.From) / (rule.To - rule.From), 0, 1), rule.Exponent);
            double output = Mix(rule.Min, rule.Max, t);
            switch (rule.Target)
            {
                case TelemetryTarget.LeftTrigger:
                case TelemetryTarget.RightTrigger:
                    if (TryTrigger(rule.Trigger, rule.Param, output, out TriggerEffect effect))
                    {
                        state = rule.Target == TelemetryTarget.LeftTrigger
                            ? state with { LeftTrigger = effect }
                            : state with { RightTrigger = effect };
                    }

                    break;
                case TelemetryTarget.Lightbar:
                    state = state with { Lightbar = rule.BlinkAt is double blinkAt && t >= blinkAt && !BlinkOn(time)
                        ? default(Rgb) : MixColor(rule.ColorFrom.GetValueOrDefault(), rule.ColorTo.GetValueOrDefault(), t) };
                    break;
                case TelemetryTarget.PlayerLeds:
                    state = state with { PlayerLeds = (byte)((1 << Round(output, 0, 5)) - 1) };
                    break;
                case TelemetryTarget.RumbleLeft:
                    state = state with { RumbleLeft = (byte)Round(output, 0, 255) };
                    break;
                case TelemetryTarget.RumbleRight:
                    state = state with { RumbleRight = (byte)Round(output, 0, 255) };
                    break;
            }
        }

        return state;
    }

    internal static bool TryTrigger(TriggerConfig? trigger, string? param, double output, out TriggerEffect effect)
    {
        effect = TriggerEffect.Off;
        if (trigger?.Params is null)
        {
            return false;
        }

        if (param is not null)
        {
            if (ParameterRange(trigger.Mode, param) is not { } range || !double.IsFinite(output))
            {
                return false;
            }

            int min = range.Min;
            int max = range.Max;
            if (trigger.Mode is TriggerMode.Slope or TriggerMode.Weapon or TriggerMode.Bow or TriggerMode.Galloping or TriggerMode.Machine)
            {
                if (param == "start")
                {
                    max = Math.Min(max, Companion("end", max + 1) - 1);
                }
                else if (param == "end")
                {
                    min = Math.Max(min, Companion("start", min - 1) + 1);
                }
            }

            if (trigger.Mode == TriggerMode.Galloping)
            {
                if (param == "firstFoot")
                {
                    max = Math.Min(max, Companion("secondFoot", max + 1) - 1);
                }
                else if (param == "secondFoot")
                {
                    min = Math.Max(min, Companion("firstFoot", min - 1) + 1);
                }
            }

            if (min > max)
            {
                return false;
            }

            int driven = Round(output, min, max);
            trigger = trigger.Mode == TriggerMode.ZonesVibration
                ? trigger with { Frequency = driven }
                : trigger with { Params = new Dictionary<string, double>(trigger.Params) { [param] = driven } };
        }

        return Triggers.TryToEffect(trigger, out effect, out _);

        int Companion(string key, int fallback) =>
            trigger.Params.TryGetValue(key, out double value) && double.IsFinite(value)
                ? Round(value, -1, 256) : fallback;
    }

    internal static (int Min, int Max)? ParameterRange(TriggerMode mode, string param) => (mode, param) switch
    {
        (TriggerMode.Feedback or TriggerMode.Vibration, "start") => (0, 9),
        (TriggerMode.Slope or TriggerMode.Galloping or TriggerMode.Machine, "start") => (0, 8),
        (TriggerMode.Bow, "start") => (0, 7),
        (TriggerMode.Weapon, "start") => (2, 7),
        (TriggerMode.Slope or TriggerMode.Galloping or TriggerMode.Machine, "end") => (1, 9),
        (TriggerMode.Bow, "end") => (1, 8),
        (TriggerMode.Weapon, "end") => (3, 8),
        (TriggerMode.Feedback or TriggerMode.Weapon or TriggerMode.Bow, "strength") => (0, 8),
        (TriggerMode.Slope, "startStrength" or "endStrength") => (1, 8),
        (TriggerMode.Bow, "snap") => (0, 8),
        (TriggerMode.Vibration, "amplitude") => (0, 8),
        (TriggerMode.Vibration or TriggerMode.ZonesVibration or TriggerMode.Galloping or TriggerMode.Machine, "frequency") => (0, 255),
        (TriggerMode.Galloping, "firstFoot") => (0, 6),
        (TriggerMode.Galloping, "secondFoot") => (1, 7),
        (TriggerMode.Machine, "amplitudeA" or "amplitudeB") => (0, 7),
        (TriggerMode.Machine, "period") => (0, 255),
        _ => null,
    };

    private static Rgb MixColor(TelemetryColor from, TelemetryColor to, double t) => new(
        (byte)Round(Mix(from.R, to.R, t), 0, 255), (byte)Round(Mix(from.G, to.G, t), 0, 255),
        (byte)Round(Mix(from.B, to.B, t), 0, 255));

    private static double Mix(double from, double to, double t) => from + (to - from) * t;

    private static int Round(double value, int min, int max) => (int)Math.Round(Math.Clamp(value, min, max));
}
