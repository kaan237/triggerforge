namespace Tactile.Device.DualSense;

/// <param name="AudioStream">Bluetooth haptics/audio stream is open: route to the speaker path and enable audio fields.</param>
public readonly record struct OutputOptions(bool FirstReport = false, bool RumbleV2 = true, bool Rumble = true, bool AudioStream = false)
{
    // `new()` on a record struct ignores the primary constructor defaults, so spell them out.
    public static OutputOptions Default { get; } = new(FirstReport: false);
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
    private const byte AudioControlEnable = 0x80;
    // Flags B (offset 1)
    private const byte MicLedEnable = 0x01;
    private const byte LightbarEnable = 0x04;
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
