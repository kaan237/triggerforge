using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Turns the real controller's input into what the virtual pad reports (docs/contract-phase4.md).</summary>
public static class EmulationInput
{
    private const Buttons Triggers = Buttons.L2 | Buttons.R2;

    /// <summary>
    /// Hidden buttons are dropped first, as the physical buttons they are, so a hidden source
    /// never presses its remap target. Then remap, then the stick swap.
    /// </summary>
    public static InputState Transform(InputState s, EmulationConfig config, Buttons hidden = Buttons.None,
        (float X, float Y) gyro = default)
    {
        IReadOnlyDictionary<string, string> remap = config.Remap ?? new Dictionary<string, string>();
        Buttons source = s.Buttons & ~hidden;
        byte l2 = (hidden & Buttons.L2) != 0 ? (byte)0 : s.L2;
        byte r2 = (hidden & Buttons.R2) != 0 ? (byte)0 : s.R2;

        Buttons buttons = Buttons.None;
        byte outL2 = 0;
        byte outR2 = 0;

        // Trigger analog follows the trigger's target when that target is a trigger, else drops to 0.
        void Analog(Buttons from, byte value)
        {
            switch (Target(remap, from))
            {
                case Buttons.L2:
                    outL2 = Math.Max(outL2, value);
                    break;
                case Buttons.R2:
                    outR2 = Math.Max(outR2, value);
                    break;
            }
        }

        Analog(Buttons.L2, l2);
        Analog(Buttons.R2, r2);

        foreach (Buttons b in ButtonNames.All)
        {
            if ((source & b) == 0)
            {
                continue;
            }

            Buttons target = Target(remap, b);
            buttons |= target;
            // A plain button landing on a trigger presses it fully, so games reading the axis see it.
            if ((b & Triggers) == 0)
            {
                if (target == Buttons.L2)
                {
                    outL2 = 255;
                }
                else if (target == Buttons.R2)
                {
                    outR2 = 255;
                }
            }
        }

        Stick right = config.SwapSticks ? s.Left : s.Right;
        if (gyro != default)
        {
            (float x, float y) = right.Normalized;
            right = new Stick(ToByte(x + gyro.X), ToByte(-y - gyro.Y));
        }

        return s with
        {
            Buttons = buttons,
            L2 = outL2,
            R2 = outR2,
            Left = config.SwapSticks ? s.Right : s.Left,
            Right = right,
        };
    }

    private static byte ToByte(float axis) => (byte)MathF.Round((Math.Clamp(axis, -1, 1) + 1) * 127.5f);

    /// <summary>Buttons the profile's keyboard/mouse mapping uses; empty while mapping is off.</summary>
    public static Buttons MappedButtons(Profile p)
    {
        MappingConfig m = p.Mapping ?? new();
        if (!m.Enabled)
        {
            return Buttons.None;
        }

        Buttons result = Buttons.None;
        bool anyLayer = false;
        foreach (IReadOnlyDictionary<string, Binding>? map in new[] { m.Buttons, m.Layer })
        {
            foreach ((string name, Binding b) in map ?? new Dictionary<string, Binding>())
            {
                if (IsBound(b) && ButtonNames.TryParse(name, out Buttons button))
                {
                    result |= button;
                    anyLayer |= ReferenceEquals(map, m.Layer);
                }
            }
        }

        // The layer button only selects the layer, so it is taken once the layer is used.
        if (anyLayer && ButtonNames.TryParse(m.LayerButton, out Buttons layer))
        {
            result |= layer;
        }

        // In zones mode the pad click belongs to the zones.
        if (m.Touchpad?.Mode == TouchpadMode.Zones)
        {
            result |= Buttons.Touchpad;
        }

        return result;
    }

    private static bool IsBound(Binding? b) =>
        b is not null && (b.Press?.Kind is not (null or ActionKind.None) || b.Hold is { Kind: not ActionKind.None } || b.Double is { Kind: not ActionKind.None }
            || b.Triple is { Kind: not ActionKind.None } || b.Cycle is { Count: > 0 });

    private static Buttons Target(IReadOnlyDictionary<string, string> remap, Buttons b)
    {
        if (!remap.TryGetValue(ButtonNames.Name(b), out string? target))
        {
            return b;
        }

        return ButtonNames.TryParse(target, out Buttons t) ? t : Buttons.None;
    }
}
