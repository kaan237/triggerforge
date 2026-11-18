using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>The fields a game has set on the virtual DualSense, laid over the profile's output.</summary>
public static class GameOutput
{
    public static OutputState Overlay(OutputState profile, OutputState game, OutputFields fields, double rumbleStrength = 1)
    {
        OutputState o = profile;
        if (fields.HasFlag(OutputFields.Rumble))
        {
            o = o with { RumbleLeft = ScaleRumble(game.RumbleLeft, rumbleStrength), RumbleRight = ScaleRumble(game.RumbleRight, rumbleStrength) };
        }

        if (fields.HasFlag(OutputFields.LeftTrigger))
        {
            o = o with { LeftTrigger = game.LeftTrigger };
        }

        if (fields.HasFlag(OutputFields.RightTrigger))
        {
            o = o with { RightTrigger = game.RightTrigger };
        }

        if (fields.HasFlag(OutputFields.Lightbar))
        {
            o = o with { Lightbar = game.Lightbar };
        }

        if (fields.HasFlag(OutputFields.PlayerLeds))
        {
            o = o with { PlayerLeds = game.PlayerLeds };
        }

        if (fields.HasFlag(OutputFields.MicLed))
        {
            o = o with { MicLed = game.MicLed };
        }

        if (fields.HasFlag(OutputFields.Brightness))
        {
            o = o with { Brightness = game.Brightness };
        }

        return o;
    }

    /// <summary>Profile rumble strength (0-2) applied to a motor value.</summary>
    public static byte ScaleRumble(byte value, double strength) =>
        (byte)Math.Clamp(Math.Round(value * (double.IsFinite(strength) ? strength : 1)), 0, 255);
}
