using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Core;

/// <summary>Mapping and macro rules from docs/contract-phase3.md.</summary>
public static partial class Validation
{
    public const int MaxMacros = 50;
    public const int MaxMacroSteps = 500;
    public const int MaxCycleActions = 8;
    public const int MaxChordKeys = 4;
    public const int MaxRunPath = 260;
    public const int MaxRunArgs = 512;

    /// <summary>Replaces every <c>run</c> action with <c>none</c>; used for imported profiles.</summary>
    public static Profile StripRunActions(Profile p, out int removed)
    {
        int count = 0;
        ActionConfig Strip(ActionConfig a)
        {
            if (a.Kind == ActionKind.Run)
            {
                count++;
                return ActionConfig.None;
            }

            return a;
        }

        MappingConfig m = p.Mapping ?? new();
        TouchpadMapping pad = m.Touchpad ?? new();
        SwipeActions swipes = pad.Swipes ?? new();
        Profile result = p with
        {
            Mapping = m with
            {
                Buttons = MapBindings(m.Buttons, Strip),
                Layer = MapBindings(m.Layer, Strip),
                Touchpad = pad with
                {
                    Zones = (pad.Zones ?? []).Select(z => MapBinding(z, Strip)).ToArray(),
                    Swipes = new SwipeActions
                    {
                        Up = Strip(swipes.Up ?? ActionConfig.None),
                        Down = Strip(swipes.Down ?? ActionConfig.None),
                        Left = Strip(swipes.Left ?? ActionConfig.None),
                        Right = Strip(swipes.Right ?? ActionConfig.None),
                    },
                },
            },
        };
        removed = count;
        return result;
    }

    private static Dictionary<string, Binding> MapBindings(IReadOnlyDictionary<string, Binding>? map, Func<ActionConfig, ActionConfig> f)
    {
        var result = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach ((string name, Binding b) in map ?? new Dictionary<string, Binding>())
        {
            if (b is not null)
            {
                result[name] = MapBinding(b, f);
            }
        }

        return result;
    }

    private static Binding MapBinding(Binding? b, Func<ActionConfig, ActionConfig> f)
    {
        b ??= new();
        ActionConfig[] cycle = (b.Cycle ?? []).Where(a => a is not null).Select(f)
            .Where(a => a.Kind != ActionKind.None).ToArray();
        return b with
        {
            Press = f(b.Press ?? ActionConfig.None),
            Hold = b.Hold is null ? null : f(b.Hold),
            Double = b.Double is null ? null : f(b.Double),
            Triple = b.Triple is { } triple && f(triple) is { Kind: not ActionKind.None } kept ? kept : null,
            Cycle = cycle.Length == 0 ? null : cycle,
        };
    }

    private static IReadOnlyList<Macro> NormalizeMacros(IReadOnlyList<Macro>? macros, List<string> issues)
    {
        List<Macro> source = (macros ?? []).Where(m => m is not null).ToList();
        if (source.Count > MaxMacros)
        {
            issues.Add(Text.Pick($"Maximum {MaxMacros} macros. Extra macros were removed.", $"En fazla {MaxMacros} makro olabilir; fazlası silindi."));
            source = source.Take(MaxMacros).ToList();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Macro>(source.Count);
        foreach (Macro m in source)
        {
            string name = (m.Name ?? "").Trim();
            if (name.Length == 0)
            {
                name = Text.Pick("Macro", "Makro");
            }
            else if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength];
            }

            string id = IsId(m.Id) && !ids.Contains(m.Id) ? m.Id : NewId();
            ids.Add(id);

            List<MacroStep> steps = (m.Steps ?? []).Where(s => s is not null).ToList();
            if (steps.Count > MaxMacroSteps)
            {
                issues.Add(Text.Pick($"Macro \"{name}\": maximum {MaxMacroSteps} steps. Extra steps were removed.", $"Makro \"{name}\": en fazla {MaxMacroSteps} adım olabilir; fazlası silindi."));
                steps = steps.Take(MaxMacroSteps).ToList();
            }

            var cleaned = new List<MacroStep>(steps.Count);
            int dropped = 0;
            foreach (MacroStep s in steps)
            {
                if (Step(s) is { } ok)
                {
                    cleaned.Add(ok);
                }
                else
                {
                    dropped++;
                }
            }

            if (dropped > 0)
            {
                issues.Add(Text.Pick($"Macro \"{name}\": removed {dropped} steps with invalid keys.", $"Makro \"{name}\": tuşu geçersiz {dropped} adım silindi."));
            }

            result.Add(new Macro
            {
                Id = id,
                Name = name,
                Repeat = Defined(m.Repeat, MacroRepeat.Once),
                Steps = cleaned,
            });
        }

