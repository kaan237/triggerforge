using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Lightbar colour for a moment in time. Pure; the engine calls it 30 times a second.</summary>
public static class LightbarEngine
{
    // Cycles per second at speed 1.
    public const double BreatheRate = 0.5;
    public const double StrobeRate = 2.0;
    public const double RainbowRate = 0.1;
    public const double LowBatteryPulseRate = 1.0;

    public const int LowBatteryPercent = 10;

    /// <summary>Player 1-4: blue, red, green, pink.</summary>
    public static IReadOnlyList<Rgb> PlayerPalette { get; } =
    [
        new(0x00, 0x40, 0xFF),
        new(0xFF, 0x00, 0x00),
        new(0x00, 0xFF, 0x00),
        new(0xFF, 0x40, 0xC0),
    ];

    /// <param name="seconds">Any monotonic clock; effects are periodic.</param>
    /// <param name="batteryPercent">Null when unknown.</param>
    public static (Rgb Color, LedBrightness Brightness) Evaluate(LightbarConfig config, double seconds, int? batteryPercent, int playerNumber)
    {
        Rgb a = Parse(config.Color, new Rgb(0x30, 0x60, 0xFF));
        Rgb b = Parse(config.Color2, default);
        double speed = double.IsFinite(config.Speed) ? Math.Clamp(config.Speed, 0.1, 5) : 1;

        Rgb color = config.Effect switch
        {
            LightbarEffect.Off => default,
            LightbarEffect.Static => a,
            LightbarEffect.Breathe => Mix(a, b, (1 - Math.Cos(2 * Math.PI * Phase(seconds, BreatheRate * speed))) / 2),
            LightbarEffect.Strobe => Phase(seconds, StrobeRate * speed) < 0.5 ? a : b,
            LightbarEffect.Rainbow => Hsv(360 * Phase(seconds, RainbowRate * speed), 1, 1),
            LightbarEffect.Battery => Battery(batteryPercent, seconds, speed, a),
            LightbarEffect.PlayerColor => PlayerPalette[((Math.Max(playerNumber, 1) - 1) % PlayerPalette.Count)],
            _ => a,
        };

        return (color, config.Brightness);
    }

    /// <summary>Green at 80 % and up, through yellow, to red at 10 %; pulses red below that.</summary>
    private static Rgb Battery(int? percent, double seconds, double speed, Rgb unknown)
    {
        if (percent is not int p)
        {
            return unknown;
        }

        if (p < LowBatteryPercent)
        {
            double k = (1 - Math.Cos(2 * Math.PI * Phase(seconds, LowBatteryPulseRate * speed))) / 2;
            return Mix(new Rgb(255, 0, 0), new Rgb(40, 0, 0), k);
        }

        double t = Math.Clamp((p - 10) / 70.0, 0, 1);
        return Hsv(120 * t, 1, 1);
    }

    private static double Phase(double seconds, double rate)
    {
        double x = seconds * rate;
        return x - Math.Floor(x);
    }

    private static Rgb Mix(Rgb a, Rgb b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <param name="hue">Degrees.</param>
    public static Rgb Hsv(double hue, double s, double v)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static Rgb Parse(string? hex, Rgb fallback)
    {
        try
        {
            return hex is null ? fallback : Rgb.FromHex(hex);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return fallback;
        }
    }
}
