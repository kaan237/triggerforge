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
    // Up, down, left, right: the key each direction currently holds.
    private readonly Key[] _held = new Key[4];
    private float _x;
    private float _y;
    private TimeSpan? _last;

    public StickMapper(StickMapping config, StickProcessor processor, HeldOutputs outputs, PointerOutput pointer)
    {
        _config = config;
        Processor = processor;
        _outputs = outputs;
        _pointer = pointer;
    }

    /// <summary>Replaced when the profile's stick shaping changes but the mapping does not.</summary>
    public StickProcessor Processor { get; set; }

    public void Input(Stick raw, TimeSpan now)
    {
        (_x, _y) = Processor.Process(raw);
        Update(now);
    }

    public void Update(TimeSpan now)
    {
        switch (_config.Mode)
        {
            case StickMappingMode.Keys:
                double t = _config.Threshold;
                StickKeys k = _config.Keys;
                Set(0, _y > t ? k.Up : Key.None);
                Set(1, _y < -t ? k.Down : Key.None);
                Set(2, _x < -t ? k.Left : Key.None);
                Set(3, _x > t ? k.Right : Key.None);
                break;
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

    public void ReleaseAll()
    {
        for (int i = 0; i < _held.Length; i++)
        {
            Set(i, Key.None);
        }

        _last = null;
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
