using Tactile.Input;

namespace Tactile.Core;

/// <summary>
/// Plays macros off the input thread. Each playback is a small state machine advanced by
/// <see cref="Pump"/> against the injected clock; threaded mode pumps from its own thread,
/// manual mode (tests) pumps when told.
/// </summary>
internal sealed class MacroRunner : IDisposable
{
    public static readonly TimeSpan TapLength = TimeSpan.FromMilliseconds(30);
    /// <summary>Added between rounds of a loop that never waits, so it cannot spin.</summary>
    public static readonly TimeSpan IdleRoundGap = TimeSpan.FromMilliseconds(10);

    private readonly Lock _lock = new();
    private readonly HeldOutputs _outputs;
    private readonly Func<TimeSpan> _clock;
    private readonly bool _threaded;
    private readonly List<Playback> _running = [];
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private int _generation;
    private volatile bool _disposed;

    public MacroRunner(HeldOutputs outputs, Func<TimeSpan> clock, bool threaded)
    {
        _outputs = outputs;
        _clock = clock;
        _threaded = threaded;
    }

    /// <summary>Bumped by <see cref="StopAll"/>; requests made under an older value are dropped.</summary>
    public int Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    public bool IsRunning(string macroId)
    {
        lock (_lock)
        {
            return _running.Any(p => p.Macro.Id == macroId);
        }
    }

    /// <summary>Starts unless that macro already runs. <paramref name="loop"/> repeats until stopped.</summary>
    public void Start(Macro macro, bool loop, int generation)
    {
        lock (_lock)
        {
            if (_disposed || generation != _generation || _running.Any(p => p.Macro.Id == macro.Id))
            {
                return;
            }

            _running.Add(new Playback(macro, loop, _clock()));
        }

        Wake();
    }

    /// <summary>Starts a looping playback, or asks a running one to stop.</summary>
    public void Toggle(Macro macro, int generation)
    {
        bool stop;
        lock (_lock)
        {
            stop = _running.Any(p => p.Macro.Id == macro.Id);
        }

        if (stop)
        {
            Stop(macro.Id, generation);
        }
        else
        {
            Start(macro, loop: true, generation);
        }
    }

    /// <summary>Stops at the next step boundary; a pending delay is cut short.</summary>
    public void Stop(string macroId, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            TimeSpan now = _clock();
            foreach (Playback p in _running.Where(p => p.Macro.Id == macroId))
            {
                p.StopRequested = true;
                if (p.PendingKey is null && p.PendingButton is null && p.Due > now)
                {
                    p.Due = now;
                }
            }
        }

