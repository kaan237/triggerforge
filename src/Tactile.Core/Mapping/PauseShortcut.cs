using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

public sealed class PauseShortcut
{
    private Buttons _combo;
    private IReadOnlyList<string>? _names;
    private bool _held;

    internal bool Observe(Buttons buttons, IReadOnlyList<string> names)
    {
        if (!ReferenceEquals(names, _names))
        {
            Buttons combo = Buttons.None;
            foreach (string name in names)
            {
                if (ButtonNames.TryParse(name, out Buttons button))
                {
                    combo |= button;
                }
            }

            _held &= combo == _combo;
            (_combo, _names) = (combo, names);
        }

        bool held = names.Count >= 2 && _combo != Buttons.None && (buttons & _combo) == _combo;
        bool fired = held && !_held;
        _held = held;
        return fired;
    }

    internal void Reset() => _held = false;

    public static bool TryGetHotkey(IReadOnlyList<Key> keys, out uint modifiers, out Key trigger)
    {
        modifiers = 0;
        trigger = Key.None;
        if (keys.Count is < 1 or > 4)
        {
            return false;
        }

        foreach (Key key in keys)
        {
            uint modifier = key switch
            {
                Key.LeftAlt or Key.RightAlt => 1u,
                Key.LeftCtrl or Key.RightCtrl => 2u,
                Key.LeftShift or Key.RightShift => 4u,
                Key.LeftWin or Key.RightWin => 8u,
                _ => 0u,
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
            }
            else if (trigger != Key.None || key == Key.None || !Enum.IsDefined(key))
            {
                return false;
            }
            else
            {
                trigger = key;
            }
        }

        return trigger != Key.None;
    }
}