        return result;
    }

    private static MacroStep? Step(MacroStep s)
    {
        MacroOp op = Defined(s.Op, MacroOp.Down);
        switch (op)
        {
            case MacroOp.Down or MacroOp.Up or MacroOp.Tap:
                Key key = s.Key ?? Key.None;
                return key != Key.None && Enum.IsDefined(key) ? new MacroStep { Op = op, Key = key } : null;
            case MacroOp.MouseDown or MacroOp.MouseUp or MacroOp.Click:
                return new MacroStep { Op = op, Button = Defined(s.Button ?? MouseButton.Left, MouseButton.Left) };
            case MacroOp.Wheel:
                return new MacroStep
                {
                    Op = op,
                    Direction = Defined(s.Direction ?? WheelDirection.Up, WheelDirection.Up),
                    Amount = Math.Clamp(s.Amount ?? 1, 1, 10),
                };
            default:
                return new MacroStep { Op = MacroOp.Delay, Ms = Math.Clamp(s.Ms ?? 0, 0, 60_000) };
        }
    }

    private static MappingConfig NormalizeMapping(MappingConfig? m, HashSet<string> macroIds, List<string> issues)
    {
        m ??= new();
        TouchpadMapping pad = m.Touchpad ?? new();
        GyroMouseConfig gyro = m.GyroMouse ?? new();
        StickMappingPair sticks = m.Sticks ?? new();

        string layer = (m.LayerButton ?? "").Trim();
        if (string.Equals(layer, ButtonNames.None, StringComparison.OrdinalIgnoreCase))
        {
            layer = ButtonNames.None;
        }
        else if (ButtonNames.TryParse(layer, out Buttons lb))
        {
            layer = ButtonNames.Name(lb);
        }
        else
        {
            issues.Add(Text.Pick($"Unknown layer button ({layer}). Switched to PS.", $"Katman tuşu bilinmiyor ({layer}), PS seçildi."));
            layer = "ps";
        }

        ZoneLayout zoneLayout = Defined(pad.ZoneLayout, ZoneLayout.Halves);
        int zoneCount = zoneLayout == ZoneLayout.Quadrants ? 4 : 2;
        List<Binding?> zones = (pad.Zones ?? []).Take(zoneCount).Select(z => (Binding?)z).ToList();
        while (zones.Count < zoneCount)
        {
            zones.Add(new Binding());
        }

        SwipeActions swipes = pad.Swipes ?? new();

        string gyroButton = ButtonNames.TryParse(gyro.Button, out Buttons gb) ? ButtonNames.Name(gb) : "r1";
        if (gyro.Button is not null && !ButtonNames.TryParse(gyro.Button, out _))
        {
            issues.Add(Text.Pick($"Unknown gyro mouse button ({gyro.Button}). Switched to R1.", $"Gyro fare tuşu bilinmiyor ({gyro.Button}), R1 seçildi."));
        }

        return new MappingConfig
        {
            Enabled = m.Enabled,
            LayerButton = layer,
            Buttons = ButtonMap(m.Buttons, "", macroIds, issues),
            Layer = ButtonMap(m.Layer, " (katman)", macroIds, issues),
            Touchpad = new TouchpadMapping
            {
                Mode = Defined(pad.Mode, TouchpadMode.Off),
                Sensitivity = Clamp(pad.Sensitivity, 0.1, 5, 1),
                Acceleration = Clamp(pad.Acceleration, 0, 2, 0.5),
                Scroll = pad.Scroll,
                ScrollSpeed = Clamp(pad.ScrollSpeed, 0.1, 5, 1),
                TapToClick = pad.TapToClick,
                ZoneLayout = zoneLayout,
                Zones = zones.Select((z, i) => BindingOf(z, Text.Pick($"Touchpad zone {i + 1}", $"Dokunmatik bölge {i + 1}"), macroIds, issues)).ToArray(),
                Swipes = new SwipeActions
                {
                    Up = ActionOf(swipes.Up, Text.Pick("Swipe up", "Yukarı kaydırma"), macroIds, issues),
                    Down = ActionOf(swipes.Down, Text.Pick("Swipe down", "Aşağı kaydırma"), macroIds, issues),
                    Left = ActionOf(swipes.Left, Text.Pick("Swipe left", "Sola kaydırma"), macroIds, issues),
                    Right = ActionOf(swipes.Right, Text.Pick("Swipe right", "Sağa kaydırma"), macroIds, issues),
                },
            },
            GyroMouse = new GyroMouseConfig
            {
                Mode = Defined(gyro.Mode, GyroMouseMode.Off),
                Button = gyroButton,
                Axis = Defined(gyro.Axis, GyroAxis.Yaw),
                SensitivityX = Clamp(gyro.SensitivityX, 0.1, 10, 1),
                SensitivityY = Clamp(gyro.SensitivityY, 0.1, 10, 1),
                InvertX = gyro.InvertX,
                InvertY = gyro.InvertY,
                Smoothing = Clamp(gyro.Smoothing, 0, 0.9, 0.2),
                Deadzone = Clamp(gyro.Deadzone, 0, 5, 0.5),
            },
            Sticks = new StickMappingPair
            {
                Left = StickMap(sticks.Left),
                Right = StickMap(sticks.Right),
            },
        };
    }

    private static Dictionary<string, Binding> ButtonMap(
        IReadOnlyDictionary<string, Binding>? map, string suffix, HashSet<string> macroIds, List<string> issues)
    {
        var result = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach ((string key, Binding b) in map ?? new Dictionary<string, Binding>())
        {
            if (!ButtonNames.TryParse(key, out Buttons button))
            {
                issues.Add(Text.Pick($"Unknown button \"{key}\"{suffix}. Mapping removed.", $"Bilinmeyen tuş adı \"{key}\"{suffix}, ataması silindi."));
                continue;
            }

            string name = ButtonNames.Name(button);
            if (b is null || result.ContainsKey(name))
            {
                continue;
            }

            result[name] = BindingOf(b, name + suffix, macroIds, issues);
        }

        return result;
    }

    private static Binding BindingOf(Binding? b, string label, HashSet<string> macroIds, List<string> issues)
    {
        b ??= new();
        ActionConfig[] cycle = (b.Cycle ?? []).Select(a => ActionOf(a, label, macroIds, issues))
            .Where(a => a.Kind != ActionKind.None).Take(MaxCycleActions + 1).ToArray();
        if (cycle.Length > MaxCycleActions)
        {
            issues.Add(Text.Pick($"{label}: cycle limited to {MaxCycleActions} actions.", $"{label}: döngü {MaxCycleActions} eylemle sınırlandı."));
            cycle = cycle[..MaxCycleActions];
        }

        return new Binding
        {
            Press = ActionOf(b.Press, label, macroIds, issues),
            Hold = Optional(b.Hold, label + Text.Pick(" (hold)", " (basılı tutma)"), macroIds, issues),
            Double = Optional(b.Double, label + Text.Pick(" (double press)", " (çift basma)"), macroIds, issues),
            Triple = Optional(b.Triple, label + Text.Pick(" (triple press)", " (üçlü basma)"), macroIds, issues),
            Cycle = cycle.Length == 0 ? null : cycle,
            HoldMs = Math.Clamp(b.HoldMs, 150, 2000),
            DoubleMs = Math.Clamp(b.DoubleMs, 100, 600),
            Turbo = b.Turbo,
            TurboHz = Math.Clamp(b.TurboHz, 1, 30),
            Toggle = b.Toggle,
        };
    }

    // A hold or double action of kind none means "not set".
    private static ActionConfig? Optional(ActionConfig? a, string label, HashSet<string> macroIds, List<string> issues)
    {
        if (a is null)
        {
            return null;
        }

        ActionConfig cleaned = ActionOf(a, label, macroIds, issues);
        return cleaned.Kind == ActionKind.None ? null : cleaned;
    }

    private static ActionConfig ActionOf(ActionConfig? a, string label, HashSet<string> macroIds, List<string> issues)
    {
        if (a is null)
        {
            return ActionConfig.None;
        }

        switch (Defined(a.Kind, ActionKind.None))
        {
            case ActionKind.Key:
            {
                IReadOnlyList<Key> raw = a.Keys ?? [];
                Key[] keys = raw.Where(k => k != Key.None && Enum.IsDefined(k)).Distinct().ToArray();
                if (keys.Length > MaxChordKeys)
                {
                    keys = keys[..MaxChordKeys];
                }

                if (keys.Length != raw.Count && raw.Count > 0)
                {
                    issues.Add(Text.Pick($"{label}: removed invalid or duplicate keys and keys beyond the first {MaxChordKeys}.", $"{label}: geçersiz, tekrarlanan ya da {MaxChordKeys} tuştan fazlası silindi."));
                }

                return keys.Length == 0 ? ActionConfig.None : new ActionConfig { Kind = ActionKind.Key, Keys = keys };
            }

            case ActionKind.MouseButton:
                return new ActionConfig { Kind = ActionKind.MouseButton, Button = Defined(a.Button ?? MouseButton.Left, MouseButton.Left) };

            case ActionKind.Wheel:
                return new ActionConfig
                {
                    Kind = ActionKind.Wheel,
                    Direction = Defined(a.Direction ?? WheelDirection.Up, WheelDirection.Up),
                    Amount = Math.Clamp(a.Amount ?? 1, 1, 10),
                };

            case ActionKind.Macro:
                if (a.MacroId is { } macroId && macroIds.Contains(macroId))
                {
                    return new ActionConfig { Kind = ActionKind.Macro, MacroId = macroId };
                }

                issues.Add(Text.Pick($"{label}: macro not found. Action removed.", $"{label}: makro bulunamadı, eylem kaldırıldı."));
                return ActionConfig.None;

            case ActionKind.Profile:
            {
                string target = (a.ProfileId ?? "").Trim();
                if (target.Equals("next", StringComparison.OrdinalIgnoreCase) || target.Equals("previous", StringComparison.OrdinalIgnoreCase))
                {
                    return new ActionConfig { Kind = ActionKind.Profile, ProfileId = target.ToLowerInvariant() };
                }

                if (IsId(target))
                {
                    return new ActionConfig { Kind = ActionKind.Profile, ProfileId = target };
                }

                issues.Add(Text.Pick($"{label}: invalid profile. Action removed.", $"{label}: profil geçersiz, eylem kaldırıldı."));
                return ActionConfig.None;
            }

            case ActionKind.Run:
            {
                string path = (a.Path ?? "").Trim();
                if (path.Length == 0 || path.Length > MaxRunPath || !IsAbsolutePath(path))
                {
                    issues.Add(Text.Pick($"{label}: action removed. Enter a full program path of up to {MaxRunPath} characters.", $"{label}: program yolu geçersiz (tam yol, en fazla {MaxRunPath} karakter), eylem kaldırıldı."));
                    return ActionConfig.None;
                }

                string args = a.Args ?? "";
                if (args.Length > MaxRunArgs)
                {
                    issues.Add(Text.Pick($"{label}: program arguments shortened to {MaxRunArgs} characters.", $"{label}: program argümanları {MaxRunArgs} karaktere kısaltıldı."));
                    args = args[..MaxRunArgs];
                }

                return new ActionConfig { Kind = ActionKind.Run, Path = path, Args = args };
            }

            case ActionKind.MicMute:
                return new ActionConfig { Kind = ActionKind.MicMute };

            case ActionKind.Special:
                return new ActionConfig { Kind = ActionKind.Special, Special = Defined(a.Special ?? SpecialAction.Screenshot, SpecialAction.Screenshot) };

            default:
                return ActionConfig.None;
        }
    }

    private static bool IsAbsolutePath(string path)
    {
        try
        {
            return System.IO.Path.IsPathFullyQualified(path) && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static StickMapping StickMap(StickMapping? s)
    {
        s ??= new();
        StickKeys keys = s.Keys ?? new();
        return new StickMapping
        {
            Mode = Defined(s.Mode, StickMappingMode.Off),
            Keys = new StickKeys
            {
                Up = DefinedKey(keys.Up),
                Down = DefinedKey(keys.Down),
                Left = DefinedKey(keys.Left),
                Right = DefinedKey(keys.Right),
            },
            Threshold = Clamp(s.Threshold, 0.1, 0.9, 0.5),
            Speed = Clamp(s.Speed, 0.1, 5, 1),
        };
    }

    private static Key DefinedKey(Key k) => Enum.IsDefined(k) ? k : Key.None;
}