        Wake();
    }

    /// <summary>Stops everything now and releases what the macros hold.</summary>
    public void StopAll()
    {
        lock (_lock)
        {
            _generation++;
            foreach (Playback p in _running)
            {
                Release(p);
            }

            _running.Clear();
        }
    }

    /// <summary>Runs every step that is due. Returns the next due time, or null when idle.</summary>
    public TimeSpan? Pump(TimeSpan now)
    {
        lock (_lock)
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (!Advance(_running[i], now))
                {
                    Release(_running[i]);
                    _running.RemoveAt(i);
                }
            }

            return _running.Count == 0 ? null : _running.Min(p => p.Due);
        }
    }

    public void Dispose()
    {
        StopAll();
        _disposed = true;
        _signal.Set();
    }

    private void Wake()
    {
        if (!_threaded)
        {
            Pump(_clock());
            return;
        }

        lock (_lock)
        {
            if (_thread is null && !_disposed)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "Tactile macros" };
                _thread.Start();
            }
        }

        _signal.Set();
    }

    private void Loop()
    {
        // The default Windows timer ticks every ~15 ms, which would stretch 30 ms taps and short
        // delays noticeably; ask for 1 ms only while a macro is playing.
        bool fineTimer = false;
        try
        {
            while (!_disposed)
            {
                TimeSpan? next = Pump(_clock());
                if (next.HasValue != fineTimer)
                {
                    fineTimer = next.HasValue;
                    if (fineTimer)
                    {
                        Tactile.Device.DualSense.TimerResolution.Begin();
                    }
                    else
                    {
                        Tactile.Device.DualSense.TimerResolution.End();
                    }
                }

                int wait = Timeout.Infinite;
                if (next is { } due)
                {
                    wait = (int)Math.Clamp(Math.Ceiling((due - _clock()).TotalMilliseconds), 0, 1000);
                }

                _signal.WaitOne(wait);
            }
        }
        finally
        {
            if (fineTimer)
            {
                Tactile.Device.DualSense.TimerResolution.End();
            }
        }
    }

    // False when the playback is finished.
    private bool Advance(Playback p, TimeSpan now)
    {
        // Bounded: a looping macro with no waits gets IdleRoundGap per round.
        for (int guard = 0; guard < 100_000 && p.Due <= now; guard++)
        {
            if (p.PendingKey is { } pk)
            {
                _outputs.Release(pk);
                p.PendingKey = null;
                p.Index++;
                continue;
            }

            if (p.PendingButton is { } pb)
            {
                _outputs.Release(pb);
                p.PendingButton = null;
                p.Index++;
                continue;
            }

            if (p.StopRequested)
            {
                return false;
            }

            IReadOnlyList<MacroStep> steps = p.Macro.Steps;
            if (p.Index >= steps.Count)
            {
                if (!p.Loop)
                {
                    return false;
                }

                p.Index = 0;
                if (!p.RoundWaited)
                {
                    p.Due = now + IdleRoundGap;
                }

                p.RoundWaited = false;
                continue;
            }

            Execute(p, steps[p.Index], now);
        }

        return true;
    }

    private void Execute(Playback p, MacroStep step, TimeSpan now)
    {
        switch (step.Op)
        {
            case MacroOp.Down when step.Key is { } k:
                if (p.Keys.Add(k))
                {
                    _outputs.Press(k);
                }

                break;
            case MacroOp.Up when step.Key is { } k:
                if (p.Keys.Remove(k))
                {
                    _outputs.Release(k);
                }

                break;
            case MacroOp.Tap when step.Key is { } k:
                _outputs.Press(k);
                p.PendingKey = k;
                p.Due = now + TapLength;
                p.RoundWaited = true;
                return;
            case MacroOp.MouseDown when step.Button is { } b:
                if (p.Buttons.Add(b))
                {
                    _outputs.Press(b);
                }

                break;
            case MacroOp.MouseUp when step.Button is { } b:
                if (p.Buttons.Remove(b))
                {
                    _outputs.Release(b);
                }

                break;
            case MacroOp.Click when step.Button is { } b:
                _outputs.Press(b);
                p.PendingButton = b;
                p.Due = now + TapLength;
                p.RoundWaited = true;
                return;
            case MacroOp.Wheel:
                (int delta, bool horizontal) = ActionDispatcher.WheelDelta(step.Direction ?? WheelDirection.Up, step.Amount ?? 1);
                _outputs.Injector.Wheel(delta, horizontal);
                break;
            case MacroOp.Delay:
                int ms = Math.Max(0, step.Ms ?? 0);
                if (ms > 0)
                {
                    p.Due = now + TimeSpan.FromMilliseconds(ms);
                    p.RoundWaited = true;
                }

                break;
        }

        p.Index++;
    }

    private void Release(Playback p)
    {
        if (p.PendingKey is { } pk)
        {
            _outputs.Release(pk);
        }

        if (p.PendingButton is { } pb)
        {
            _outputs.Release(pb);
        }

        foreach (Key k in p.Keys.Reverse())
        {
            _outputs.Release(k);
        }

        foreach (MouseButton b in p.Buttons)
        {
            _outputs.Release(b);
        }

        p.PendingKey = null;
        p.PendingButton = null;
        p.Keys.Clear();
        p.Buttons.Clear();
    }

    private sealed class Playback(Macro macro, bool loop, TimeSpan start)
    {
        public Macro Macro { get; } = macro;
        public bool Loop { get; } = loop;
        public int Index { get; set; }
        public TimeSpan Due { get; set; } = start;
        public bool StopRequested { get; set; }
        public bool RoundWaited { get; set; }
        public Key? PendingKey { get; set; }
        public MouseButton? PendingButton { get; set; }
        public HashSet<Key> Keys { get; } = [];
        public HashSet<MouseButton> Buttons { get; } = [];
    }
}
