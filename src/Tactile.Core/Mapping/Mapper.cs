using System.Text.Json;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

/// <summary>Active = enabled in the active profile, not paused, controller connected.</summary>
/// <param name="BlockedBy">Exe name of an elevated foreground window that drops our input, else null.</param>
public sealed record MappingStatus(bool Active, bool Paused, string? BlockedBy, string? PauseReason)
{
    public MappingStatus(bool Active, bool Paused, string? BlockedBy)
        : this(Active, Paused, BlockedBy, Paused ? "ui" : null) { }

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
    private readonly IMappingHost _host;
    private readonly MacroRunner _macros;
    private readonly ActionDispatcher _actions;
    private readonly Func<TimeSpan> _clock;
    private readonly bool _threaded;
    private readonly Dictionary<Buttons, ButtonMachine> _machines = [];

    private Profile? _profile;
    private MappingConfig _config = new();
    private (MappingLayer Layer, Buttons Button)[] _layers = [];
    private Buttons _activators;
    private int _activeLayers;
    private Buttons _gyroButton = Buttons.R1;
    private TouchpadMapper _pad = null!;
    private GyroMouse _gyro = null!;
    private StickMapper _left = null!;
    private StickMapper _right = null!;
    private ChordMachine _chords = null!;
    private TriggerMapper _l2 = null!;
    private TriggerMapper _r2 = null!;
    private FlickStick _flick = null!;
    private bool _flickEnabled;
    private bool _testTurnAllowed;
    private Buttons _prev;
    private Buttons _suppressedButtons;
    // Held buttons that may not open a layer until let go; set only by SuppressButtons.
    private Buttons _suppressedLayerButtons;
    private bool _padTouched;
    private bool _suppressPadTouch;
    private int? _pendingPadZone;
    private bool _gyroLatched;
    private bool _active;
    private bool _disposed;
    private bool _notifyLayers = true;

