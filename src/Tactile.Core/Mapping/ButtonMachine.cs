namespace Tactile.Core;

/// <summary>
/// The press-type state machine for one button or touchpad zone (contract rules 1-5).
/// The binding is taken at press time and kept until the press sequence ends, so a layer
/// change mid-press never moves the release elsewhere.
/// </summary>
internal sealed class ButtonMachine
{
    private enum Phase { Idle, Ignored, Plain, Turbo, Pending, Fired, WaitDouble }

    private readonly ActionDispatcher _actions;
    private Phase _phase;
    private Binding? _binding;
    private HeldAction? _active;
    private HeldAction? _latched;
    private TimeSpan _since;
    private TimeSpan _nextEdge;
    private bool _turboOn;

    public ButtonMachine(ActionDispatcher actions) => _actions = actions;

    public bool Idle => _phase == Phase.Idle && _latched is null;

    public void Down(Binding? binding, TimeSpan now)
    {
        Update(now);
        if (_phase == Phase.WaitDouble)
        {
            // Second press in time: double fires, held actions follow this press.
            _active = _actions.Down(_binding!.Double!, now);
            _phase = Phase.Fired;
            return;
        }

        if (_latched is not null)
        {
            // Toggle: the next press releases whatever latched, whatever the binding is now.
            _actions.Up(_latched);
            _latched = null;
            _phase = Phase.Ignored;
            return;
        }

        _binding = binding;
        if (binding is null)
        {
            _phase = Phase.Ignored;
            return;
        }

        if (binding.Hold is not null || binding.Double is not null)
        {
            _since = now;
            _phase = Phase.Pending;
            return;
        }

        if (binding.Turbo)
        {
            _phase = Phase.Turbo;
            _turboOn = true;
            _active = _actions.Down(binding.Press, now);
            _nextEdge = now + HalfPeriod(binding);
            return;
        }

        if (binding.Toggle)
        {
            // One-shots return null, so they fire on every press.
            _latched = _actions.Down(binding.Press, now);
            _phase = Phase.Ignored;
            return;
        }

        _active = _actions.Down(binding.Press, now);
        _phase = Phase.Plain;
    }

    public void Up(TimeSpan now)
    {
        Update(now);
        switch (_phase)
        {
            case Phase.Plain or Phase.Fired:
                _actions.Up(_active);
                break;
            case Phase.Turbo:
                if (_turboOn)
                {
                    _actions.Up(_active);
                }

                break;
            case Phase.Pending:
                if (_binding!.Double is not null)
                {
                    _since = now;
                    _phase = Phase.WaitDouble;
                    return;
                }

                _actions.Tap(_binding.Press, now);
                break;
            case Phase.WaitDouble:
                // Release without a press (state reset mid-press); keep waiting.
                return;
        }

        _active = null;
        _phase = Phase.Idle;
    }

    /// <summary>Timers: hold threshold, double-press window, turbo edges.</summary>
    public void Update(TimeSpan now)
    {
        switch (_phase)
        {
            case Phase.Pending when _binding!.Hold is { } hold && now - _since >= TimeSpan.FromMilliseconds(_binding.HoldMs):
                _active = _actions.Down(hold, now);
                _phase = Phase.Fired;
                break;
            case Phase.WaitDouble when now - _since > TimeSpan.FromMilliseconds(_binding!.DoubleMs):
                _actions.Tap(_binding.Press, now);
                _phase = Phase.Idle;
                break;
            case Phase.Turbo:
                TimeSpan half = HalfPeriod(_binding!);
                if (now - _nextEdge > TimeSpan.FromSeconds(1))
                {
                    // Long gap (paused clock): resync instead of replaying every edge.
                    _nextEdge = now;
                }

                while (now >= _nextEdge)
                {
                    if (_turboOn)
                    {
                        _actions.Up(_active);
                        _active = null;
                    }
                    else
                    {
                        _active = _actions.Down(_binding!.Press, now);
                    }

                    _turboOn = !_turboOn;
                    _nextEdge += half;
                }

                break;
        }
    }

    /// <summary>Rule 8 and swipe cancel: let go of everything, fire nothing pending.</summary>
    public void ReleaseAll()
    {
        if (_phase is Phase.Plain or Phase.Fired || (_phase == Phase.Turbo && _turboOn))
        {
            _actions.Up(_active);
        }

        _actions.Up(_latched);
        _active = null;
        _latched = null;
        _binding = null;
        _phase = Phase.Idle;
    }

    private static TimeSpan HalfPeriod(Binding b) => TimeSpan.FromSeconds(0.5 / Math.Clamp(b.TurboHz, 1, 30));
}
