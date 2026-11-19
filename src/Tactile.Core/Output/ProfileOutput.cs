using Tactile.Device.DualSense;

namespace Tactile.Core;

internal static class ProfileOutput
{
    public static (TriggerEffect Left, TriggerEffect Right) TriggersFor(Profile profile)
    {
        Triggers.TryToEffect(profile.Triggers.Left, out TriggerEffect left, out _);
        Triggers.TryToEffect(profile.Triggers.Right, out TriggerEffect right, out _);
        return (left, right);
    }

    public static OutputState Build(Profile profile, (TriggerEffect Left, TriggerEffect Right) triggers,
        TimeSpan now, int? battery, int? autoPlayer)
    {
        PlayerLedConfig leds = profile.PlayerLeds;
        (Rgb color, LedBrightness brightness) = LightbarEngine.Evaluate(profile.Lightbar, now.TotalSeconds, battery, leds.Number);
        return OutputState.Default with
        {
            Lightbar = color,
            Brightness = brightness,
            PlayerLeds = PlayerLeds.Mask(leds, autoPlayer),
            MicLed = profile.MicLed,
            LeftTrigger = triggers.Left,
            RightTrigger = triggers.Right,
        };
    }
}
