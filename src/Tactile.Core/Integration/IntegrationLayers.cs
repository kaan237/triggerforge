namespace Tactile.Core.Integration;

public static class IntegrationLayers
{
    public const string Udp = "integration.udp";
    public const string Telemetry = "integration.telemetry";

    internal static int Order(string key) => key switch
    {
        Udp => 0,
        Telemetry => 1,
        _ => key.StartsWith("preview.", StringComparison.Ordinal) ? 3 : 2,
    };
}
