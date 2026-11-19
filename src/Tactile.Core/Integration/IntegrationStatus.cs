using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

public sealed record IntegrationStatus(UdpStatus Udp, ForzaStatus Forza);

public enum UdpListenerState { Off, Listening, PortBusy }
public enum UdpFormat { Native, Dsx }

public sealed record UdpStatus(
    UdpListenerState State, int Port, long Packets, long Rejected, long Unsupported,
    long? LastPacketAgoMs, bool Holding, UdpFormat? Format);

public enum ForzaListenerState { Off, Listening, Receiving, PortBusy, PortConflict }

public sealed record ForzaStatus(
    ForzaListenerState State, int Port, ForzaFormat? Format, long Packets, long Rejected, TelemetryValues? Values);

internal readonly record struct UdpControllerStatus(Link? Link, int? Battery)
{
    public bool Connected => Link is not null;
}
