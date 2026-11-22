namespace Tactile.Core;

internal sealed class TriggerMapper(TriggerMapping config, TriggerResponseProcessor processor, ActionDispatcher actions)
{
    private HeldAction? _softHeld;
    private HeldAction? _fullHeld;
    private TimeSpan? _softDue;
    private bool _soft;
    private bool _full;
    private bool _skipSoft;
    private bool _blocked;
    private double _travel;

    public bool Enabled => config.Mode != TriggerStageMode.Off;
    public TriggerResponseProcessor Processor { get; set; } = processor;

    public void Input(byte raw, TimeSpan now)
    {
        double travel = Processor.Process(raw) / 255.0;
        bool atRest = travel == 0 || travel < config.SoftThreshold - config.Hysteresis;
        _travel = travel;
        if (_blocked)
        {
            _blocked = !atRest;
            return;
        }

        bool reachingFull = !_full && travel >= config.FullThreshold;
        Update(now);

        if (_full && (travel == 0 || travel < config.FullThreshold - config.Hysteresis))
        {
            SetFull(false, now);
        }

        if (atRest)
        {
            if (_softDue is not null)
            {
                actions.Tap(config.Soft, now);
                _softDue = null;
            }

            SetSoft(false, now);
            _skipSoft = false;
            return;
        }

        if (reachingFull)
        {
            if (config.Mode == TriggerStageMode.Exclusive ||
                (config.Mode == TriggerStageMode.HipFire && !_soft))
            {
                _softDue = null;
                SetSoft(false, now);
                _skipSoft = true;
            }
        }

        if (!_soft && !_skipSoft && _softDue is null && travel >= config.SoftThreshold)
        {
            if (config.Mode == TriggerStageMode.HipFire)
            {
                _softDue = now + TimeSpan.FromMilliseconds(config.HipFireMs);
            }
            else
            {
                SetSoft(true, now);
            }
        }

        if (reachingFull)
        {
            SetFull(true, now);
        }
    }

    public void Update(TimeSpan now)
    {
        if (_softDue is { } due && now >= due)
        {
            _softDue = null;
            SetSoft(true, now);
        }
    }

    public void ReleaseAll(bool reset = false)
    {
        SetSoft(false, default);
        SetFull(false, default);
        _softDue = null;
        _skipSoft = false;
        _blocked = !reset && _travel > 0 && _travel >= config.SoftThreshold - config.Hysteresis;
        if (reset)
        {
            _travel = 0;
        }
    }

    private void SetSoft(bool down, TimeSpan now)
    {
        if (down == _soft)
        {
            return;
        }

        _soft = down;
        if (down)
        {
            _softHeld = actions.Down(config.Soft, now);
        }
        else
        {
            actions.Up(_softHeld);
            _softHeld = null;
        }
    }

    private void SetFull(bool down, TimeSpan now)
    {
        if (down == _full)
        {
            return;
        }

        _full = down;
        if (down)
        {
            _fullHeld = actions.Down(config.Full, now);
        }
        else
        {
            actions.Up(_fullHeld);
            _fullHeld = null;
        }
    }
}
