using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

/// <summary>Touchpad as mouse (move, scroll, tap to click) or as click zones with swipes.</summary>
internal sealed class TouchpadMapper
{
    public const double PadWidth = 1920;
    public const double PadHeight = 1080;
    /// <summary>Full pad width at sensitivity 1, no acceleration.</summary>
    public const double PixelsPerWidth = 900;
    /// <summary>Smooth scroll: a full pad height = this many wheel units at scroll speed 1.</summary>
    public const double WheelPerHeight = 1200;

    public static readonly TimeSpan TapMaxDuration = TimeSpan.FromMilliseconds(200);
    public const double TapMaxTravel = 0.03 * PadWidth;
    public static readonly TimeSpan SwipeMaxDuration = TimeSpan.FromMilliseconds(500);
    public const double SwipeMinFraction = 0.25;

    private readonly TouchpadMapping _config;
    private readonly ActionDispatcher _actions;
    private readonly PointerOutput _pointer;
    private readonly ButtonMachine[] _zones;

    // Finger tracking: last position per slot, for move deltas.
    private (byte Id, double X, double Y)? _last1;
    private (byte Id, double X, double Y)? _last2;
    private TimeSpan _lastTime;

    // The current touch session (first finger down to last finger up).
    private bool _touching;
    private TimeSpan _touchStart;
    private double _startX;
    private double _startY;
    private double _endX;
    private double _endY;
    private double _travel;
    private int _maxFingers;
    private bool _clickedDuringTouch;

    private bool _clickDown;
    private int _clickZone = -1;
    private int _sessionZone = -1;

    public TouchpadMapper(TouchpadMapping config, ActionDispatcher actions, PointerOutput pointer)
    {
        _config = config;
        _actions = actions;
        _pointer = pointer;
        _zones = config.Zones.Select(_ => new ButtonMachine(actions)).ToArray();
    }

    /// <summary>In zones mode the pad click goes to the zones, not to <c>buttons.touchpad</c>.</summary>
    public bool HandlesClick => _config.Mode == TouchpadMode.Zones;

    public static int ZoneAt(ZoneLayout layout, double x, double y)
    {
        int col = x >= PadWidth / 2 ? 1 : 0;
        return layout == ZoneLayout.Quadrants ? (y >= PadHeight / 2 ? 2 : 0) + col : col;
    }

    public void Input(TouchPoint t1, TouchPoint t2, bool click, TimeSpan now, bool deferZoneClick = false)
    {
        if (_config.Mode == TouchpadMode.Off)
        {
            return;
        }

        foreach (ButtonMachine z in _zones)
        {
            z.Update(now);
        }

        int fingers = (t1.Active ? 1 : 0) + (t2.Active ? 1 : 0);
        TrackSession(t1, t2, fingers, now);

        if (_config.Mode == TouchpadMode.Mouse)
        {
            if (click)
            {
                _clickedDuringTouch = true;
            }

            MoveOrScroll(t1, t2, fingers, now);
        }
        else if (!deferZoneClick)
        {
            ZoneClick(click ? ClickZone(t1.Active ? t1 : t2) : null, click, now);
        }

        if (_touching && fingers == 0)
        {
            EndSession(now);
        }

        _lastTime = now;
    }

    public void Update(TimeSpan now)
    {
        foreach (ButtonMachine z in _zones)
        {
            z.Update(now);
        }
    }

    public void ReleaseAll()
    {
        foreach (ButtonMachine z in _zones)
        {
            z.ReleaseAll();
        }

        _clickZone = -1;
        _clickDown = false;
        _touching = false;
        _last1 = _last2 = null;
    }

    private void TrackSession(TouchPoint t1, TouchPoint t2, int fingers, TimeSpan now)
    {
        if (fingers > 0 && !_touching)
        {
            _touching = true;
            _touchStart = now;
            TouchPoint first = t1.Active ? t1 : t2;
            (_startX, _startY) = (first.X, first.Y);
            (_endX, _endY) = (_startX, _startY);
            _travel = 0;
            _maxFingers = 0;
            _clickedDuringTouch = _clickDown;
            _sessionZone = -1;
        }

        if (!_touching || fingers == 0)
        {
            return;
        }

        _maxFingers = Math.Max(_maxFingers, fingers);
        if (t1.Active)
        {
            (_endX, _endY) = (t1.X, t1.Y);
            _travel = Math.Max(_travel, Math.Sqrt(Sq(t1.X - _startX) + Sq(t1.Y - _startY)));
        }
    }

