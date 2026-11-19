// Fixed in phase 5b (docs/contract-phase5b.md). Do not edit.
namespace Tactile.Emulation;

/// <summary>
/// Game audio sent to the virtual DualSense: interleaved 4-channel S16LE at 48 kHz
/// (channels 1-2 speaker, 3-4 left/right haptic actuators). Valid only during the call.
/// </summary>
public delegate void GameAudioHandler(ReadOnlySpan<short> frames4ch);

/// <summary>Audio side of a virtual DualSense created with audio.</summary>
public interface IVirtualAudio
{
    /// <summary>The host selected the speaker streaming interface (interface 1, alt 1).</summary>
    bool SpeakerActive { get; }

    /// <summary>Raised on the ISO scheduler thread for every 1 ms packet. Handlers must be quick and must not throw.</summary>
    event GameAudioHandler? Audio;
}

public static class VirtualAudioPads
{
    /// <summary>
    /// Creates a virtual DualSense. With <paramref name="audio"/> it carries the USB Audio Class 1
    /// function (full speed, docs/protocol-emulation.md §10) and the returned pad implements
    /// <see cref="IVirtualAudio"/>; without it, the phase 4 HID-only pad.
    /// </summary>
    public static IVirtualPad CreateDualSense(this IUsbIpServer server, DualSenseIdentity? identity, bool audio) =>
        server is UsbIpServer s ? s.CreateDualSense(identity, audio) : server.Create(VirtualKind.DualSense, identity);
}
