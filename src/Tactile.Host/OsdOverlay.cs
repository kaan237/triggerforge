using System.Diagnostics;
using Tactile.Core;
using Tactile.Emulation;

namespace Tactile.Host;

public enum OsdEventKind { Profile, Layer, Pause, LowBattery, VirtualController }

public sealed record OsdNotification(OsdEventKind Kind, string Title, string Detail);

/// <summary>What the coordinator draws on; a fake in tests, a layered window in the app.</summary>
internal interface IOsdView : IDisposable
{
    void Show(OsdNotification notification, OsdSettings settings);

    void Hide();
}

/// <summary>Injected time and one restartable one-shot timer, so the coordinator needs no real clock.</summary>
internal interface IOsdClock : IDisposable
{
    TimeSpan Now { get; }

    /// <summary>Runs <paramref name="callback"/> once after the delay; replaces any pending schedule.</summary>
    void Schedule(TimeSpan delay, Action callback);

    void Cancel();
}

/// <summary>The app's clock: a UI-thread timer, so the callback runs where the view lives.</summary>
internal sealed class FormsOsdClock : IOsdClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new();
    private Action? _callback;

    public FormsOsdClock() => _timer.Tick += (_, _) =>
    {
        _timer.Stop();
        Action? callback = _callback;
        _callback = null;
        callback?.Invoke();
    };

    public TimeSpan Now => _watch.Elapsed;

    public void Schedule(TimeSpan delay, Action callback)
    {
        _timer.Stop();
        _callback = callback;
        _timer.Interval = (int)Math.Clamp(delay.TotalMilliseconds, 1, int.MaxValue);
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _callback = null;
    }

    public void Dispose()
    {
        Cancel();
        _timer.Dispose();
    }
}

/// <summary>Wording of the five OSD events, in the current language.</summary>
internal static class OsdText
{
    public static OsdNotification Profile(string name, string? reason)
    {
        const string game = "game:";
        string detail = reason is not null && reason.StartsWith(game, StringComparison.Ordinal) && reason.Length > game.Length
            ? $"{name} ({reason[game.Length..]})"
            : name;
        return new OsdNotification(OsdEventKind.Profile, Tactile.Core.Text.Pick("Profile", "Profil"), detail);
    }

    public static OsdNotification Layer(string? name) => new(OsdEventKind.Layer,
        Tactile.Core.Text.Pick("Layer", "Katman"), name ?? Tactile.Core.Text.Pick("Base", "Temel"));

    public static OsdNotification Pause(bool paused, string? reason)
    {
        if (!paused)
        {
            return new OsdNotification(OsdEventKind.Pause, Tactile.Core.Text.Pick("Mapping resumed", "Eşleme sürdürüldü"), "");
        }

        string detail = reason switch
        {
            "shortcut" => Tactile.Core.Text.Pick("Hotkey", "Kısayol"),
            "fullscreen" => Tactile.Core.Text.Pick("Fullscreen", "Tam ekran"),
            _ => "",
        };
        return new OsdNotification(OsdEventKind.Pause, Tactile.Core.Text.Pick("Mapping paused", "Eşleme duraklatıldı"), detail);
    }

    public static OsdNotification LowBattery(string message) => new(OsdEventKind.LowBattery,
        Tactile.Core.Text.Pick("Low battery", "Düşük pil"), message);

    public static OsdNotification Mode(EmulationMode mode) => new(OsdEventKind.VirtualController,
        Tactile.Core.Text.Pick("Virtual controller", "Sanal kontrolcü"), mode switch
        {
            EmulationMode.Xbox360 => "Xbox 360",
            EmulationMode.DualSense => "DualSense",
            EmulationMode.DualShock4 => "DualShock 4",
            EmulationMode.Auto => Tactile.Core.Text.Pick("Auto", "Otomatik"),
            _ => Tactile.Core.Text.Pick("Off", "Kapalı"),
        });
}