    private void EndSession(TimeSpan now)
    {
        _touching = false;
        TimeSpan duration = now - _touchStart;
        if (_config.Mode == TouchpadMode.Mouse)
        {
            if (_config.TapToClick && !_clickedDuringTouch && duration < TapMaxDuration && _travel < TapMaxTravel)
            {
                _actions.Tap(ActionConfig.ForMouse(_maxFingers >= 2 ? MouseButton.Right : MouseButton.Left), now);
            }

            return;
        }

        if (_maxFingers != 1 || duration >= SwipeMaxDuration)
        {
            return;
        }

        double dx = _endX - _startX;
        double dy = _endY - _startY;
        double fx = Math.Abs(dx) / PadWidth;
        double fy = Math.Abs(dy) / PadHeight;
        if (fx <= SwipeMinFraction && fy <= SwipeMinFraction)
        {
            return;
        }

        SwipeActions s = _config.Swipes;
        ActionConfig swipe = fx >= fy ? (dx < 0 ? s.Left : s.Right) : (dy < 0 ? s.Up : s.Down);
        if (_sessionZone >= 0)
        {
            // A zone press that started during the swipe is cancelled.
            _zones[_sessionZone].ReleaseAll();
            if (_clickZone == _sessionZone)
            {
                _clickZone = -1;
            }
        }

        _actions.Tap(swipe, now);
    }

    private void MoveOrScroll(TouchPoint t1, TouchPoint t2, int fingers, TimeSpan now)
    {
        (byte, double, double)? p1 = t1.Active ? (t1.Id, t1.X, t1.Y) : null;
        (byte, double, double)? p2 = t2.Active ? (t2.Id, t2.X, t2.Y) : null;

        if (fingers == 1 && p1 is { } a && _last1 is { } la && la.Id == a.Item1 && _last2 is null)
        {
            double dx = a.Item2 - la.X;
            double dy = a.Item3 - la.Y;
            double seconds = Math.Max(0.001, (now - _lastTime).TotalSeconds);
            // Speed in pad widths per second, capped so a glitch cannot fling the cursor.
            double speed = Math.Min(3, Math.Sqrt(dx * dx + dy * dy) / PadWidth / seconds);
            double gain = PixelsPerWidth / PadWidth * _config.Sensitivity * (1 + _config.Acceleration * speed);
            _pointer.Move(dx * gain, dy * gain);
        }
        else if (fingers == 2 && _config.Scroll && p1 is { } b1 && p2 is { } b2 &&
                 _last1 is { } l1 && _last2 is { } l2 && l1.Id == b1.Item1 && l2.Id == b2.Item1)
        {
            double dx = (b1.Item2 - l1.X + b2.Item2 - l2.X) / 2;
            double dy = (b1.Item3 - l1.Y + b2.Item3 - l2.Y) / 2;
            double k = WheelPerHeight / PadHeight * _config.ScrollSpeed;
            // Content follows the fingers: moving down scrolls down (negative wheel).
            _pointer.Scroll(-dy * k, dx * k);
        }

        // Deltas only between reports with the same fingers, so no jump between move and scroll.
        _last1 = p1;
        _last2 = p2;
    }

    public int ClickZone(TouchPoint touch) => touch.Active
        ? ZoneAt(_config.ZoneLayout, touch.X, touch.Y)
        : ZoneAt(_config.ZoneLayout, _endX, _endY);

    public void ZoneClick(int? zone, bool click, TimeSpan now, TimeSpan? pressedAt = null)
    {
        if (click == _clickDown)
        {
            return;
        }

        _clickDown = click;
        if (_zones.Length == 0)
        {
            return;
        }

        if (click)
        {
            _clickZone = Math.Clamp(zone ?? ClickZone(default), 0, _zones.Length - 1);
            _zones[_clickZone].Down(_config.Zones[_clickZone], now, pressedAt: pressedAt);
            _sessionZone = _touching ? _clickZone : -1;
        }
        else if (_clickZone >= 0)
        {
            // The release goes to the zone that took the press.
            _zones[_clickZone].Up(now);
            _clickZone = -1;
        }
    }

    private static double Sq(double v) => v * v;
}
