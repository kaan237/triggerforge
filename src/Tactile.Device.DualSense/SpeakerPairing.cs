namespace Tactile.Device.DualSense;

public enum SpeakerPacket { Single36, Double39 }

internal enum SpeakerTick { Haptics, Hold, Single, Double }

internal sealed class SpeakerPairing
{
    private readonly sbyte[] _haptics = new sbyte[HapticReport.PayloadLength];
    private readonly byte[] _opus = new byte[HapticReport.SpeakerFrameLength];

    public bool HasPending { get; private set; }
    public ReadOnlySpan<sbyte> PendingHaptics => _haptics;
    public ReadOnlySpan<byte> PendingOpus => _opus;

    public SpeakerTick Tick(SpeakerPacket mode, bool hasSpeaker, ReadOnlySpan<sbyte> haptics,
        ReadOnlySpan<byte> opus, out bool flushPending)
    {
        bool doubleFrame = hasSpeaker && mode == SpeakerPacket.Double39;
        flushPending = HasPending && !doubleFrame;
        if (doubleFrame && !HasPending)
        {
            haptics.CopyTo(_haptics);
            opus.CopyTo(_opus);
            HasPending = true;
            return SpeakerTick.Hold;
        }

        HasPending = false;
        if (!hasSpeaker)
        {
            return SpeakerTick.Haptics;
        }

        return doubleFrame ? SpeakerTick.Double : SpeakerTick.Single;
    }
}
