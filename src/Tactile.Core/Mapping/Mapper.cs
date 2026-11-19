using System.Text.Json;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

/// <summary>Active = enabled in the active profile, not paused, controller connected.</summary>
/// <param name="BlockedBy">Exe name of an elevated foreground window that drops our input, else null.</param>
public sealed record MappingStatus(bool Active, bool Paused, string? BlockedBy)
{
    public static MappingStatus Off { get; } = new(false, false, null);
}

/// <summary>
/// Turns controller input into keyboard and mouse output for one profile. Fed from the input
/// reports and the 30 Hz tick; all time comes in as arguments. Thread-safe.
/// </summary>
internal sealed class Mapper : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HeldOutputs _outputs;
    private readonly PointerOutput _pointer;
    private readonly ActionWorker _worker;
    private readonly MacroRunner _macros;
    private readonly ActionDispatcher _actions;
    private readonly bool _threaded;
    private readonly Dictionary<Buttons, ButtonMachine> _machines = [];

    private Profile? _profile;
    private MappingConfig _config = new();
    private Buttons _layerButton = Buttons.PS;
    private Buttons _gyroButton = Buttons.R1;
    private TouchpadMapper _pad;
    private GyroMouse _gyro;
    private StickMapper _left;
    private StickMapper _right;
    private Buttons _prev;
    private bool _gyroLatched;
    private bool _active;
    private bool _disposed;

    /// <param name="threaded">False: worker jobs and macros run on the calling thread when pumped (tests).</param>
    public Mapper(InputPlatform platform, IMappingHost host, Func<TimeSpan> clock, bool threaded)
    {
        _threaded = threaded;
        _outputs = new HeldOutputs(platform.Injector);
        _pointer = new PointerOutput(platform.Injector);
        _worker = new ActionWorker(threaded, e => host.Report(new Notice(NoticeKinds.Mapping, $"Mapping error: {Text.English(e)}", $"Eşleme hatası: {e.Message}")));
        _macros = new MacroRunner(_outputs, clock, threaded);
        _actions = new ActionDispatcher(_outputs, _macros, _worker, platform, host);
        foreach (Buttons b in ButtonNames.All)
        {
            _machines[b] = new ButtonMachine(_actions);
        }

        (_pad, _gyro, _left, _right) = Build(new Profile());
    }

    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// Takes the active profile. A different profile, or the same one saved with a different
    /// mapping or macros, releases everything first (rule 8).
    /// </summary>
    public void Configure(Profile profile)
    {
        lock (_gate)
        {
            if (ReferenceEquals(profile, _profile) || _disposed)
            {
                return;
            }

            bool same = _profile is not null && _profile.Id == profile.Id && SameMapping(_profile, profile);
            Profile? old = _profile;
            _profile = profile;
            if (same && old is not null)
            {
                // Only stick shaping or unrelated settings changed; keep what is held.
                _left.Processor = new StickProcessor(profile.Sticks.Left);
                _right.Processor = new StickProcessor(profile.Sticks.Right);
                return;
            }

            ReleaseAllLocked();
            _config = profile.Mapping ?? new();
            _layerButton = ButtonNames.TryParse(_config.LayerButton, out Buttons lb) ? lb : Buttons.None;
            _gyroButton = ButtonNames.TryParse(_config.GyroMouse?.Button, out Buttons gb) ? gb : Buttons.None;
            _actions.SetMacros(profile.Macros ?? []);
            (_pad, _gyro, _left, _right) = Build(profile);
        }
    }

    /// <summary>False releases everything and ignores input until true again.</summary>
    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (active == _active || _disposed)
            {
                return;
            }

            _active = active;
            ReleaseAllLocked();
        }
    }

    public void OnInput(InputState s, Vector3 gyroDegPerSec, TimeSpan now)
    {
        lock (_gate)
        {
            if (!_active || _disposed)
            {
                // Track buttons anyway, so a button held while inactive does not fire later.
                _prev = s.Buttons;
                return;
            }

            UpdateTimers(now);
            Buttons changed = _prev ^ s.Buttons;
            if (changed != Buttons.None)
            {
                ProcessButtons(s.Buttons, changed, now);
            }

            _prev = s.Buttons;
            _pad.Input(s.Touch1, s.Touch2, s.IsDown(Buttons.Touchpad), now);
            _gyro.Input(gyroDegPerSec, GyroEnabled(s.Buttons), now);
            _left.Input(s.Left, now);
            _right.Input(s.Right, now);
            _pointer.Flush(now);
        }

        AfterGate(now);
    }

    public void Tick(TimeSpan now)
    {
        lock (_gate)
        {
            if (_active && !_disposed)
            {
                UpdateTimers(now);
                _left.Update(now);
                _right.Update(now);
                _pointer.Flush(now);
            }
        }

        AfterGate(now);
    }

    /// <summary>Rule 8: all keys, mouse buttons, macros, toggles and turbo let go.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            ReleaseAllLocked();
        }
    }

    internal void ResetController()
    {
        lock (_gate)
        {
            _prev = Buttons.None;
            ReleaseAllLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseAllLocked();
            _disposed = true;
        }

        _macros.Dispose();
        _worker.Dispose();
    }

    private static bool SameMapping(Profile a, Profile b) =>
        JsonSerializer.Serialize(a.Mapping, TactileJson.Compact) == JsonSerializer.Serialize(b.Mapping, TactileJson.Compact) &&
        JsonSerializer.Serialize(a.Macros, TactileJson.Compact) == JsonSerializer.Serialize(b.Macros, TactileJson.Compact);

    private (TouchpadMapper, GyroMouse, StickMapper, StickMapper) Build(Profile p)
    {
        MappingConfig m = p.Mapping ?? new();
        StickMappingPair sticks = m.Sticks ?? new();
        return (
            new TouchpadMapper(m.Touchpad ?? new(), _actions, _pointer),
            new GyroMouse(m.GyroMouse ?? new(), _pointer),
            new StickMapper(sticks.Left ?? new(), new StickProcessor(p.Sticks?.Left), _outputs, _pointer),
            new StickMapper(sticks.Right ?? new(), new StickProcessor(p.Sticks?.Right), _outputs, _pointer));
    }

    private void AfterGate(TimeSpan now)
    {
        // Manual mode: run worker jobs and due macro steps now that the gate is free.
        if (!_threaded)
        {
            _worker.RunPending();
            _macros.Pump(now);
        }
    }

    private void UpdateTimers(TimeSpan now)
    {
        _actions.Update(now);
        foreach (ButtonMachine m in _machines.Values)
        {
            m.Update(now);
        }

        _pad.Update(now);
    }

    private void ProcessButtons(Buttons current, Buttons changed, TimeSpan now)
    {
        bool layerHeld = _layerButton != Buttons.None && (current & _layerButton) != 0;
        foreach (Buttons b in ButtonNames.All)
        {
            if ((changed & b) == 0)
            {
                continue;
            }

            bool down = (current & b) != 0;
            if (b == _gyroButton && down && _config.GyroMouse?.Mode == GyroMouseMode.Toggle)
            {
                _gyroLatched = !_gyroLatched;
            }

            // Rule 6: the layer button only selects the layer.
            if (b == _layerButton || (b == Buttons.Touchpad && _pad.HandlesClick))
            {
                continue;
            }

            ButtonMachine machine = _machines[b];
            if (down)
            {
                machine.Down(Lookup(b, layerHeld), now);
            }
            else
            {
                machine.Up(now);
            }
        }
    }

    private Binding? Lookup(Buttons b, bool layerHeld)
    {
        string name = ButtonNames.Name(b);
        if (layerHeld && _config.Layer?.TryGetValue(name, out Binding? layered) == true)
        {
            return layered;
        }

        return _config.Buttons?.GetValueOrDefault(name);
    }

    private bool GyroEnabled(Buttons current) => _config.GyroMouse?.Mode switch
    {
        GyroMouseMode.Always => true,
        GyroMouseMode.WhileHeld => _gyroButton != Buttons.None && (current & _gyroButton) != 0,
        GyroMouseMode.Toggle => _gyroLatched,
        _ => false,
    };

    private void ReleaseAllLocked()
    {
        foreach (ButtonMachine m in _machines.Values)
        {
            m.ReleaseAll();
        }

        _pad.ReleaseAll();
        _actions.ReleaseTaps();
        _left.ReleaseAll();
        _right.ReleaseAll();
        _gyro.Reset();
        _gyroLatched = false;
        _pointer.Reset();
        _macros.StopAll();
        _outputs.ReleaseAll();
    }
}
