using Tactile.Device.DualSense;

namespace Tactile.Core;

public sealed record GyroCalibrationResult(bool Success, Vector3 Bias, string? Reason)
{
    /// <summary>Gyro in deg/s with the measured resting drift removed.</summary>
    public static Vector3 Apply(Vector3 degPerSec, Vector3 bias) =>
        new(degPerSec.X - bias.X, degPerSec.Y - bias.Y, degPerSec.Z - bias.Z);
}

/// <summary>
/// Averages gyro readings (deg/s, after <see cref="ImuCalibration"/>) while the controller lies
/// still. The mean is the drift to subtract. Feed it samples until it returns a result.
/// </summary>
public sealed class GyroCalibrator
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);

    /// <summary>Per-axis standard deviation in deg/s above which the controller counts as moved.</summary>
    public const double DefaultMaxStdDev = 1.0;

    /// <summary>A resting drift larger than this means it was turning steadily, not resting.</summary>
    public const double MaxBias = 10.0;

    public const int MinSamples = 30;

    private readonly TimeSpan _duration;
    private readonly double _maxStdDev;
    private readonly Action<double>? _progress;
    private readonly Axis[] _axes = [new(), new(), new()];
    private TimeSpan? _start;
    private int _lastStep = -1;

    public GyroCalibrator(TimeSpan? duration = null, double maxStdDev = DefaultMaxStdDev, Action<double>? progress = null)
    {
        _duration = duration ?? DefaultDuration;
        _maxStdDev = maxStdDev;
        _progress = progress;
    }

    public GyroCalibrationResult? Result { get; private set; }

    public int Samples => _axes[0].Count;

    /// <param name="time">Any monotonic clock.</param>
    /// <returns>The result once the duration has passed, otherwise null.</returns>
    public GyroCalibrationResult? Add(Vector3 degPerSec, TimeSpan time)
    {
        if (Result is not null)
        {
            return Result;
        }

        _start ??= time;
        _axes[0].Add(degPerSec.X);
        _axes[1].Add(degPerSec.Y);
        _axes[2].Add(degPerSec.Z);

        double progress = Math.Clamp((time - _start.Value) / _duration, 0, 1);
        // Report in 10 % steps; samples arrive at a few hundred per second.
        int step = (int)(progress * 10);
        if (step != _lastStep && progress < 1)
        {
            _lastStep = step;
            _progress?.Invoke(progress);
        }

        if (progress < 1)
        {
            return null;
        }

        Result = Finish();
        _progress?.Invoke(1);
        return Result;
    }

    /// <summary>Ends early, e.g. when no samples arrive.</summary>
    public GyroCalibrationResult Fail(string reason)
    {
        Result ??= new GyroCalibrationResult(false, default, reason);
        return Result;
    }

    private GyroCalibrationResult Finish()
    {
        if (Samples < MinSamples)
        {
            return new(false, default, Text.Pick("Not enough gyro data.", "Yeterli gyro verisi gelmedi."));
        }

        if (_axes.Any(a => a.StdDev > _maxStdDev))
        {
            return new(false, default, Text.Pick("The controller moved. Set it on a flat surface and try again.", "Kontrolcü hareket etti; düz bir yüzeye koyup tekrar deneyin."));
        }

        var bias = new Vector3((float)_axes[0].Mean, (float)_axes[1].Mean, (float)_axes[2].Mean);
        if (_axes.Any(a => Math.Abs(a.Mean) > MaxBias))
        {
            return new(false, default, Text.Pick("The controller appears to be turning. Keep it still and try again.", "Kontrolcü dönüyor gibi; sabit durduğundan emin olun."));
        }

        return new(true, bias, null);
    }

    // Running mean and variance (Welford).
    private sealed class Axis
    {
        private double _m2;

        public int Count { get; private set; }

        public double Mean { get; private set; }

        public double StdDev => Count > 1 ? Math.Sqrt(_m2 / (Count - 1)) : 0;

        public void Add(double v)
        {
            Count++;
            double d = v - Mean;
            Mean += d / Count;
            _m2 += d * (v - Mean);
        }
    }
}
