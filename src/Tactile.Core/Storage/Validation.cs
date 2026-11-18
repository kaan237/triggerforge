using System.Text.RegularExpressions;
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

        string name = (p.Name ?? "").Trim();
        if (name.Length == 0)
        {
            name = "Profil";
        }
        else if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

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
        };
    }

    public static AppSettings Normalize(AppSettings s)
    {
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