    /// <param name="threaded">False: worker jobs and macros run on the calling thread when pumped (tests).</param>
    public Mapper(InputPlatform platform, IMappingHost host, Func<TimeSpan> clock, bool threaded)
    {
        _threaded = threaded;
        _clock = clock;
        _host = host;
        _outputs = new HeldOutputs(platform.Injector);
        _pointer = new PointerOutput(platform.Injector);
        _worker = new ActionWorker(threaded, e => host.Report(new Notice(NoticeKinds.Mapping, $"Mapping error: {Text.English(e)}", $"Eşleme hatası: {e.Message}")));
        _macros = new MacroRunner(_outputs, clock, threaded);
        _actions = new ActionDispatcher(_outputs, _macros, _worker, platform, host);
        foreach (Buttons b in ButtonNames.All)
        {
            _machines[b] = new ButtonMachine(_actions);
        }

        Build(new Profile());
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

    /// <summary>Name of the highest active shift layer; null when none is active.</summary>
    public string? ActiveLayerName
    {
        get
        {
            lock (_gate)
            {
                return LayerNameLocked();
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
                _l2.Processor = new TriggerResponseProcessor(profile.TriggerResponse.Left);
                _r2.Processor = new TriggerResponseProcessor(profile.TriggerResponse.Right);
                return;
            }

            // The host announces the layer reset itself, after the profile change.
            ReleaseAllLocked(notifyLayers: false);
            _config = profile.Mapping ?? new();
            _gyroButton = ButtonNames.TryParse(_config.GyroMouse?.Button, out Buttons gb) ? gb : Buttons.None;
            _actions.SetMacros(profile.Macros ?? []);
            Build(profile);
        }
    }

    /// <summary>False releases everything and ignores input until true again.</summary>
    public void SetActive(bool active, bool? testTurnAllowed = null)
    {
        lock (_gate)
        {
            bool allowTurn = testTurnAllowed ?? active;
            if (_disposed || (active == _active && allowTurn == _testTurnAllowed))
            {
                return;
            }

            _active = active;
            _testTurnAllowed = allowTurn;
            ReleaseAllLocked();
        }
    }

    public void StartTestTurn(int counts, int delayMs, TimeSpan now)
    {
        lock (_gate)
        {
            if (_testTurnAllowed && !_disposed)
            {
                _flick.StartTestTurn(counts, delayMs, now);
            }
        }
    }

    public void OnInput(InputState s, Vector3 gyroDegPerSec, TimeSpan now, bool suppressLayerPulse = false)
    {
        lock (_gate)
        {
            // Use the same serialized clock for live reports and timer decisions.
            now = _threaded ? _clock() : now;
            bool touched = s.Touch1.Active || s.Touch2.Active;
            if (!_active || _disposed)
            {
                // Track buttons anyway, so a button held while inactive does not fire later.
                _prev = s.Buttons;
                _suppressedButtons = s.Buttons;
                _suppressedLayerButtons &= s.Buttons;
                _padTouched = _suppressPadTouch = touched;
                return;
            }

            Buttons changed = _prev ^ s.Buttons;
            _suppressedButtons &= s.Buttons;
            _suppressedLayerButtons &= s.Buttons;
            UpdateLayers(s.Buttons, changed, suppressLayerPulse);
            _padTouched = touched;
            _suppressPadTouch &= touched;
            TouchPoint firstTouch = _suppressPadTouch ? default : s.Touch1;
            TouchPoint secondTouch = _suppressPadTouch ? default : s.Touch2;
            if (_pad.HandlesClick && (_chords.Members & changed & s.Buttons & Buttons.Touchpad) != 0)
            {
                _pendingPadZone = _pad.ClickZone(firstTouch.Active ? firstTouch : secondTouch);
            }

            if (_chords.Members != Buttons.None)
            {
                _chords.Input(s.Buttons, changed, now, FlushChordButton);
            }

            UpdateTimers(now);
            if (changed != Buttons.None)
            {
                ProcessButtons(s.Buttons, changed, now);
            }

            _prev = s.Buttons;
            bool physicalClick = s.IsDown(Buttons.Touchpad);
            bool deferZoneClick = (_chords.Members & Buttons.Touchpad) != 0;
            bool padClick = physicalClick && (!_pad.HandlesClick ||
                ((_activators | _suppressedButtons) & Buttons.Touchpad) == 0);
            _pad.Input(firstTouch, secondTouch, padClick, now, deferZoneClick);
            _gyro.Input(gyroDegPerSec, GyroEnabled(s.Buttons, touched), now);
            _left.Input(s.Left, now);
            _right.Input(s.Right, now);
            if (UsesStages(_l2, Buttons.L2))
            {
                _l2.Input(s.L2, now);
            }

            if (UsesStages(_r2, Buttons.R2))
            {
                _r2.Input(s.R2, now);
            }

            if (_flickEnabled)
            {
                _flick.Input(s.Right, now);
            }

            UpdatePointer(now);
        }

        AfterGate(now);
    }

    public void Tick(TimeSpan now)
    {
        lock (_gate)
        {
            now = _threaded ? _clock() : now;
            if (_disposed)
            {
                return;
            }

            if (_active)
            {
                if (_chords.Members != Buttons.None)
                {
                    _chords.Input(_prev, Buttons.None, now, FlushChordButton);
                }

                UpdateTimers(now);
                _l2.Update(now);
                _r2.Update(now);
                _left.Update(now);
                _right.Update(now);
            }

            if (_active || _testTurnAllowed)
            {
                UpdatePointer(now);
            }
        }

        AfterGate(now);
    }

    /// <summary>Buttons held right now stay ignored until they are let go.</summary>
    public void SuppressButtons(Buttons mask)
    {
        lock (_gate)
        {
            _suppressedButtons |= mask;
            _suppressedLayerButtons |= mask;
        }
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
            _suppressedLayerButtons = Buttons.None;
            _testTurnAllowed = false;
            ReleaseAllLocked(resetAxes: true);
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

            _notifyLayers = false;
            ReleaseAllLocked();
            _disposed = true;
        }

        _macros.Dispose();
        _worker.Dispose();
    }

    private static bool SameMapping(Profile a, Profile b) =>
        JsonSerializer.Serialize(a.Mapping, TactileJson.Compact) == JsonSerializer.Serialize(b.Mapping, TactileJson.Compact) &&
        JsonSerializer.Serialize(a.Macros, TactileJson.Compact) == JsonSerializer.Serialize(b.Macros, TactileJson.Compact);

