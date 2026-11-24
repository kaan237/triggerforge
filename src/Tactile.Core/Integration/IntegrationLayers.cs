namespace Tactile.Core.Integration;

public static class IntegrationLayers
{
    public const string Udp = "integration.udp";
    public const string Telemetry = "integration.telemetry";
    /// <summary>The Bluetooth microphone's mute light; outranks every other layer and survives ClearOverrides.</summary>
    public const string MicLed = "mic.led";

    internal static int Order(string key) => key switch
    {
        Udp => 0,
        Telemetry => 1,
        MicLed => 4,
        _ => key.StartsWith("preview.", StringComparison.Ordinal) ? 3 : 2,
    };
}
