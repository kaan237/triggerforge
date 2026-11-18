using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Deadzones, curve and inversion for one stick. Immutable; build a new one when the profile changes.</summary>
public sealed class StickProcessor
{
    public StickProcessor(StickConfig? config = null)
    {
        Config = config ?? new StickConfig();
    }

    public StickConfig Config { get; }

    /// <returns>-1..1 per axis, Y up positive.</returns>
    public (float X, float Y) Process(Stick raw)
    {
        (float x, float y) = raw.Normalized;
        return Process(x, y);
    }

    /// <param name="x">-1..1.</param>
    /// <param name="y">-1..1, up positive.</param>
    public (float X, float Y) Process(float x, float y)
    {
        double ox, oy;
        if (Config.Shape == StickShape.Square)
        {
            ox = Axis(x);
            oy = Axis(y);
        }
        else
        {
            double r = Math.Sqrt((double)x * x + (double)y * y);
            double m = Magnitude(r);
            (ox, oy) = r > 0 ? (x / r * m, y / r * m) : (0, 0);
        }

        if (Config.InvertX)
        {
            ox = -ox;
        }

        if (Config.InvertY)
        {
            oy = -oy;
        }

        return ((float)Math.Clamp(ox, -1, 1), (float)Math.Clamp(oy, -1, 1));
    }

    private double Axis(double v) => Math.CopySign(Magnitude(Math.Abs(v)), v);

    // Distance from centre (0..~1.41) to output magnitude 0..1.
    private double Magnitude(double r)
    {
        double inner = Config.InnerDeadzone;
        double edge = Math.Max(inner + 0.01, 1 - Config.OuterDeadzone);
        if (r <= inner)
        {
            return 0;
        }

        double t = Math.Min(1, (r - inner) / (edge - inner));
        double curved = ResponseCurve.Apply(Config.Curve, Config.Exponent, Config.Points, t);
        double anti = Math.Clamp(Config.AntiDeadzone, 0, 1);
        return anti + (1 - anti) * curved;
    }
}

/// <summary>Pressure shaping for one analog trigger, 0-255 in and out.</summary>
public sealed class TriggerResponseProcessor
{
    public TriggerResponseProcessor(TriggerResponse? config = null)
    {
        Config = config ?? new TriggerResponse();
    }

    public TriggerResponse Config { get; }

    public byte Process(byte raw)
    {
        int deadzone = Math.Clamp(Config.Deadzone, 0, 254);
        int max = Math.Clamp(Config.Max, deadzone + 1, 255);
        if (raw <= deadzone)
        {
            return 0;
        }

        if (raw >= max)
        {
            return 255;
        }

        double t = (raw - deadzone) / (double)(max - deadzone);
        return (byte)Math.Round(255 * ResponseCurve.Apply(Config.Curve, Config.Exponent, Config.Points, t));
    }
}
