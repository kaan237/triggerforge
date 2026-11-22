using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Gyro rotation to cursor movement: smoothing, deadzone, sensitivity, inversion.</summary>
internal sealed class GyroMouse
{
    /// <summary>Sensitivity 1 = this many pixels per degree.</summary>
    public const double PixelsPerDegree = 10;

    // Axis signs, kept in one place until checked on hardware.
    private const double PitchToY = -1;
    private const double YawToX = -1;
    private const double RollToX = -1;

    private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(50);

    private readonly GyroMouseConfig _config;
    private readonly PointerOutput _pointer;
    private readonly double _countsPerDegree;
    private double _sx;
    private double _sy;
    private TimeSpan? _last;

    public GyroMouse(GyroMouseConfig config, PointerOutput pointer, int countsPer360 = 3600)
    {
        _config = config;
        _pointer = pointer;
        _countsPerDegree = config.UseCalibration ? countsPer360 / 360.0 : PixelsPerDegree;
    }

    /// <param name="degPerSec">Calibrated, drift removed.</param>
    /// <param name="enabled">False resets the filter so the next start does not jump.</param>
    public void Input(Vector3 degPerSec, bool enabled, TimeSpan now)
    {
        if (!enabled)
        {
            Reset();
            return;
        }

        // Cursor-space rates in deg/s.
        double rx = (_config.Axis == GyroAxis.Roll ? RollToX * degPerSec.Z : YawToX * degPerSec.Y) * (_config.InvertX ? -1 : 1);
        double ry = PitchToY * degPerSec.X * (_config.InvertY ? -1 : 1);

        double a = Math.Clamp(_config.Smoothing, 0, 0.9);
        if (_last is null)
        {
            (_sx, _sy) = (rx, ry);
        }
        else
        {
            _sx = a * _sx + (1 - a) * rx;
            _sy = a * _sy + (1 - a) * ry;
        }

        TimeSpan dt = _last is { } last ? now - last : TimeSpan.Zero;
        _last = now;
        if (dt <= TimeSpan.Zero)
        {
            return;
        }

        if (dt > MaxStep)
        {
            dt = MaxStep;
        }

        if (Math.Sqrt(_sx * _sx + _sy * _sy) < _config.Deadzone)
        {
            return;
        }

        double seconds = dt.TotalSeconds;
        _pointer.Move(
            _sx * seconds * _countsPerDegree * _config.SensitivityX,
            _sy * seconds * _countsPerDegree * _config.SensitivityY);
    }

    public void Reset()
    {
        _sx = _sy = 0;
        _last = null;
    }
}
