using Tactile.Device.DualSense;

namespace Tactile.Core;

internal sealed class ChordMachine
{
    private readonly record struct Chord(Buttons Mask, int Size, ActionConfig Action);

    private readonly ActionDispatcher _actions;
    private readonly TimeSpan _window;
    private readonly Chord[] _chords;
    private readonly Dictionary<Buttons, TimeSpan> _pending = [];
    private readonly List<(Buttons Mask, HeldAction? Held)> _active = [];
    private Buttons _consumed;
    private Buttons _singles;

    public ChordMachine(MappingConfig config, Buttons activators, ActionDispatcher actions)
    {
        _actions = actions;
        _window = TimeSpan.FromMilliseconds(config.ChordWindowMs);
        var chords = new List<Chord>();
        foreach (ButtonChord chord in config.Chords ?? [])
        {
            if (chord?.Action is not { Kind: not ActionKind.None })
            {
                continue;
            }

            Buttons mask = Buttons.None;
            int size = 0;
            foreach (string name in chord.Buttons ?? [])
            {
                if (ButtonNames.TryParse(name, out Buttons button) && (mask & button) == 0)
                {
                    mask |= button;
                    size++;
                }
            }

            if (size is 2 or 3 && (mask & activators) == 0)
            {
                chords.Add(new Chord(mask, size, chord.Action));
                Members |= mask;
            }
        }

        _chords = chords.OrderByDescending(c => c.Size).ToArray();
    }

    public Buttons Members { get; }

    public bool Pending(Buttons button) => _pending.ContainsKey(button);

    public void Input(Buttons current, Buttons changed, TimeSpan now, Action<Buttons, TimeSpan?, TimeSpan, bool> flush)
    {
        if (_pending.Count == 0 && (changed & Members) == 0)
        {
            return;
        }

        Buttons previous = current ^ changed;
        _consumed &= current;
        Buttons releasedSingles = _singles & ~current;
        _singles &= current;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if ((current & _active[i].Mask) != _active[i].Mask)
            {
                _actions.Up(_active[i].Held);
                _active.RemoveAt(i);
            }
        }

        foreach (Buttons button in ButtonNames.All)
        {
            if ((current & changed & Members & button) != 0)
            {
                _pending[button] = now;
            }
        }

        foreach (Chord chord in _chords)
        {
            Buttons mask = chord.Mask;
            bool held = (current & mask) == mask;
            if (!held && (previous & mask) != mask)
            {
                continue;
            }

            if (!CanComplete(mask, mask, out TimeSpan first, out TimeSpan last) || last - first > _window)
            {
                continue;
            }

            if (held && chord.Size == 2 && _chords.Any(c => c.Size == 3 && (c.Mask & mask) == mask &&
                    CanComplete(c.Mask, current, out TimeSpan start, out _) && now - start < _window))
            {
                continue;
            }

            HeldAction? action = _actions.Down(chord.Action, now);
            if (held)
            {
                _active.Add((mask, action));
            }
            else
            {
                _actions.Up(action);
            }

            _consumed |= mask & current;
            foreach (Buttons button in ButtonNames.All)
            {
                if ((mask & button) != 0)
                {
                    _pending.Remove(button);
                }
            }
        }

        foreach (Buttons button in ButtonNames.All)
        {
            bool down = (current & button) != 0;
            if ((releasedSingles & button) != 0)
            {
                flush(button, null, now, false);
            }

            if (_pending.TryGetValue(button, out TimeSpan pressed))
            {
                if (!down || now - pressed >= _window)
                {
                    _pending.Remove(button);
                    if (down)
                    {
                        _singles |= button;
                    }

                    flush(button, pressed, now, down);
                }
            }
        }
    }

    private bool CanComplete(Buttons mask, Buttons current, out TimeSpan first, out TimeSpan last)
    {
        first = TimeSpan.MaxValue;
        last = TimeSpan.MinValue;
        bool hasPending = false;
        if ((mask & _consumed) != 0)
        {
            return false;
        }

        foreach (Buttons button in ButtonNames.All)
        {
            if ((mask & button) == 0)
            {
                continue;
            }

            if (_pending.TryGetValue(button, out TimeSpan pressed))
            {
                if ((current & button) == 0)
                {
                    return false;
                }

                first = pressed < first ? pressed : first;
                last = pressed > last ? pressed : last;
                hasPending = true;
            }
            else if ((current & button) != 0)
            {
                return false;
            }
        }

        return hasPending;
    }

    public void ReleaseAll()
    {
        foreach (var chord in _active)
        {
            _actions.Up(chord.Held);
        }

        _active.Clear();
        _pending.Clear();
        _consumed = Buttons.None;
        _singles = Buttons.None;
    }
}
