using Tactile.Device.DualSense;

namespace Tactile.Core;

internal sealed class FlickStick(FlickStickConfig config, PointerOutput pointer)
{
    private static readonly TimeSpan TurnDuration = TimeSpan.FromMilliseconds(200);

    private bool _engaged;
    private bool _blocked;
    private double _angle;
    private double _sweepRemainder;
    private TimeSpan? _sweepAt;
    private TimeSpan? _flickAt;
    private double _flickCounts;
    private double _flickSent;
    private TimeSpan? _turnAt;
    private int _turnCounts;
    private int _turnSent;

    public void Input(Stick raw, TimeSpan now)
    {
        (float x, float y) = raw.Normalized;
        double radius = Math.Sqrt(x * x + y * y);
        double release = config.Threshold - 0.05;
        if (_blocked)
        {
            _blocked = radius >= release;
            return;
        }

        if (radius < (_engaged ? release : config.Threshold))
        {
            _engaged = false;
            return;
        }

        // Either of the center byte values represents zero on an axis.
        double angle = Math.Atan2(raw.X is 127 or 128 ? 0 : x, raw.Y is 127 or 128 ? 0 : y) * 180 / Math.PI;
        if (_engaged)
        {
            double delta = angle - _angle;
            _sweepRemainder += delta <= -180 ? delta + 360 : delta > 180 ? delta - 360 : delta;
        }
        else
        {
            AdvanceFlick(now);
            _engaged = true;
            _flickAt = now;
            _flickSent = 0;
            double step = config.Snap == FlickSnapMode.Four ? 90 : 45;
            double target = Math.Abs(angle) <= config.DeadzoneAngle ? 0 : config.Snap == FlickSnapMode.Off
                ? angle
                : Math.Round(angle / step, MidpointRounding.AwayFromZero) * step;
            _flickCounts = target * config.CountsPer360 / 360;
        }

        _angle = angle;
    }

    public void StartTestTurn(int counts, int delayMs, TimeSpan now)
    {
        _turnCounts = Math.Clamp(counts, 500, 30000);
        _turnAt = now + TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, 5000));
        _turnSent = 0;
    }

    public void Update(TimeSpan now)
    {
        AdvanceFlick(now);
        if (_sweepAt != now && _sweepRemainder != 0)
        {
            _sweepAt = now;
            double step = Math.Abs(_sweepRemainder) < 0.000001 ? _sweepRemainder : _sweepRemainder * (1 - config.Smoothing);
            _sweepRemainder -= step;
            pointer.Move(step * config.CountsPer360 / 360, 0);
        }

        if (_turnAt is { } start && now >= start)
        {
            double fraction = Math.Min(1, (now - start) / TurnDuration);
            int target = (int)Math.Round(_turnCounts * fraction);
            pointer.MoveCounts(target - _turnSent);
            _turnSent = target;
        }
    }

    public int? FinishTestTurn()
    {
        if (_turnAt is null || _turnSent != _turnCounts)
        {
            return null;
        }

        _turnAt = null;
        return _turnCounts;
    }

    public void ReleaseAll(bool reset = false)
    {
        _blocked = !reset && (_engaged || _blocked);
        _engaged = false;
        _sweepRemainder = 0;
        _sweepAt = _flickAt = _turnAt = null;
    }

    private void AdvanceFlick(TimeSpan now)
    {
        if (_flickAt is not { } start)
        {
            return;
        }

        double fraction = Math.Clamp((now - start).TotalMilliseconds / config.FlickTimeMs, 0, 1);
        double target = _flickCounts * fraction;
        pointer.Move(target - _flickSent, 0);
        _flickSent = target;
        if (fraction == 1)
        {
            _flickAt = null;
        }
    }
}
