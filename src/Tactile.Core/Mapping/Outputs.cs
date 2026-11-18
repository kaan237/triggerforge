using Tactile.Input;

namespace Tactile.Core;

/// <summary>
/// Keys and mouse buttons with a reference count each: a key goes down with its first holder
/// and up with its last. Shared by the input thread and the macro thread.
/// </summary>
internal sealed class HeldOutputs
{
    private readonly Lock _lock = new();
    private readonly IInputInjector _injector;
    private readonly Dictionary<Key, int> _keys = [];
    private readonly Dictionary<MouseButton, int> _buttons = [];

    public HeldOutputs(IInputInjector injector) => _injector = injector;

    public IInputInjector Injector => _injector;

    public void Press(Key key)
    {
        lock (_lock)
        {
            int n = _keys.GetValueOrDefault(key);
            _keys[key] = n + 1;
            if (n == 0)
            {
                _injector.Key(key, true);
            }
        }
    }

    public void Release(Key key)
    {
        lock (_lock)
        {
            int n = _keys.GetValueOrDefault(key);
            if (n == 0)
            {
                return;
            }

            if (n == 1)
            {
                _keys.Remove(key);
                _injector.Key(key, false);
            }
            else
            {
                _keys[key] = n - 1;
            }
        }
    }

    public void Press(MouseButton button)
    {
        lock (_lock)
        {
            int n = _buttons.GetValueOrDefault(button);
            _buttons[button] = n + 1;
            if (n == 0)
            {
                _injector.MouseButton(button, true);
            }
        }
    }

    public void Release(MouseButton button)
    {
        lock (_lock)
        {
            int n = _buttons.GetValueOrDefault(button);
            if (n == 0)
            {
                return;
            }

            if (n == 1)
            {
                _buttons.Remove(button);
                _injector.MouseButton(button, false);
            }
            else
            {
                _buttons[button] = n - 1;
            }
        }
    }

    /// <summary>Safety net after every holder let go: anything still down goes up.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (Key k in _keys.Keys.Reverse().ToArray())
            {
                _injector.Key(k, false);
            }

            foreach (MouseButton b in _buttons.Keys.ToArray())
            {
                _injector.MouseButton(b, false);
            }

            _keys.Clear();
            _buttons.Clear();
        }
    }
}

/// <summary>
/// Sums cursor moves and smooth scrolling from touchpad, gyro and sticks, keeps the sub-pixel
/// remainder, and sends at most once per <see cref="MinInterval"/>.
/// </summary>
internal sealed class PointerOutput
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(4);

    private readonly IInputInjector _injector;
    private double _x;
    private double _y;
    private double _wheel;
    private double _hwheel;
    private TimeSpan? _lastSend;

    public PointerOutput(IInputInjector injector) => _injector = injector;

    /// <summary>Pixels.</summary>
    public void Move(double dx, double dy)
    {
        _x += dx;
        _y += dy;
    }

    /// <summary>Wheel units (120 = one notch). Positive is up / right.</summary>
    public void Scroll(double vertical, double horizontal)
    {
        _wheel += vertical;
        _hwheel += horizontal;
    }

    public void Flush(TimeSpan now)
    {
        if (_lastSend is { } last && now - last < MinInterval)
        {
            return;
        }

        int dx = (int)Math.Truncate(_x);
        int dy = (int)Math.Truncate(_y);
        int wv = (int)Math.Truncate(_wheel);
        int wh = (int)Math.Truncate(_hwheel);
        if (dx == 0 && dy == 0 && wv == 0 && wh == 0)
        {
            return;
        }

        if (dx != 0 || dy != 0)
        {
            _injector.MouseMove(dx, dy);
            _x -= dx;
            _y -= dy;
        }

        if (wv != 0)
        {
            _injector.Wheel(wv, horizontal: false);
            _wheel -= wv;
        }

        if (wh != 0)
        {
            _injector.Wheel(wh, horizontal: true);
            _hwheel -= wh;
        }

        _lastSend = now;
    }

    public void Reset()
    {
        _x = _y = _wheel = _hwheel = 0;
        _lastSend = null;
    }
}
