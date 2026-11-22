using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

/// <summary>One stick as held keys (with diagonals), cursor movement or scrolling.</summary>
internal sealed class StickMapper
{
    /// <summary>Full tilt at speed 1.</summary>
    public const double PixelsPerSecond = 1200;
    public const double NotchesPerSecond = 15;

    private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    private readonly StickMapping _config;
    private readonly HeldOutputs _outputs;
    private readonly PointerOutput _pointer;
    private readonly ActionDispatcher _actions;
    private readonly bool _bypassMode;
    // Up, down, left, right: the key each direction currently holds.
    private readonly Key[] _held = new Key[4];
    private readonly bool[] _blockedDirections = new bool[4];
    private float _x;
    private float _y;
    private TimeSpan? _last;
    private bool _ringActive;
    private bool _outsideRing;
    private bool _ringBlocked;
    private HeldAction? _ringHeld;

    public StickMapper(StickMapping config, StickProcessor processor, HeldOutputs outputs, PointerOutput pointer,
        ActionDispatcher actions, bool bypassMode = false)
    {
        _config = config;
        Processor = processor;
        _outputs = outputs;
        _pointer = pointer;
        _actions = actions;
        _bypassMode = bypassMode;
    }

    /// <summary>Replaced when the profile's stick shaping changes but the mapping does not.</summary>
    public StickProcessor Processor { get; set; }

    public void Input(Stick raw, TimeSpan now)
    {
        (_x, _y) = Processor.Process(raw);
        UpdateRing(now);
        if (!_bypassMode && _config.Mode == StickMappingMode.Keys)
        {
            double t = _config.Threshold;
            StickKeys k = _config.Keys;
            UpdateDirection(0, _y > t, k.Up);
            UpdateDirection(1, _y < -t, k.Down);
            UpdateDirection(2, _x < -t, k.Left);
            UpdateDirection(3, _x > t, k.Right);
        }

        Update(now);
    }

    public void Update(TimeSpan now)
    {
        if (_bypassMode)
        {
            return;
        }

        switch (_config.Mode)
        {
            case StickMappingMode.Mouse:
                double s = Step(now) * PixelsPerSecond * _config.Speed;
                _pointer.Move(_x * s, -_y * s);
                return;
            case StickMappingMode.Scroll:
                double w = Step(now) * NotchesPerSecond * 120 * _config.Speed;
                _pointer.Scroll(_y * w, _x * w);
                return;
        }

        _last = now;
    }

    private void UpdateRing(TimeSpan now)
    {
        if (_config.RingMode == StickRingMode.Off)
        {
            return;
        }

        double radius = Math.Min(1, Math.Sqrt((double)_x * _x + (double)_y * _y));
        double threshold = _config.RingThreshold;
        double release = Math.Max(threshold - _config.RingHysteresis, 0.01);
        bool active;
        if (_config.RingMode == StickRingMode.Outer)
        {
            active = radius >= (_ringActive || _ringBlocked ? release : threshold);
        }
        else
        {
            _outsideRing = radius >= threshold || (_outsideRing && radius > release);
            active = radius > 0 && !_outsideRing;
        }

        _ringBlocked &= active;
        if (!_ringBlocked && active != _ringActive)
        {
            _ringActive = active;
            if (active)
            {
                _ringHeld = _actions.Down(_config.RingAction, now);
            }
            else
            {
                _actions.Up(_ringHeld);
                _ringHeld = null;
            }
        }
    }

    public void ReleaseAll(bool reset = false, bool preserveMotion = false)
    {
        for (int i = 0; i < _held.Length; i++)
        {
            _blockedDirections[i] = !reset && (_blockedDirections[i] || _held[i] != Key.None);
            Set(i, Key.None);
        }

        _actions.Up(_ringHeld);
        _ringHeld = null;
        _ringBlocked = !reset && (_ringBlocked || _ringActive);
        _ringActive = false;
        if (reset)
        {
            _outsideRing = false;
        }

        if (!preserveMotion)
        {
            _last = null;
            _x = _y = 0;
        }
    }

    private void UpdateDirection(int direction, bool down, Key key)
    {
        _blockedDirections[direction] &= down;
        Set(direction, down && !_blockedDirections[direction] ? key : Key.None);
    }

    private double Step(TimeSpan now)
    {
        TimeSpan dt = _last is { } last ? now - last : TimeSpan.Zero;
        _last = now;
        if (dt <= TimeSpan.Zero)
        {
            return 0;
        }

        return (dt > MaxStep ? MaxStep : dt).TotalSeconds;
    }

    private void Set(int direction, Key key)
    {
        if (_held[direction] == key)
        {
            return;
        }

        if (_held[direction] != Key.None)
        {
            _outputs.Release(_held[direction]);
        }

        _held[direction] = key;
        if (key != Key.None)
        {
            _outputs.Press(key);
        }
    }
}
