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
    public const int MaxLayers = 4;
    public const int MaxLayerNameLength = 32;
    public const int MaxChords = 32;
    public const int MaxRadialMenus = 4;
    public const int MinRadialSlices = 2;
    public const int MaxRadialSlices = 8;
    public const int MaxRadialMenuNameLength = 32;
    public const int MaxRadialSliceLabelLength = 24;

    /// <summary>Replaces every <c>run</c> action with <c>none</c>; used for imported profiles.</summary>
    public static Profile StripRunActions(Profile p, out int removed)
    {
        int count = 0;
        ActionConfig Strip(ActionConfig? a)
        {
            if (a?.Kind == ActionKind.Run)
            {
                count++;
                return ActionConfig.None;
            }

            return a ?? ActionConfig.None;
        }

        MappingConfig m = p.Mapping ?? new();
        TriggerMapping StripTrigger(TriggerMapping? trigger)
        {
            trigger ??= new();
            return trigger with { Soft = Strip(trigger.Soft), Full = Strip(trigger.Full) };
        }

        StickMapping StripRing(StickMapping? stick)
        {
            stick ??= new();
            return stick with { RingAction = Strip(stick.RingAction) };
        }

        MappingLayer[] layers = (m.Layers ?? [])
            .Select(l => (l ?? new()) with { Buttons = MapBindings(l?.Buttons, Strip) }).ToArray();
        TriggerMappingPair triggers = m.Triggers ?? new();
        StickMappingPair sticks = m.Sticks ?? new();
        TouchpadMapping pad = m.Touchpad ?? new();
        SwipeActions swipes = pad.Swipes ?? new();
        Profile result = p with
        {
            Mapping = m with
            {
                Buttons = MapBindings(m.Buttons, Strip),
                LayerButton = layers.Length == 0 ? m.LayerButton : layers[0].Button,
                Layer = layers.Length == 0 ? MapBindings(m.Layer, Strip) : layers[0].Buttons,
                Layers = layers,
                RadialMenus = (m.RadialMenus ?? []).Where(r => r is not null)
                    .Select(r => r with { Slices = (r.Slices ?? []).Select(sl => (sl ?? new()) with { Action = Strip(sl?.Action) }).ToArray() })
                    .ToArray(),
                Chords = (m.Chords ?? []).Where(c => c is not null)
                    .Select(c => c with { Action = Strip(c.Action) })
                    .Where(c => c.Action.Kind != ActionKind.None).ToArray(),
                Triggers = new() { Left = StripTrigger(triggers.Left), Right = StripTrigger(triggers.Right) },
                Sticks = new() { Left = StripRing(sticks.Left), Right = StripRing(sticks.Right) },
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

        // Menus first: a button may only point at a menu that survives.
        IReadOnlyList<RadialMenu> radialMenus = NormalizeRadialMenus(m.RadialMenus, macroIds, issues);
        HashSet<string> radialIds = radialMenus.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        MappingLayer[] layers = NormalizeLayers(m, macroIds, radialIds, issues);
        TriggerMappingPair triggers = m.Triggers ?? new();
        FlickStickConfig flick = m.FlickStick ?? new();

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
            LayerButton = layers[0].Button,
            Buttons = ButtonMap(m.Buttons, "", macroIds, issues, radialIds),
            Layer = layers[0].Buttons,
            Layers = layers,
            RadialMenus = radialMenus,
            ChordWindowMs = m.ChordWindowMs == 0 ? 50 : Math.Clamp(m.ChordWindowMs, 20, 300),
            Chords = NormalizeChords(m.Chords, macroIds, issues),
            Triggers = new()
            {
                Left = TriggerOf(triggers.Left, "L2", macroIds, issues),
                Right = TriggerOf(triggers.Right, "R2", macroIds, issues),
            },
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
                UseCalibration = gyro.UseCalibration,
            },
            Sticks = new StickMappingPair
            {
                Left = StickMap(sticks.Left, "L3", macroIds, issues),
                Right = StickMap(sticks.Right, "R3", macroIds, issues),
            },
            FlickStick = flick with
            {
                CountsPer360 = Math.Clamp(flick.CountsPer360, 500, 30000),
                Threshold = Clamp(flick.Threshold, 0.50, 0.98, 0.90),
                FlickTimeMs = Math.Clamp(flick.FlickTimeMs, 30, 300),
                Snap = Defined(flick.Snap, FlickSnapMode.Off),
                DeadzoneAngle = Clamp(flick.DeadzoneAngle, 0, 45, 0),
                Smoothing = Clamp(flick.Smoothing, 0, 0.9, 0.2),
            },
        };
    }

    private static IReadOnlyList<RadialMenu> NormalizeRadialMenus(IReadOnlyList<RadialMenu>? menus, HashSet<string> macroIds, List<string> issues)
    {
        List<RadialMenu> source = (menus ?? []).Where(r => r is not null).ToList();
        if (source.Count > MaxRadialMenus)
        {
            issues.Add(Text.Pick($"Maximum {MaxRadialMenus} radial menus. Extra menus were removed.", $"En fazla {MaxRadialMenus} daire menü olabilir; fazlası silindi."));
            source = source.Take(MaxRadialMenus).ToList();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RadialMenu>(source.Count);
        foreach (RadialMenu menu in source)
        {
            string name = (menu.Name ?? "").Trim();
            if (name.Length == 0)
            {
                name = Text.Pick("Radial menu", "Daire menü");
            }
            else if (name.Length > MaxRadialMenuNameLength)
            {
                name = name[..MaxRadialMenuNameLength];
            }

            string id = IsId(menu.Id) && !ids.Contains(menu.Id) ? menu.Id : NewId();
            ids.Add(id);

            List<RadialSlice?> slices = (menu.Slices ?? []).Select(sl => (RadialSlice?)sl).ToList();
            if (slices.Count > MaxRadialSlices)
            {
                issues.Add(Text.Pick($"Radial menu \"{name}\": maximum {MaxRadialSlices} slices. Extra slices were removed.", $"Daire menü \"{name}\": en fazla {MaxRadialSlices} dilim olabilir; fazlası silindi."));
                slices = slices.Take(MaxRadialSlices).ToList();
            }
            else if (slices.Count < MinRadialSlices)
            {
                issues.Add(Text.Pick($"Radial menu \"{name}\": at least {MinRadialSlices} slices are needed. Empty slices were added.", $"Daire menü \"{name}\": en az {MinRadialSlices} dilim gerekir; boş dilim eklendi."));
                while (slices.Count < MinRadialSlices)
                {
                    slices.Add(null);
                }
            }

            result.Add(new RadialMenu
            {
                Id = id,
                Name = name,
                Stick = Defined(menu.Stick, RadialStick.Left),
                Slices = slices.Select((sl, i) =>
                {
                    string label = (sl?.Label ?? "").Trim();
                    return new RadialSlice
                    {
                        Label = label.Length > MaxRadialSliceLabelLength ? label[..MaxRadialSliceLabelLength] : label,
                        Action = ActionOf(sl?.Action, $"{name} {i + 1}", macroIds, issues),
                    };
                }).ToArray(),
            });
        }

        return result;
    }

    private static MappingLayer[] NormalizeLayers(MappingConfig m, HashSet<string> macroIds, HashSet<string> radialIds, List<string> issues)
    {
        MappingLayer[] layers = m.ShiftLayers().Take(MaxLayers + 1).ToArray();
        if (layers.Length > MaxLayers)
        {
            issues.Add(Text.Pick("Extra shift layers were removed.", "Fazla katmanlar kaldırıldı."));
            layers = layers[..MaxLayers];
        }

        var activators = new HashSet<Buttons>();
        for (int i = 0; i < layers.Length; i++)
        {
            MappingLayer layer = layers[i] ?? new();
            string name = (layer.Name ?? "").Trim();
            if (name.Length == 0)
            {
                name = Text.Pick($"Layer {i + 1}", $"Katman {i + 1}");
            }

            string button = (layer.Button ?? "").Trim();
            string fallback = i == 0 ? "ps" : ButtonNames.None;
            if (button.Length == 0)
            {
                button = fallback;
            }
            else if (!button.Equals(ButtonNames.None, StringComparison.OrdinalIgnoreCase) && !ButtonNames.TryParse(button, out _))
            {
                issues.Add(Text.Pick($"Unknown layer button ({button}). Switched to {fallback}.", $"Katman tuşu bilinmiyor ({button}), {fallback} seçildi."));
                button = fallback;
            }

            if (ButtonNames.TryParse(button, out Buttons flag) && !activators.Add(flag))
            {
                issues.Add(Text.Pick($"Duplicate layer button ({button}). Cleared.", $"Tekrarlanan katman tuşu ({button}) kaldırıldı."));
                flag = Buttons.None;
            }

            layers[i] = new MappingLayer
            {
                Name = name[..Math.Min(name.Length, MaxLayerNameLength)],
                Button = ButtonNames.Name(flag),
                Mode = Defined(layer.Mode, LayerMode.Hold),
                Buttons = ButtonMap(layer.Buttons, $" ({name})", macroIds, issues, radialIds),
            };
        }

        return layers;
    }

    private static IReadOnlyList<ButtonChord> NormalizeChords(IReadOnlyList<ButtonChord>? chords, HashSet<string> macroIds, List<string> issues)
    {
        var result = new List<ButtonChord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ButtonChord chord in chords ?? [])
        {
            if (chord is null)
            {
                continue;
            }

            string[] buttons = (chord.Buttons ?? []).Select(b => ButtonNames.TryParse(b, out Buttons flag) ? flag : Buttons.None)
                .Where(b => b != Buttons.None).Distinct().Order().Select(ButtonNames.Name).ToArray();
            if (buttons.Length < 2)
            {
                issues.Add(Text.Pick("Chord removed: it needs at least two distinct buttons.", "Akor kaldırıldı: en az iki farklı tuş gerekli."));
                continue;
            }

            if (buttons.Length > 3)
            {
                issues.Add(Text.Pick("Chord limited to three buttons.", "Akor üç tuşla sınırlandı."));
                buttons = buttons[..3];
            }

            ActionConfig action = ActionOf(chord.Action, string.Join(" + ", buttons), macroIds, issues);
            if (action.Kind == ActionKind.None)
            {
                continue;
            }

            if (!seen.Add(string.Join(",", buttons)))
            {
                issues.Add(Text.Pick("Duplicate chord removed.", "Tekrarlanan akor kaldırıldı."));
                continue;
            }

            if (result.Count == MaxChords)
            {
                issues.Add(Text.Pick("Extra chords were removed.", "Fazla akorlar kaldırıldı."));
                break;
            }

            result.Add(new ButtonChord { Buttons = buttons, Action = action });
        }

        return result;
    }

    private static TriggerMapping TriggerOf(TriggerMapping? t, string label, HashSet<string> macroIds, List<string> issues)
    {
        t ??= new();
        double soft = Clamp(t.SoftThreshold, 0.05, 0.90, 0.25);
        return t with
        {
            Mode = Defined(t.Mode, TriggerStageMode.Off),
            SoftThreshold = soft,
            FullThreshold = Math.Max(soft + 0.05, Clamp(t.FullThreshold, 0.15, 1, 0.95)),
            Hysteresis = Clamp(t.Hysteresis, 0.01, 0.20, 0.05),
            HipFireMs = Math.Clamp(t.HipFireMs, 20, 300),
            Soft = ActionOf(t.Soft, label, macroIds, issues),
            Full = ActionOf(t.Full, label, macroIds, issues),
        };
    }

    private static Dictionary<string, Binding> ButtonMap(
        IReadOnlyDictionary<string, Binding>? map, string suffix, HashSet<string> macroIds, List<string> issues,
        HashSet<string>? radialIds = null)
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

            result[name] = BindingOf(b, name + suffix, macroIds, issues, radialIds);
        }

        return result;
    }

    private static Binding BindingOf(Binding? b, string label, HashSet<string> macroIds, List<string> issues,
        HashSet<string>? radialIds = null)
    {
        b ??= new();
        ActionConfig[] cycle = (b.Cycle ?? []).Select(a => ActionOf(a, label, macroIds, issues))
            .Where(a => a.Kind != ActionKind.None).Take(MaxCycleActions + 1).ToArray();
        if (cycle.Length > MaxCycleActions)
        {
            issues.Add(Text.Pick($"{label}: cycle limited to {MaxCycleActions} actions.", $"{label}: döngü {MaxCycleActions} eylemle sınırlandı."));
            cycle = cycle[..MaxCycleActions];
        }

        ActionConfig press = ActionOf(b.Press, label, macroIds, issues, radialIds);
        // A radial menu is the plain press: nothing may delay, repeat or latch it.
        bool radial = press is { Kind: ActionKind.Special, Special: SpecialAction.RadialMenu };
        // An explicit none beside the press is just "not set" and no conflict.
        if (radial && (b.Hold is { Kind: not ActionKind.None } || b.Double is { Kind: not ActionKind.None } ||
            b.Triple is { Kind: not ActionKind.None } || cycle.Length > 0 || b.Turbo || b.Toggle))
        {
            issues.Add(Text.Pick($"{label}: a radial menu cannot be combined with hold, double press, triple press, cycle, turbo or toggle. Those were removed.", $"{label}: daire menü basılı tutma, çift/üçlü basma, döngü, turbo ya da kilitleme ile birlikte kullanılamaz; bunlar kaldırıldı."));
        }

        return new Binding
        {
            Press = press,
            Hold = radial ? null : Optional(b.Hold, label + Text.Pick(" (hold)", " (basılı tutma)"), macroIds, issues),
            Double = radial ? null : Optional(b.Double, label + Text.Pick(" (double press)", " (çift basma)"), macroIds, issues),
            Triple = radial ? null : Optional(b.Triple, label + Text.Pick(" (triple press)", " (üçlü basma)"), macroIds, issues),
            Cycle = radial || cycle.Length == 0 ? null : cycle,
            HoldMs = Math.Clamp(b.HoldMs, 150, 2000),
            DoubleMs = Math.Clamp(b.DoubleMs, 100, 600),
            Turbo = !radial && b.Turbo,
            TurboHz = Math.Clamp(b.TurboHz, 1, 30),
            Toggle = !radial && b.Toggle,
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

    /// <param name="radialIds">Menus a radial menu action may point at; null where a radial menu is not allowed at all.</param>
    private static ActionConfig ActionOf(ActionConfig? a, string label, HashSet<string> macroIds, List<string> issues,
        HashSet<string>? radialIds = null)
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
            {
                SpecialAction special = Defined(a.Special ?? SpecialAction.Screenshot, SpecialAction.Screenshot);
                if (special != SpecialAction.RadialMenu)
                {
                    return new ActionConfig { Kind = ActionKind.Special, Special = special };
                }

                if (radialIds is null)
                {
                    issues.Add(Text.Pick($"{label}: a radial menu can only be the press action of a button. Action removed.", $"{label}: daire menü yalnızca bir tuşun basma eylemi olabilir, eylem kaldırıldı."));
                    return ActionConfig.None;
                }

                if (a.RadialMenuId is { } menuId && radialIds.Contains(menuId))
                {
                    return new ActionConfig { Kind = ActionKind.Special, Special = special, RadialMenuId = menuId };
                }

                issues.Add(Text.Pick($"{label}: radial menu not found. Action removed.", $"{label}: daire menü bulunamadı, eylem kaldırıldı."));
                return ActionConfig.None;
            }

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

    private static StickMapping StickMap(StickMapping? s, string label, HashSet<string> macroIds, List<string> issues)
    {
        s ??= new();
        StickKeys keys = s.Keys ?? new();
        double ringThreshold = Clamp(s.RingThreshold, 0.20, 0.98, 0.90);
        double ringHysteresis = Clamp(s.RingHysteresis, 0.01, 0.20, 0.05);
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
            RingMode = Defined(s.RingMode, StickRingMode.Off),
            RingThreshold = ringThreshold,
            RingHysteresis = ringHysteresis < ringThreshold ? ringHysteresis : ringThreshold - 0.01,
            RingAction = ActionOf(s.RingAction, label, macroIds, issues),
        };
    }

    private static Key DefinedKey(Key k) => Enum.IsDefined(k) ? k : Key.None;
}
