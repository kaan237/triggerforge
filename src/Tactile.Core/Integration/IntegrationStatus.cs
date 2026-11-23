using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

public enum TelemetrySource { Forza, F1, Acc, Ac, BeamNg }

public sealed record IntegrationStatus(
    UdpStatus Udp, ForzaStatus Forza, DsuStatus? Dsu = null,
    F1Status? F1 = null, AcStatus? Ac = null, BeamNgStatus? BeamNg = null, TelemetrySource? ActiveSource = null);

public enum DsuListenerState { Off, Listening, PortBusy, PortConflict }

/// <param name="Packets">Datagrams sent to emulators.</param>
/// <param name="Rejected">Datagrams received and dropped.</param>
public sealed record DsuStatus(DsuListenerState State, int Port, int Clients, long Packets, long Rejected);

public enum UdpListenerState { Off, Listening, PortBusy }
public enum UdpFormat { Native, Dsx }

public sealed record UdpStatus(
    UdpListenerState State, int Port, long Packets, long Rejected, long Unsupported,
    long? LastPacketAgoMs, bool Holding, UdpFormat? Format);

public enum ForzaListenerState { Off, Listening, Receiving, PortBusy, PortConflict }

public sealed record ForzaStatus(
    ForzaListenerState State, int Port, ForzaFormat? Format, long Packets, long Rejected, TelemetryValues? Values);

/// <param name="Format">Packet format year of the last accepted datagram.</param>
/// <param name="Unsupported">Valid datagrams of a format this version does not read.</param>
public sealed record F1Status(
    ForzaListenerState State, int Port, int? Format, long Packets, long Rejected, long Unsupported, TelemetryValues? Values);

public enum AcListenerState { Off, Listening, Receiving }

public sealed record AcStatus(AcListenerState State, AcGame? Game, long Packets, TelemetryValues? Values);

public sealed record BeamNgStatus(
    ForzaListenerState State, int Port, long Packets, long Rejected, TelemetryValues? Values);

internal readonly record struct UdpControllerStatus(Link? Link, int? Battery)
{
    public bool Connected => Link is not null;
}
