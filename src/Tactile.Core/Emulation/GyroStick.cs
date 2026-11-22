using Tactile.Device.DualSense;

namespace Tactile.Core;

internal sealed class GyroStick(GyroStickConfig config)
{
    private readonly Buttons _button = ButtonNames.TryParse(config.Button, out Buttons button) ? button : Buttons.L2;
    private bool _held;
    private bool _latched;
    private double _x;
    private double _y;
    private TimeSpan? _last;

    public (float X, float Y) Input(Vector3 gyroDegPerSec, InputState input, TimeSpan now)
    {
        bool held = input.IsDown(_button);
        if (held && !_held && config.Mode == GyroMouseMode.Toggle)
        {
            _latched = !_latched;
        }

        _held = held;
        bool enabled = config.Mode switch
        {
            GyroMouseMode.Always => true,
            GyroMouseMode.WhileHeld => held,
            GyroMouseMode.WhileReleased => !held,
            GyroMouseMode.Toggle => _latched,
            GyroMouseMode.WhileTouchpad => input.Touch1.Active || input.Touch2.Active,
            _ => false,
        };
        if (!enabled)
        {
            _last = null;
            return default;
        }

        double x = -(config.Axis == GyroAxis.Roll ? gyroDegPerSec.Z : gyroDegPerSec.Y);
        double y = gyroDegPerSec.X;
        double smoothing = _last is { } last && now >= last && now - last <= TimeSpan.FromMilliseconds(50)
            ? config.Smoothing : 0;
        _last = now;
        _x = _x * smoothing + x * (1 - smoothing);
        _y = _y * smoothing + y * (1 - smoothing);

        return (Deflection(_x, config.SensitivityX, config.InvertX),
            Deflection(_y, config.SensitivityY, config.InvertY));
    }

    private float Deflection(double rate, double sensitivity, bool invert)
    {
        double magnitude = Math.Abs(rate);
        if (magnitude <= config.Deadzone)
        {
            return 0;
        }

        double scaled = Math.Min(1, magnitude * sensitivity / 120);
        double boosted = config.AntiDeadzone + (1 - config.AntiDeadzone) * scaled;
        return (float)Math.CopySign(boosted, invert ? -rate : rate);
    }
}
