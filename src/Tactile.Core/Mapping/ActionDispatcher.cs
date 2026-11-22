using Tactile.Input;

namespace Tactile.Core;

/// <summary>What the mapping engine asks of the app: things it cannot do itself.</summary>
internal interface IMappingHost
{
    /// <summary>An id, "next" or "previous".</summary>
    void SwitchProfile(string target);

    void MicMuteChanged(bool muted);

    void ShowApp();

    void PulseConfirm() { }

    /// <summary>A runtime failure, shown as a <c>mapping</c> notice.</summary>
    void Report(Notice notice);
}

/// <summary>What one held action pressed, so the release goes to exactly that.</summary>
internal sealed class HeldAction
{
    public Key[] Keys { get; init; } = [];
    public MouseButton? Button { get; init; }
    /// <summary>A whileHeld macro to stop on release.</summary>
    public Macro? Macro { get; init; }
}

/// <summary>
/// Turns actions into output. Held actions (keys, mouse buttons, whileHeld macros) return a
/// <see cref="HeldAction"/> to release later; one-shots fire and return null. Slow work goes
/// to the worker. Only used under the mapper's lock.
/// </summary>
internal sealed class ActionDispatcher
{
    public static readonly TimeSpan TapLength = TimeSpan.FromMilliseconds(30);

    private readonly HeldOutputs _outputs;
    private readonly MacroRunner _macros;
    private readonly ActionWorker _worker;
    private readonly InputPlatform _platform;
    private readonly IMappingHost _host;
    private readonly List<(HeldAction Held, TimeSpan ReleaseAt)> _taps = [];
    private IReadOnlyDictionary<string, Macro> _macroById = new Dictionary<string, Macro>();

    public ActionDispatcher(HeldOutputs outputs, MacroRunner macros, ActionWorker worker, InputPlatform platform, IMappingHost host)
    {
        _outputs = outputs;
        _macros = macros;
        _worker = worker;
        _platform = platform;
        _host = host;
    }

    public void SetMacros(IReadOnlyList<Macro> macros) =>
        _macroById = macros.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public static (int Delta, bool Horizontal) WheelDelta(WheelDirection direction, int amount)
    {
        int units = 120 * Math.Clamp(amount, 1, 10);
        return direction switch
        {
            WheelDirection.Down => (-units, false),
            WheelDirection.Left => (-units, true),
            WheelDirection.Right => (units, true),
            _ => (units, false),
        };
    }

    public HeldAction? Down(ActionConfig action, TimeSpan now)
    {
        switch (action.Kind)
        {
            case ActionKind.Key when action.Keys is { Count: > 0 } keys:
                foreach (Key k in keys)
                {
                    _outputs.Press(k);
                }

                return new HeldAction { Keys = [.. keys] };
            case ActionKind.MouseButton:
                MouseButton b = action.Button ?? MouseButton.Left;
                _outputs.Press(b);
                return new HeldAction { Button = b };
            case ActionKind.Wheel:
                (int delta, bool horizontal) = WheelDelta(action.Direction ?? WheelDirection.Up, action.Amount ?? 1);
                _outputs.Injector.Wheel(delta, horizontal);
                return null;
            case ActionKind.Macro:
                return StartMacro(action.MacroId);
            case ActionKind.Profile when action.ProfileId is { } target:
                _worker.Post(() => _host.SwitchProfile(target));
                return null;
            case ActionKind.Run when action.Path is { } path:
                string args = action.Args ?? "";
                _worker.Post(() => RunProgram(path, args));
                return null;
            case ActionKind.MicMute:
                _worker.Post(ToggleMic);
                return null;
            case ActionKind.Special:
                Special(action.Special ?? SpecialAction.Screenshot, now);
                return null;
            default:
                return null;
        }
    }

    public void Up(HeldAction? held)
    {
        if (held is null)
        {
            return;
        }

        for (int i = held.Keys.Length - 1; i >= 0; i--)
        {
            _outputs.Release(held.Keys[i]);
        }

        if (held.Button is { } b)
        {
            _outputs.Release(b);
        }

        if (held.Macro is { } m)
        {
            int generation = _macros.Generation;
            _worker.Post(() => _macros.Stop(m.Id, generation));
        }
    }

    /// <summary>Down, then up <see cref="TapLength"/> later. One-shots just fire.</summary>
    public void Tap(ActionConfig action, TimeSpan now)
    {
        if (Down(action, now) is { } held)
        {
            _taps.Add((held, now + TapLength));
        }
    }

    /// <summary>Releases taps whose time is up.</summary>
    public void Update(TimeSpan now)
    {
        for (int i = 0; i < _taps.Count; i++)
        {
            if (_taps[i].ReleaseAt <= now)
            {
                HeldAction held = _taps[i].Held;
                _taps.RemoveAt(i--);
                Up(held);
            }
        }
    }

    public void ReleaseTaps()
    {
        foreach ((HeldAction held, _) in _taps.ToArray())
        {
            Up(held);
        }

        _taps.Clear();
    }

    private HeldAction? StartMacro(string? id)
    {
        if (id is null || !_macroById.TryGetValue(id, out Macro? macro))
        {
            _worker.Post(() => _host.Report(new Notice(NoticeKinds.Mapping, "Macro not found.", "Makro bulunamadı.")));
            return null;
        }

        int generation = _macros.Generation;
        switch (macro.Repeat)
        {
            case MacroRepeat.WhileHeld:
                _worker.Post(() => ConfirmMacroStart(_macros.Start(macro, loop: true, generation)));
                return new HeldAction { Macro = macro };
            case MacroRepeat.Toggle:
                _worker.Post(() => ConfirmMacroStart(_macros.Toggle(macro, generation)));
                return null;
            default:
                _worker.Post(() => ConfirmMacroStart(_macros.Start(macro, loop: false, generation)));
                return null;
        }
    }

    private void ConfirmMacroStart(bool started)
    {
        if (started)
        {
            _host.PulseConfirm();
        }
    }

    private void Special(SpecialAction special, TimeSpan now)
    {
        Key? second = special switch
        {
            SpecialAction.Screenshot => Key.PrintScreen,
            SpecialAction.GameBar => Key.G,
            SpecialAction.TaskView => Key.Tab,
            SpecialAction.Desktop => Key.D,
            _ => null,
        };

        if (second is { } k)
        {
            // Win + key as a tap, released by Update like any other tap.
            _outputs.Press(Key.LeftWin);
            _outputs.Press(k);

            _taps.Add((new HeldAction { Keys = [Key.LeftWin, k] }, now + TapLength));
            return;
        }

        _worker.Post(_host.ShowApp);
    }

    private void RunProgram(string path, string args)
    {
        try
        {
            _platform.Launcher.Run(path, args);
        }
        catch (InvalidOperationException e)
        {
            _host.Report(new Notice(NoticeKinds.Mapping, Text.English(e), e.Message));
        }
    }

    private void ToggleMic()
    {
        if (_platform.Microphone.ToggleMute() is { } muted)
        {
            _host.MicMuteChanged(muted);
        }
        else
        {
            _host.Report(new Notice(NoticeKinds.Mapping, "No recording device found. Could not mute the microphone.", "Kayıt aygıtı bulunamadı; mikrofon susturulamadı."));
        }
    }
}
