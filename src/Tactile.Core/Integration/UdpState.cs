using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal sealed record UdpState(
    TriggerEffect? LeftTrigger = null, TriggerEffect? RightTrigger = null,
    Rgb? Lightbar = null, byte? PlayerLeds = null, MicLed? MicLed = null,
    byte? RumbleLeft = null, byte? RumbleRight = null)
{
    public static UdpState Empty { get; } = new();

    public OutputState Overlay(OutputState output) => output with
    {
        LeftTrigger = LeftTrigger ?? output.LeftTrigger,
        RightTrigger = RightTrigger ?? output.RightTrigger,
        Lightbar = Lightbar ?? output.Lightbar,
        PlayerLeds = PlayerLeds ?? output.PlayerLeds,
        MicLed = MicLed ?? output.MicLed,
        RumbleLeft = RumbleLeft ?? output.RumbleLeft,
        RumbleRight = RumbleRight ?? output.RumbleRight,
    };
}