/// <summary>
/// Decides when the OSD speaks. Initial values are recorded silently; only real transitions notify.
/// UI thread only: every event is marshalled by the caller. Nothing shows while the OSD is disabled,
/// but values keep being tracked so enabling it later does not replay the past.
/// </summary>
internal sealed class OsdCoordinator : IDisposable
{
    /// <summary>A layer change this soon after a profile change is the profile's own reset.</summary>
    internal static readonly TimeSpan LayerSuppression = TimeSpan.FromMilliseconds(400);

    /// <summary>Profile and mode changes this soon after the start are the controllers connecting.</summary>
    internal static readonly TimeSpan StartupSilence = TimeSpan.FromSeconds(5);

    private readonly IOsdView _view;
    private readonly IOsdClock _clock;
    private readonly Func<OsdSettings> _settings;
    private string? _profileId;
    private string? _layer;
    private bool _layerKnown;
    private bool? _paused;
    private EmulationMode? _mode;
    private TimeSpan? _profileChangedAt;
    private TimeSpan? _seededAt;
    private OsdNotification? _current;
    private OsdSettings _applied;
    private bool _disposed;

    public OsdCoordinator(IOsdView view, IOsdClock clock, Func<OsdSettings> settings)
    {
        _view = view;
        _clock = clock;
        _settings = settings;
        _applied = settings();
    }

    public bool Visible => _current is not null;

    /// <summary>Records the starting values without showing anything.</summary>
    public void Seed(string profileId, string? layer, bool paused, EmulationMode mode)
    {
        _profileId = profileId;
        _layer = layer;
        _layerKnown = true;
        _paused = paused;
        _mode = mode;
        _seededAt = _clock.Now;
    }

    private bool Starting => _seededAt is { } at && _clock.Now - at < StartupSilence;

    public void OnProfile(string profileId, string name, string? reason)
    {
        if (_disposed)
        {
            return;
        }

        bool known = _profileId is not null;
        if (profileId == _profileId)
        {
            return;
        }

        _profileId = profileId;
        _profileChangedAt = _clock.Now;
        if (known && !Starting)
        {
            Notify(OsdText.Profile(name, reason));
        }
    }

    public void OnLayer(string? layer)
    {
        if (_disposed || (_layerKnown && layer == _layer))
        {
            return;
        }

        bool known = _layerKnown;
        _layer = layer;
        _layerKnown = true;
        bool withProfile = layer is null && _profileChangedAt is { } at && _clock.Now - at < LayerSuppression;
        if (known && !withProfile)
        {
            Notify(OsdText.Layer(layer));
        }
    }

    public void OnMapping(bool paused, string? reason)
    {
        if (_disposed || _paused == paused)
        {
            return;
        }

        bool known = _paused is not null;
        _paused = paused;
        if (known)
        {
            Notify(OsdText.Pause(paused, reason));
        }
    }

    public void OnEmulationMode(EmulationMode mode)
    {
        if (_disposed || _mode == mode)
        {
            return;
        }

        bool known = _mode is not null;
        _mode = mode;
        if (known && !Starting)
        {
            Notify(OsdText.Mode(mode));
        }
    }

    public void OnNotice(Notice notice)
    {
        if (!_disposed && notice.Kind == NoticeKinds.LowBattery)
        {
            Notify(OsdText.LowBattery(notice.Message));
        }
    }