    private void Build(Profile p)
    {
        MappingConfig m = p.Mapping ?? new();
        StickMappingPair sticks = m.Sticks ?? new();
        FlickStickConfig flick = m.FlickStick ?? new();
        TriggerMappingPair triggers = m.Triggers ?? new();
        _layers = m.ShiftLayers().Take(Validation.MaxLayers)
            .Select(l => (l, ButtonNames.TryParse(l.Button, out Buttons button) ? button : Buttons.None)).ToArray();
        _activators = Buttons.None;
        foreach (var layer in _layers)
        {
            _activators |= layer.Button;
        }

        _chords = new ChordMachine(m, _activators, _actions);
        _pad = new TouchpadMapper(m.Touchpad ?? new(), _actions, _pointer);
        _gyro = new GyroMouse(m.GyroMouse ?? new(), _pointer, flick.CountsPer360);
        _left = new StickMapper(sticks.Left ?? new(), new StickProcessor(p.Sticks?.Left), _outputs, _pointer, _actions);
        _right = new StickMapper(sticks.Right ?? new(), new StickProcessor(p.Sticks?.Right), _outputs, _pointer, _actions, flick.Enabled);
        _l2 = new TriggerMapper(triggers.Left ?? new(), new TriggerResponseProcessor(p.TriggerResponse?.Left), _actions);
        _r2 = new TriggerMapper(triggers.Right ?? new(), new TriggerResponseProcessor(p.TriggerResponse?.Right), _actions);
        _flick = new FlickStick(flick, _pointer);
        _flickEnabled = flick.Enabled;
    }

    private void UpdatePointer(TimeSpan now)
    {
        _flick.Update(now);
        if (_pointer.Flush(now) && _flick.FinishTestTurn() is { } counts)
        {
            _worker.Post(() => _host.Report(new Notice(NoticeKinds.Info,
                $"Sent 360° test turn ({counts} counts).", $"360° deneme dönüşü gönderildi ({counts} sayım).")));
        }
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
        foreach ((Buttons button, ButtonMachine machine) in _machines)
        {
            if (!_chords.Pending(button))
            {
                machine.Update(now);
            }
        }

        _pad.Update(now);
    }

    private void UpdateLayers(Buttons current, Buttons changed, bool suppressLayerPulse)
    {
        int next = _activeLayers;
        for (int i = 0; i < _layers.Length; i++)
        {
            (MappingLayer layer, Buttons button) = _layers[i];
            int bit = 1 << i;
            // A suppressed activator cannot open its layer until it is let go; an open layer stays as it is.
            bool down = (current & button) != 0 && ((_suppressedLayerButtons & button) == 0 || (next & bit) != 0);
            if (layer.Mode == LayerMode.Toggle)
            {
                if (down && (changed & button) != 0 && (_suppressedLayerButtons & button) == 0)
                {
                    next ^= bit;
                }
            }
            else
            {
                next = down ? next | bit : next & ~bit;
            }
        }

        if (next != _activeLayers)
        {
            ReleaseAllLocked(resetLayers: false);
            _suppressedButtons &= current;
            _activeLayers = next;
            NotifyLayerLocked();
            if (!suppressLayerPulse && (changed & _activators) != 0 && _profile?.Haptics.ConfirmPulses == true)
            {
                _worker.Post(_host.PulseConfirm);
            }
        }
    }

    private string? LayerNameLocked()
    {
        for (int i = Math.Min(_layers.Length, 31) - 1; i >= 0; i--)
        {
            if ((_activeLayers & (1 << i)) != 0)
            {
                return string.IsNullOrWhiteSpace(_layers[i].Layer.Name) ? $"{i + 1}" : _layers[i].Layer.Name;
            }
        }

        return null;
    }

    private void NotifyLayerLocked()
    {
        if (_notifyLayers)
        {
            string? name = LayerNameLocked();
            _worker.Post(() => _host.LayerChanged(name));
        }
    }

