using System.Text.RegularExpressions;
using Tactile.Audio;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Clamps everything to the ranges in docs/contract.md. Never throws; lists what it changed.</summary>
public static partial class Validation
{
    public const int MaxNameLength = 64;

    public static Profile Normalize(Profile p, List<string>? issues = null)
    {
        issues ??= [];
        // Records deserialized from JSON may hold null where the type says otherwise.
        LightbarConfig lb = p.Lightbar ?? new();
        PlayerLedConfig leds = p.PlayerLeds ?? new();
        TriggerPair triggers = p.Triggers ?? new();
        TriggerResponsePair response = p.TriggerResponse ?? new();
        StickPair sticks = p.Sticks ?? new();
        GyroConfig gyro = p.Gyro ?? new();
        RumbleConfig rumble = p.Rumble ?? new();
        HapticsConfig haptics = p.Haptics ?? new();
        string audioApp = (haptics.App ?? "").Trim();
        string speakerApp = (haptics.SpeakerApp ?? "").Trim();

        string name = (p.Name ?? "").Trim();
        if (name.Length == 0)
        {
            name = "Profil";
        }
        else if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        // Macros first: actions may only point at macros that survive.
        IReadOnlyList<Macro> macros = NormalizeMacros(p.Macros, issues);
        MappingConfig mapping = NormalizeMapping(p.Mapping, macros.Select(m => m.Id).ToHashSet(StringComparer.Ordinal), issues);

        return new Profile
        {
            Version = Profile.CurrentVersion,
            Id = IsId(p.Id) ? p.Id : NewId(),
            Name = name,
            Games = (p.Games ?? [])
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => Path.GetFileName(g.Trim()))
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Lightbar = new LightbarConfig
            {
                Effect = Defined(lb.Effect, LightbarEffect.Static),
                Color = Color(lb.Color, "#3060FF"),
                Color2 = Color(lb.Color2, "#000000"),
                Speed = Clamp(lb.Speed, 0.1, 5, 1),
                Brightness = Defined(lb.Brightness, LedBrightness.High),
            },
            PlayerLeds = new PlayerLedConfig
            {
                Mode = Defined(leds.Mode, PlayerLedMode.Number),
                Number = Math.Clamp(leds.Number, 1, PlayerLeds.MaxNumber),
                Mask = leds.Mask & 0x1F,
            },
            MicLed = Defined(p.MicLed, MicLed.Off),
            Triggers = new TriggerPair
            {
                Left = Trigger(triggers.Left, "Sol tetik", issues),
                Right = Trigger(triggers.Right, "Sağ tetik", issues),
            },
            TriggerResponse = new TriggerResponsePair
            {
                Left = Response(response.Left, "Sol tetik eğrisi", issues),
                Right = Response(response.Right, "Sağ tetik eğrisi", issues),
            },
            Sticks = new StickPair
            {
                Left = Stick(sticks.Left, "Sol analog", issues),
                Right = Stick(sticks.Right, "Sağ analog", issues),
            },
            Gyro = new GyroConfig
            {
                Sensitivity = Clamp(gyro.Sensitivity, 0.1, 5, 1),
                Smoothing = Clamp(gyro.Smoothing, 0, 0.95, 0.2),
                InvertX = gyro.InvertX,
                InvertY = gyro.InvertY,
            },
            Rumble = new RumbleConfig { Strength = Clamp(rumble.Strength, 0, 2, 1) },
            Haptics = haptics with
            {
                Audio = Defined(haptics.Audio, AudioHapticSource.Off),
                App = audioApp[..Math.Min(audioApp.Length, 260)],
                Mode = Defined(haptics.Mode, HapticMode.Game),
                Strength = Clamp(haptics.Strength, 0, 2, 1),
                ClickStrength = Clamp(haptics.ClickStrength, 0, 1, 0.6),
                Speaker = Defined(haptics.Speaker, AudioHapticSource.Off),
                SpeakerApp = speakerApp[..Math.Min(speakerApp.Length, 260)],
                SpeakerVolume = Clamp(haptics.SpeakerVolume, 0, 1, 0.8),
                GameAudio = Defined(haptics.GameAudio, GameAudioMode.Both),
                GameStrength = Clamp(haptics.GameStrength, 0, 2, 1),
            },
            Mapping = mapping,
            Macros = macros,
            Emulation = NormalizeEmulation(p.Emulation, issues),
            Telemetry = NormalizeTelemetry(p.Telemetry, issues),
        };
    }

    private static TelemetryConfig NormalizeTelemetry(TelemetryConfig? config, List<string> issues)
    {
        const int maxRules = 32;
        IReadOnlyList<TelemetryRule> input = config?.Rules ?? [];
        var rules = new List<TelemetryRule>(Math.Min(input.Count, maxRules));
        if (input.Count > maxRules)
        {
            issues.Add("Telemetri: ilk 32 kuraldan sonrakiler kaldırıldı.");
        }

        foreach (TelemetryRule? rule in input.Take(maxRules))
        {
            if (rule is null || rule.Signal == TelemetrySignal.Unknown || rule.Target == TelemetryTarget.Unknown ||
                !Enum.IsDefined(rule.Signal) || !Enum.IsDefined(rule.Target))
            {
                issues.Add("Telemetri: geçersiz sinyal veya hedef içeren kural kaldırıldı.");
                continue;
            }

            (double Min, double Max) inputRange = rule.Signal switch
            {
                TelemetrySignal.SpeedKmh => (0, Math.Sqrt(3) * float.MaxValue * 3.6),
                TelemetrySignal.SlipFront or TelemetrySignal.SlipRear or TelemetrySignal.Surface => (0, float.MaxValue),
                TelemetrySignal.Gear => (0, 255),
                TelemetrySignal.Steer => (-1, 1),
                TelemetrySignal.Boost => (-float.MaxValue, float.MaxValue),
                _ => (0, 1),
            };
            int outputMin = 0;
            int outputMax = rule.Target == TelemetryTarget.PlayerLeds ? 5 : 255;
            TriggerConfig? trigger = null;
            if (rule.Target is TelemetryTarget.LeftTrigger or TelemetryTarget.RightTrigger)
            {
                trigger = rule.Trigger;
                if (trigger?.Params is null || trigger.Params.Values.Any(v => !double.IsFinite(v) || v < int.MinValue || v > int.MaxValue))
                {
                    issues.Add("Telemetri: geçersiz tetik şablonu içeren kural kaldırıldı.");
                    continue;
                }

                if (rule.Param is { } param)
                {
                    if (TelemetryRules.ParameterRange(trigger.Mode, param) is not { } range)
                    {
                        issues.Add("Telemetri: geçersiz tetik parametresi içeren kural kaldırıldı.");
                        continue;
                    }

                    (outputMin, outputMax) = range;
                }

                trigger = trigger with
                {
                    Params = new Dictionary<string, double>(trigger.Params),
                    Zones = trigger.Zones?.ToArray(),
                    Frequency = Math.Clamp(trigger.Frequency, 0, 255),
                };
                if (!TelemetryRules.TryTrigger(trigger, rule.Param, outputMin, out _))
                {
                    issues.Add("Telemetri: geçersiz tetik şablonu içeren kural kaldırıldı.");
                    continue;
                }
            }

            rules.Add(rule with
            {
                From = Clamp(rule.From, inputRange.Min, inputRange.Max, 0),
                To = Clamp(rule.To, inputRange.Min, inputRange.Max, 1),
                Exponent = Clamp(rule.Exponent, 0.2, 5, 1),
                Trigger = trigger,
                Min = Clamp(rule.Min, outputMin, outputMax, outputMin),
                Max = Clamp(rule.Max, outputMin, outputMax, outputMin),
                ColorFrom = NormalizeTelemetryColor(rule.ColorFrom.GetValueOrDefault()),
                ColorTo = NormalizeTelemetryColor(rule.ColorTo.GetValueOrDefault()),
                BlinkAt = rule.BlinkAt is double blink && double.IsFinite(blink) ? Math.Clamp(blink, 0, 1) : null,
            });
        }

        return new TelemetryConfig { Rules = rules.AsReadOnly() };
    }

    private static TelemetryColor NormalizeTelemetryColor(TelemetryColor color) =>
        new(Math.Clamp(color.R, 0, 255), Math.Clamp(color.G, 0, 255), Math.Clamp(color.B, 0, 255));

    public static AppSettings Normalize(AppSettings s)
    {
        IntegrationSettings integration = s.Integration ?? new();
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string mac, string id) in s.ControllerDefaults ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(mac) && IsId(id))
            {
                defaults[mac.Trim().ToUpperInvariant()] = id;
            }
        }

        return new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            StartWithWindows = s.StartWithWindows,
            CloseToTray = s.CloseToTray,
            Language = Defined(s.Language, Language.Tr),
            AutoProfile = s.AutoProfile,
            AutoProfileDelayMs = Math.Clamp(s.AutoProfileDelayMs, 0, 10_000),
            DefaultProfileId = IsId(s.DefaultProfileId) ? s.DefaultProfileId : null,
            ControllerDefaults = defaults,
            IdleSleepMinutes = Math.Clamp(s.IdleSleepMinutes, 0, 240),
            LowBatteryWarnings = (s.LowBatteryWarnings ?? [])
                .Where(v => v is >= 1 and <= 99)
                .Distinct()
                .OrderDescending()
                .Take(5)
                .ToArray(),
            RunAsAdminHint = s.RunAsAdminHint,
            Integration = integration with
            {
                UdpPort = Math.Clamp(integration.UdpPort, 1024, 65535),
                ReleaseAfterSeconds = Math.Clamp(integration.ReleaseAfterSeconds, 0, 3600),
                ForzaPort = Math.Clamp(integration.ForzaPort, 1024, 65535),
            },
            Emulation = new EmulationSettings
            {
                Enabled = s.Emulation?.Enabled ?? true,
                HideController = s.Emulation?.HideController ?? true,
                Audio = s.Emulation?.Audio ?? true,
                KeepAudioDefaults = s.Emulation?.KeepAudioDefaults ?? true,
                HapticBuffer = Math.Clamp(s.Emulation?.HapticBuffer ?? 64, 16, 128),
                SpeakerPacket = Defined(s.Emulation?.SpeakerPacket ?? SpeakerPacket.Single36, SpeakerPacket.Single36),
            },
        };
    }

    private static EmulationConfig NormalizeEmulation(EmulationConfig? e, List<string> issues)
    {
        e ??= new();
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string target) in e.Remap ?? new Dictionary<string, string>())
        {
            if (!ButtonNames.TryParse(key, out Buttons source))
            {
                issues.Add($"Sanal kontrolcü: bilinmeyen tuş adı \"{key}\", yeniden ataması silindi.");
                continue;
            }

            string to;
            if (string.Equals(target?.Trim(), ButtonNames.None, StringComparison.OrdinalIgnoreCase))
            {
                to = ButtonNames.None;
            }
            else if (ButtonNames.TryParse(target, out Buttons t))
            {
                to = ButtonNames.Name(t);
            }
            else
            {
                issues.Add($"Sanal kontrolcü: \"{key}\" için hedef tuş bilinmiyor ({target}), yeniden ataması silindi.");
                continue;
            }

            string from = ButtonNames.Name(source);
            // Mapping a button to itself is the same as not listing it.
            if (from != to)
            {
                remap.TryAdd(from, to);
            }
        }

        return new EmulationConfig
        {
            Mode = Defined(e.Mode, EmulationMode.Off),
            Remap = remap,
            SwapSticks = e.SwapSticks,
            HideMappedButtons = e.HideMappedButtons,
        };
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsId(string? id) =>
        id is { Length: 32 } && Guid.TryParseExact(id, "N", out _) && id.All(c => !char.IsUpper(c));

    private static TriggerConfig Trigger(TriggerConfig? c, string label, List<string> issues)
    {
        if (c is null)
        {
            return TriggerConfig.Off;
        }

        if (!Enum.IsDefined(c.Mode))
        {
            issues.Add($"{label}: bilinmeyen mod, kapatıldı.");
            return TriggerConfig.Off;
        }

        var cleaned = c with
        {
            Params = new Dictionary<string, double>(c.Params ?? new Dictionary<string, double>()),
            Zones = c.Zones?.ToArray(),
            Frequency = Math.Clamp(c.Frequency, 0, 255),
        };
        if (!Triggers.TryToEffect(cleaned, out _, out string? error))
        {
            issues.Add($"{label}: {error} Kapatıldı.");
            return TriggerConfig.Off;
        }

        return cleaned;
    }

    private static TriggerResponse Response(TriggerResponse? r, string label, List<string> issues)
    {
        r ??= new();
        int deadzone = Math.Clamp(r.Deadzone, 0, 254);
        (CurveKind curve, double[][]? points) = Curve(r.Curve, r.Points, label, issues);
        return new TriggerResponse
        {
            Deadzone = deadzone,
            Max = Math.Clamp(r.Max, deadzone + 1, 255),
            Curve = curve,
            Exponent = Clamp(r.Exponent, ResponseCurve.MinExponent, ResponseCurve.MaxExponent, 1),
            Points = points,
        };
    }

    private static StickConfig Stick(StickConfig? s, string label, List<string> issues)
    {
        s ??= new();
        (CurveKind curve, double[][]? points) = Curve(s.Curve, s.Points, label, issues);
        double inner = Clamp(s.InnerDeadzone, 0, 0.9, 0.05);
        // Keep some travel between the two deadzones.
        double outer = Math.Min(Clamp(s.OuterDeadzone, 0, 0.5, 0.02), 0.95 - inner);
        return new StickConfig
        {
            InnerDeadzone = inner,
            OuterDeadzone = outer,
            Shape = Defined(s.Shape, StickShape.Round),
            Curve = curve,
            Exponent = Clamp(s.Exponent, ResponseCurve.MinExponent, ResponseCurve.MaxExponent, 1),
            Points = points,
            AntiDeadzone = Clamp(s.AntiDeadzone, 0, 0.9, 0),
            InvertX = s.InvertX,
            InvertY = s.InvertY,
        };
    }

    private static (CurveKind, double[][]?) Curve(CurveKind kind, double[][]? points, string label, List<string> issues)
    {
        if (!Enum.IsDefined(kind))
        {
            kind = CurveKind.Linear;
        }

        bool valid = ResponseCurve.IsValid(points);
        if (kind == CurveKind.Custom && !valid)
        {
            issues.Add($"{label}: özel eğri noktaları geçersiz, doğrusala dönüldü.");
            return (CurveKind.Linear, null);
        }

        return (kind, valid ? points!.Select(p => p.ToArray()).ToArray() : null);
    }

    private static T Defined<T>(T value, T fallback) where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    private static string Color(string? hex, string fallback) =>
        hex is not null && HexColor().IsMatch(hex) ? "#" + hex.TrimStart('#').ToUpperInvariant() : fallback;

    [GeneratedRegex("^#?[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
