using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests;

/// <summary>Records every synthetic event as text, e.g. "A down", "mouse Left up", "move 3,0", "wheel -120 v".</summary>
internal sealed class FakeInjector : IInputInjector
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];

    public int MovedX { get; private set; }

    public int MovedY { get; private set; }

    public void Key(Key key, bool down) => Add($"{key} {(down ? "down" : "up")}");

    public void MouseButton(MouseButton button, bool down) => Add($"mouse {button} {(down ? "down" : "up")}");

    public void MouseMove(int dx, int dy)
    {
        lock (_lock)
        {
            MovedX += dx;
            MovedY += dy;
        }

        Add($"move {dx},{dy}");
    }

    public void Wheel(int delta, bool horizontal) => Add($"wheel {delta} {(horizontal ? "h" : "v")}");

    /// <summary>Events since the last call.</summary>
    public string[] Take()
    {
        lock (_lock)
        {
            string[] e = [.. _events];
            _events.Clear();
            return e;
        }
    }

    private void Add(string e)
    {
        lock (_lock)
        {
            _events.Add(e);
        }
    }
}

internal sealed class FakeMicrophone : IMicrophone
{
    public bool Present { get; set; } = true;

    public bool? IsMuted { get; private set; } = false;

    public bool? ToggleMute()
    {
        if (!Present)
        {
            return null;
        }

        IsMuted = !(IsMuted ?? false);
        return IsMuted;
    }
}

internal sealed class FakeLauncher : ILauncher
{
    public List<(string Path, string Args)> Runs { get; } = [];

    public bool Fail { get; set; }

    public void Run(string path, string arguments)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Program bulunamadı.");
        }

        Runs.Add((path, arguments));
    }
}

internal sealed class FakeElevation : IElevationProbe
{
    public bool Blocks { get; set; }

    public bool ForegroundBlocksInput() => Blocks;
}

internal sealed class FakePlatform
{
    public FakeInjector Injector { get; } = new();
    public FakeMicrophone Microphone { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public FakeElevation Elevation { get; } = new();

    public InputPlatform Platform => new(Injector, Microphone, Launcher, Elevation);
}

/// <summary>A mapper with a fake platform and a hand-driven clock. Buttons stay down until released.</summary>
internal sealed class MapperRig : IMappingHost, IDisposable
{
    private Buttons _held;
    private Stick _left = new(128, 128);
    private Stick _right = new(128, 128);
    private TouchPoint _t1;
    private TouchPoint _t2;
    private Vector3 _gyro;

    public MapperRig(MappingConfig mapping, IReadOnlyList<Macro>? macros = null, StickPair? sticks = null,
        bool confirmPulses = false, TriggerResponsePair? triggerResponse = null)
    {
        Mapper = new Mapper(Fake.Platform, this, () => Now, threaded: false);
        Profile = Validation.Normalize(new Profile
        {
            Name = "Test",
            Mapping = mapping with { Enabled = true },
            Macros = macros ?? [],
            Sticks = sticks ?? new StickPair(),
            Haptics = new HapticsConfig { ConfirmPulses = confirmPulses },
            TriggerResponse = triggerResponse ?? new(),
        });
        Mapper.Configure(Profile);
        Mapper.SetActive(true);
    }

    public FakePlatform Fake { get; } = new();

    public Mapper Mapper { get; }

    public Profile Profile { get; }

    public TimeSpan Now { get; private set; }

    public List<string> Reports { get; } = [];

    public List<string> Switches { get; } = [];

    public List<bool> MicStates { get; } = [];

    public int ShowAppCount { get; private set; }
    public int ConfirmPulses { get; private set; }

    public string[] Take() => Fake.Injector.Take();

    public MapperRig Press(Buttons b)
    {
        _held |= b;
        return Send();
    }

    public MapperRig Release(Buttons b)
    {
        _held &= ~b;
        return Send();
    }

    public MapperRig Sticks(Stick? left = null, Stick? right = null)
    {
        _left = left ?? _left;
        _right = right ?? _right;
        return Send();
    }

    public MapperRig Touch(TouchPoint t1, TouchPoint t2 = default)
    {
        _t1 = t1;
        _t2 = t2;
        return Send();
    }

    public MapperRig Gyro(Vector3 degPerSec)
    {
        _gyro = degPerSec;
        return Send();
    }

    public MapperRig Send()
    {
        Mapper.OnInput(Inputs.Make(buttons: _held, left: _left, right: _right, touch1: _t1, touch2: _t2), _gyro, Now);
        return this;
    }

    /// <summary>Moves the clock and runs the 30 Hz tick once.</summary>
    public MapperRig Advance(double ms)
    {
        Now += TimeSpan.FromMilliseconds(ms);
        Mapper.Tick(Now);
        return this;
    }

    /// <summary>Moves the clock in small steps, ticking each time.</summary>
    public MapperRig Run(double ms, double step = 5)
    {
        for (double t = 0; t < ms; t += step)
        {
            Advance(Math.Min(step, ms - t));
        }

        return this;
    }

    /// <summary>Moves the clock without ticking.</summary>
    public void Skip(double ms) => Now += TimeSpan.FromMilliseconds(ms);

    public void SwitchProfile(string target) => Switches.Add(target);

    public void MicMuteChanged(bool muted) => MicStates.Add(muted);

    public void ShowApp() => ShowAppCount++;
    public void PulseConfirm() => ConfirmPulses++;

    public void Report(Notice notice) => Reports.Add(notice.Message);

    public void Dispose() => Mapper.Dispose();
}

internal static class Bind
{
    public static Binding Key(params Key[] keys) => new() { Press = ActionConfig.ForKeys(keys) };

    public static ActionConfig Keys(params Key[] keys) => ActionConfig.ForKeys(keys);

    public static ActionConfig Wheel(WheelDirection d, int amount = 1) => new() { Kind = ActionKind.Wheel, Direction = d, Amount = amount };

    public static ActionConfig Macro(string id) => new() { Kind = ActionKind.Macro, MacroId = id };

    public static Dictionary<string, Binding> Map(params (string Button, Binding Binding)[] items) =>
        items.ToDictionary(i => i.Button, i => i.Binding);

    public static MappingConfig Buttons(params (string Button, Binding Binding)[] items) => new() { Buttons = Map(items) };
}