    /// <summary>Applies new OSD settings to what is on screen; hides it when disabled.</summary>
    public void OnSettingsChanged()
    {
        if (_disposed)
        {
            return;
        }

        OsdSettings settings = _settings();
        if (settings == _applied)
        {
            return;
        }

        _applied = settings;
        if (!settings.Enabled)
        {
            Hide();
        }
        else if (_current is { } current)
        {
            _view.Show(current, settings);
            Restart(settings);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clock.Cancel();
        _current = null;
    }

    private void Notify(OsdNotification notification)
    {
        OsdSettings settings = _settings();
        _applied = settings;
        if (!settings.Enabled)
        {
            return;
        }

        _current = notification;
        _view.Show(notification, settings);
        Restart(settings);
    }

    private void Restart(OsdSettings settings) =>
        _clock.Schedule(TimeSpan.FromSeconds(Math.Clamp(settings.DurationSeconds, 1, 5)), Hide);

    private void Hide()
    {
        _clock.Cancel();
        if (_current is not null)
        {
            _current = null;
            _view.Hide();
        }
    }
}

/// <summary>The OSD card: dark graphite surface, one pixel border, left accent line, title and detail.</summary>
internal sealed class OsdOverlayWindow : OverlayWindow, IOsdView
{
    private const string FontFamily = "Segoe UI";

    private string _title = "";
    private string _detail = "";
    private int _dpi = OverlayLayout.BaseDpi;
    private int _radius;
    private Font? _titleFont;
    private Font? _detailFont;
    private int _fontKey;

    void IOsdView.Show(OsdNotification notification, OsdSettings settings)
    {
        MonitorArea area = ForegroundMonitor();
        _dpi = area.Dpi;
        Size card = OverlayLayout.OsdCardSize(settings.Size, _dpi);
        _radius = OverlayLayout.Scale(10, _dpi);
        EnsureFonts(settings.Size);

        int textWidth = card.Width - OverlayLayout.Scale(4 + 14, _dpi) - OverlayLayout.Scale(14, _dpi);
        _title = OverlayLayout.Ellipsize(notification.Title, textWidth, s => Measure(s, _titleFont!));
        _detail = OverlayLayout.Ellipsize(notification.Detail, textWidth, s => Measure(s, _detailFont!));
        ShowUnactivated(OverlayLayout.OsdBounds(area.WorkArea, settings.Corner, card, _dpi), OverlayLayout.Alpha(settings.Opacity), _radius);
        Invalidate();
    }

    void IOsdView.Hide() => HideOverlay();

    private void EnsureFonts(OsdSize size)
    {
        int points = OverlayLayout.OsdPoints(size);
        int key = points * 10000 + _dpi;
        if (_fontKey == key && _detailFont is not null)
        {
            return;
        }

        _titleFont?.Dispose();
        _detailFont?.Dispose();
        _titleFont = new Font(FontFamily, OverlayLayout.FontPixels(points - 2, _dpi), FontStyle.Regular, GraphicsUnit.Pixel);
        _detailFont = new Font(FontFamily, OverlayLayout.FontPixels(points, _dpi), FontStyle.Bold, GraphicsUnit.Pixel);
        _fontKey = key;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_titleFont is null || _detailFont is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        g.Clear(Surface);
        using (var border = new Pen(LineStrong))
        using (var path = RoundedRectangle(new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), _radius))
        {
            g.DrawPath(border, path);
        }

        int accentWidth = OverlayLayout.Scale(4, _dpi);
        using (var accent = new SolidBrush(Accent))
        {
            g.FillRectangle(accent, 0, 0, accentWidth, bounds.Height);
        }

        int left = accentWidth + OverlayLayout.Scale(14, _dpi);
        int width = bounds.Width - left - OverlayLayout.Scale(14, _dpi);
        if (_detail.Length == 0)
        {
            DrawLine(g, _title, _detailFont, Ink, new Rectangle(left, 0, width, bounds.Height));
            return;
        }

        int titleHeight = _titleFont.Height;
        int detailHeight = _detailFont.Height;
        int top = (bounds.Height - titleHeight - detailHeight) / 2;
        DrawLine(g, _title, _titleFont, Muted, new Rectangle(left, top, width, titleHeight));
        DrawLine(g, _detail, _detailFont, Ink, new Rectangle(left, top + titleHeight, width, detailHeight));
    }

    private static void DrawLine(Graphics g, string text, Font font, Color color, Rectangle area) =>
        TextRenderer.DrawText(g, text, font, area, color, LineFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _detailFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
