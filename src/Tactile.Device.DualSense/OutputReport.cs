namespace Tactile.Device.DualSense;

/// <param name="AudioStream">Bluetooth haptics/audio stream is open: route to the speaker path and enable audio fields.</param>
public readonly record struct OutputOptions(bool FirstReport = false, bool RumbleV2 = true, bool Rumble = true, bool AudioStream = false,
    bool MicStream = false, byte MicGain = 0x40, bool MicMuted = false)
{
    // `new()` on a record struct ignores the primary constructor defaults, so spell them out.
    public static OutputOptions Default { get; } = new(FirstReport: false);
}

/// <summary>Fields of the state block whose valid flags are set.</summary>
[Flags]
public enum OutputFields
{
    None = 0,
    Rumble = 1 << 0,
    RightTrigger = 1 << 1,
    LeftTrigger = 1 << 2,
    Lightbar = 1 << 3,
    PlayerLeds = 1 << 4,
    MicLed = 1 << 5,
    Brightness = 1 << 6,
}

/// <summary>Builds main output reports (USB 0x02, Bluetooth 0x31). Layout: docs/protocol.md.</summary>
public static class OutputReport
{
    /// <summary>Firmware "update version" from which rumble uses the v2 flag (2.21).</summary>
    public const ushort RumbleV2UpdateVersion = 0x0215;

    public const int StateLength = 47;
    public const int UsbLength = 63;
    public const int BluetoothLength = 78;

    // Flags A (offset 0)
    private const byte CompatibleRumble = 0x01;
    private const byte RumbleNotHaptics = 0x02;
    private const byte RightTriggerEffect = 0x04;
    private const byte LeftTriggerEffect = 0x08;
    private const byte HeadphoneVolumeEnable = 0x10;
    private const byte SpeakerVolumeEnable = 0x20;
    private const byte MicVolumeEnable = 0x40;
    private const byte AudioControlEnable = 0x80;
    // Flags B (offset 1)
    private const byte MicLedEnable = 0x01;
    private const byte PowerSaveEnable = 0x02;
    private const byte LightbarEnable = 0x04;
    private const byte MicMuteBit = 0x10;
    private const byte PlayerLedsEnable = 0x10;
    private const byte AudioControl2Enable = 0x80;
    // Flags C (offset 38)
    private const byte BrightnessEnable = 0x01;
    private const byte LightbarSetupEnable = 0x02;
    private const byte CompatibleRumbleV2 = 0x04;

    /// <summary>Writes the 47-byte state block.</summary>
    public static void WriteState(OutputState s, Span<byte> block, OutputOptions? options = null)
    {
        OutputOptions o = options ?? OutputOptions.Default;
        block[..StateLength].Clear();
        block[0] = RightTriggerEffect | LeftTriggerEffect | SpeakerVolumeEnable;
        block[1] = MicLedEnable | LightbarEnable | PlayerLedsEnable;
        block[38] = BrightnessEnable;
        if (o.Rumble)
        {
            // Classic two-motor rumble. Left off while streaming haptic audio.
            block[0] |= RumbleNotHaptics;
            if (o.RumbleV2)
            {
                block[38] |= CompatibleRumbleV2;
            }
            else
            {
                block[0] |= CompatibleRumble;
            }

            block[2] = s.RumbleRight;
            block[3] = s.RumbleLeft;
        }

        block[5] = s.SpeakerVolume;
        block[8] = (byte)s.MicLed;
        if (o.AudioStream)
        {
            block[0] |= HeadphoneVolumeEnable | AudioControlEnable;
            block[1] |= AudioControl2Enable;
            block[4] = 0x3F;        // headphone volume
            block[7] = 0x30;        // output path: speaker
            block[37] = 0x02;       // speaker preamp gain
        }

        if (o.MicStream)
        {
            block[0] |= MicVolumeEnable;
            block[1] |= PowerSaveEnable;
            block[6] = Math.Min(o.MicGain, (byte)0x40);
            block[9] = o.MicMuted ? MicMuteBit : (byte)0;
        }

        s.RightTrigger.Bytes.CopyTo(block[10..]);
        s.LeftTrigger.Bytes.CopyTo(block[21..]);
        if (o.FirstReport)
        {
            // Releases the lightbar from the controller's own power-on fade so our colour shows.
            block[38] |= LightbarSetupEnable;
            block[41] = 0x02;
        }

        block[42] = (byte)s.Brightness;
        block[43] = (byte)(s.PlayerLeds & 0x1F);
        block[44] = s.Lightbar.R;
        block[45] = s.Lightbar.G;
        block[46] = s.Lightbar.B;
    }

