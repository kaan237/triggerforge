namespace Tactile.Device.DualSense;

/// <summary>Feature reports 0x05, 0x09 and 0x20 as a USB controller returns them (report id first, no CRC).</summary>
public sealed record UsbFeatureReports(byte[]? Calibration, byte[]? Pairing, byte[]? Firmware)
{
    public static UsbFeatureReports None { get; } = new(null, null, null);
}

/// <summary>Turns feature reports read over either link into their USB form. Sizes: docs/protocol.md.</summary>
public static class FeatureReport
{
    public const byte Calibration = 0x05;
    public const byte Pairing = 0x09;
    public const byte Firmware = 0x20;

    /// <summary>USB size including the id byte, or 0 for ids not handled here.</summary>
    public static int UsbLength(byte id) => id switch
    {
        Calibration => 41,
        Pairing => 20,
        Firmware => 64,
        _ => 0,
    };

    /// <summary>
    /// <paramref name="raw"/> is the buffer Windows filled, id first. Over Bluetooth the payload is
    /// four bytes shorter than over USB and the CRC sits where USB has its last four bytes; the CRC
    /// is checked and those bytes are zeroed. Null when the id is unknown, the buffer too short or
    /// the CRC wrong.
    /// </summary>
    public static byte[]? ToUsb(ReadOnlySpan<byte> raw, Link link)
    {
        if (raw.IsEmpty)
        {
            return null;
        }

        int length = UsbLength(raw[0]);
        if (length == 0 || raw.Length < length)
        {
            return null;
        }

        byte[] usb = raw[..length].ToArray();
        if (link == Link.Bluetooth)
        {
            if (!Crc32.Verify(Crc32.FeatureSeed, usb))
            {
                return null;
            }

            usb.AsSpan(length - 4).Clear();
        }

        return usb;
    }
}
