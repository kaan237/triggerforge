// Virtual controller contracts (docs/contract-phase4.md and docs/contract-phase12.md).
using Tactile.Device.DualSense;

namespace Tactile.Emulation;

public enum VirtualKind { Xbox360, DualSense, DualShock4 }

/// <summary>Real controller values the virtual DualSense answers with (USB form: report id first).</summary>
public sealed record DualSenseIdentity(byte[] Calibration, byte[] Pairing, byte[] Firmware);

/// <summary>Something the game sent to the virtual pad. Timestamp: Stopwatch ticks at arrival.</summary>
public abstract record PadFeedback(long Timestamp);

/// <summary>Xbox 360 rumble.</summary>
public sealed record RumbleFeedback(long Timestamp, byte Large, byte Small) : PadFeedback(Timestamp);

/// <summary>Xbox 360 ring LED pattern.</summary>
public sealed record LedFeedback(long Timestamp, byte Pattern) : PadFeedback(Timestamp);

/// <summary>47-byte common block from DualSense output report 0x02.</summary>
public sealed record DualSenseOutputFeedback(long Timestamp, byte[] State) : PadFeedback(Timestamp);

/// <summary>DualShock 4 rumble and lightbar updates.</summary>
public sealed record DualShock4Feedback(long Timestamp, bool HasRumble, byte Large, byte Small, bool HasLightbar, Rgb Lightbar) : PadFeedback(Timestamp);

public interface IVirtualPad : IDisposable
{
    VirtualKind Kind { get; }

    /// <summary>USB/IP bus id the VHCI imports, e.g. "1-1".</summary>
    string BusId { get; }

    /// <summary>A client has imported it (OP_REQ_IMPORT done).</summary>
    bool Imported { get; }

    /// <summary>Next input report towards the game (latest wins).</summary>
    void Submit(InputState state);

    /// <summary>Raised on the server thread; handlers must be quick.</summary>
    event Action<PadFeedback>? Feedback;
}

public interface IUsbIpServer : IDisposable
{
    /// <summary>Bound on 127.0.0.1.</summary>
    int Port { get; }

    /// <summary>Identity for DualSense and DualShock 4; null = built-in defaults.</summary>
    IVirtualPad Create(VirtualKind kind, DualSenseIdentity? identity);

    void Remove(IVirtualPad pad);
}

public static partial class UsbIpServerFactory
{
    /// <summary>Starts the server on 127.0.0.1; port 0 = ephemeral.</summary>
    public static partial IUsbIpServer Start(int port = 0);
}