    /// <summary>Which fields a 47-byte state block (e.g. from a game) asks to change.</summary>
    public static OutputFields ValidFields(ReadOnlySpan<byte> block)
    {
        if (block.Length < StateLength)
        {
            return OutputFields.None;
        }

        OutputFields f = OutputFields.None;
        if ((block[0] & (CompatibleRumble | RumbleNotHaptics)) != 0 || (block[38] & CompatibleRumbleV2) != 0)
        {
            f |= OutputFields.Rumble;
        }

        if ((block[0] & RightTriggerEffect) != 0)
        {
            f |= OutputFields.RightTrigger;
        }

        if ((block[0] & LeftTriggerEffect) != 0)
        {
            f |= OutputFields.LeftTrigger;
        }

        if ((block[1] & MicLedEnable) != 0)
        {
            f |= OutputFields.MicLed;
        }

        if ((block[1] & LightbarEnable) != 0)
        {
            f |= OutputFields.Lightbar;
        }

        if ((block[1] & PlayerLedsEnable) != 0)
        {
            f |= OutputFields.PlayerLeds;
        }

        if ((block[38] & BrightnessEnable) != 0)
        {
            f |= OutputFields.Brightness;
        }

        return f;
    }

    /// <summary>
    /// Reads a 47-byte state block written by someone else and applies only the fields whose
    /// valid flags are set; everything else stays as in <paramref name="current"/>.
    /// </summary>
    public static OutputState ApplyState(ReadOnlySpan<byte> block, OutputState current)
    {
        OutputFields f = ValidFields(block);
        OutputState s = current;
        if (f.HasFlag(OutputFields.Rumble))
        {
            s = s with { RumbleRight = block[2], RumbleLeft = block[3] };
        }

        if (f.HasFlag(OutputFields.RightTrigger))
        {
            s = s with { RightTrigger = TriggerEffect.FromBytes(block.Slice(10, TriggerEffect.Size)) };
        }

        if (f.HasFlag(OutputFields.LeftTrigger))
        {
            s = s with { LeftTrigger = TriggerEffect.FromBytes(block.Slice(21, TriggerEffect.Size)) };
        }

        if (f.HasFlag(OutputFields.MicLed))
        {
            s = s with { MicLed = Enum.IsDefined((MicLed)block[8]) ? (MicLed)block[8] : MicLed.Off };
        }

        if (f.HasFlag(OutputFields.Lightbar))
        {
            s = s with { Lightbar = new Rgb(block[44], block[45], block[46]) };
        }

        if (f.HasFlag(OutputFields.PlayerLeds))
        {
            s = s with { PlayerLeds = (byte)(block[43] & 0x1F) };
        }

        if (f.HasFlag(OutputFields.Brightness))
        {
            s = s with { Brightness = Enum.IsDefined((LedBrightness)block[42]) ? (LedBrightness)block[42] : LedBrightness.High };
        }

        return s;
    }

    public static byte[] BuildUsb(OutputState s, OutputOptions? options = null)
    {
        byte[] r = new byte[UsbLength];
        r[0] = 0x02;
        WriteState(s, r.AsSpan(1), options);
        return r;
    }

    /// <param name="sequence">0-15, incremented by the caller per report.</param>
    public static byte[] BuildBluetooth(OutputState s, int sequence, OutputOptions? options = null)
    {
        byte[] r = new byte[BluetoothLength];
        r[0] = 0x31;
        r[1] = (byte)((sequence & 0x0F) << 4);
        r[2] = 0x10;
        WriteState(s, r.AsSpan(3), options);
        Crc32.Stamp(Crc32.OutputSeed, r);
        return r;
    }
}