    private bool UsesStages(TriggerMapper trigger, Buttons button) =>
        trigger.Enabled && ((_activators | _chords.Members) & button) == 0;

    private void FlushChordButton(Buttons button, TimeSpan? pressedAt, TimeSpan now, bool down)
    {
        if (button == Buttons.Touchpad && _pad.HandlesClick)
        {
            if (pressedAt is not null)
            {
                _pad.ZoneClick(_pendingPadZone, true, now, pressedAt);
            }

            if (!down)
            {
                _pad.ZoneClick(null, false, now);
            }

            return;
        }

        ButtonMachine machine = _machines[button];
        if (pressedAt is { } pressed)
        {
            Binding? binding = Lookup(button, out int layer);
            machine.Down(binding, now, layer, pressed);
        }

        if (!down)
        {
            machine.Up(now);
        }
    }

    private void ProcessButtons(Buttons current, Buttons changed, TimeSpan now)
    {
        foreach (Buttons b in ButtonNames.All)
        {
            if ((changed & b) == 0)
            {
                continue;
            }

            bool down = (current & b) != 0;
            if (((_activators | _suppressedButtons) & b) != 0)
            {
                continue;
            }

            if (b == _gyroButton && down && _config.GyroMouse?.Mode == GyroMouseMode.Toggle)
            {
                _gyroLatched = !_gyroLatched;
            }

            if ((_chords.Members & b) != 0 || (b == Buttons.Touchpad && _pad.HandlesClick) ||
                (b == Buttons.L2 && UsesStages(_l2, b)) || (b == Buttons.R2 && UsesStages(_r2, b)))
            {
                continue;
            }

            ButtonMachine machine = _machines[b];
            if (down)
            {
                Binding? binding = Lookup(b, out int layer);
                machine.Down(binding, now, layer);
            }
            else
            {
                machine.Up(now);
            }
        }
    }

    private Binding? Lookup(Buttons b, out int layer)
    {
        string name = ButtonNames.Name(b);
        for (int i = _layers.Length - 1; i >= 0; i--)
        {
            if ((_activeLayers & (1 << i)) != 0 && _layers[i].Layer.Buttons?.TryGetValue(name, out Binding? binding) == true)
            {
                layer = i + 1;
                return binding;
            }
        }

        layer = 0;
        return _config.Buttons?.GetValueOrDefault(name);
    }

    private bool GyroEnabled(Buttons current, bool touched) => _config.GyroMouse?.Mode switch
    {
        GyroMouseMode.Always => true,
        GyroMouseMode.WhileHeld => _gyroButton != Buttons.None && (current & _gyroButton) != 0,
        GyroMouseMode.WhileReleased => (current & _gyroButton) == 0,
        GyroMouseMode.WhileTouchpad => touched,
        GyroMouseMode.Toggle => _gyroLatched,
        _ => false,
    };

    private void ReleaseAllLocked(bool resetLayers = true, bool resetAxes = false, bool notifyLayers = true)
    {
        foreach (ButtonMachine m in _machines.Values)
        {
            m.ReleaseAll(resetCycle: resetLayers);
        }

        if (resetLayers)
        {
            bool hadLayers = _activeLayers != 0;
            _activeLayers = 0;
            if (hadLayers && notifyLayers)
            {
                NotifyLayerLocked();
            }

            _gyroLatched = false;
            _gyro.Reset();
        }

        _suppressedButtons = _prev;
        _pendingPadZone = null;
        if (resetLayers || _config.Touchpad?.Mode != TouchpadMode.Mouse)
        {
            _suppressPadTouch = !resetAxes && _padTouched;
            _pad.ReleaseAll();
        }

        if (resetAxes)
        {
            _padTouched = false;
        }

        _chords.ReleaseAll();
        _actions.ReleaseTaps();
        _l2.ReleaseAll(resetAxes);
        _r2.ReleaseAll(resetAxes);
        _left.ReleaseAll(resetAxes, preserveMotion: !resetLayers);
        _right.ReleaseAll(resetAxes, preserveMotion: !resetLayers);
        _flick.ReleaseAll(resetAxes);
        _pointer.Reset();
        _macros.StopAll();
        _outputs.ReleaseAll();
    }
}
