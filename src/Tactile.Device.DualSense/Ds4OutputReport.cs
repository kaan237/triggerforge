namespace Tactile.Device.DualSense;

/// <summary>Builds DualShock 4 output reports (USB 0x05, Bluetooth 0x11). Trigger, player LED, mic LED and speaker fields do not exist here.</summary>
public static class Ds4OutputReport
{
    public const int UsbLength = 32;
    public const int BluetoothLength = 78;

    private const byte Flags = 0x07;   // rumble, lightbar, blink

    public static byte[] BuildUsb(OutputState state)
    {
        byte[] report = new byte[UsbLength];
        report[0] = 0x05;
        report[1] = Flags;
        report[4] = state.RumbleRight;
        report[5] = state.RumbleLeft;
        WriteColor(report.AsSpan(6, 3), state);
        return report;
    }

    public static byte[] BuildBluetooth(OutputState state)
    {
        byte[] report = new byte[BluetoothLength];
        report[0] = 0x11;
        report[1] = 0xC0;
        report[3] = Flags;
        report[6] = state.RumbleRight;
        report[7] = state.RumbleLeft;
        WriteColor(report.AsSpan(8, 3), state);
        Crc32.Stamp(Crc32.OutputSeed, report);
        return report;
    }

    public static Rgb ScaleBrightness(Rgb color, LedBrightness brightness)
    {
        double factor = brightness switch
        {
            LedBrightness.Medium => 0.62,
            LedBrightness.Low => 0.30,
            _ => 1.0,
        };

        return new Rgb(Scale(color.R, factor), Scale(color.G, factor), Scale(color.B, factor));
    }

    /// <summary>True when nothing a DualShock 4 can show differs.</summary>
    public static bool SameEffect(OutputState a, OutputState? b) =>
        b is not null &&
        a.Lightbar == b.Lightbar && a.Brightness == b.Brightness &&
        a.RumbleLeft == b.RumbleLeft && a.RumbleRight == b.RumbleRight;

    private static void WriteColor(Span<byte> rgb, OutputState state)
    {
        Rgb c = ScaleBrightness(state.Lightbar, state.Brightness);
        rgb[0] = c.R;
        rgb[1] = c.G;
        rgb[2] = c.B;
    }

    private static byte Scale(byte value, double factor) => (byte)Math.Clamp((int)Math.Round(value * factor), 0, 255);
}
