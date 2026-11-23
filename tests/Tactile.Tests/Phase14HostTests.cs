using System.Drawing;
using Tactile.Core;
using Tactile.Emulation;
using Tactile.Host;

namespace Tactile.Tests;

internal sealed class FakeOsdView : IOsdView
{
    public List<(OsdNotification Notification, OsdSettings Settings)> Shown { get; } = [];
    public int Hidden { get; private set; }

    public void Show(OsdNotification notification, OsdSettings settings) => Shown.Add((notification, settings));

    public void Hide() => Hidden++;

    public void Dispose()
    {
    }
}

internal sealed class FakeOsdClock : IOsdClock
{
    private TimeSpan _due;
    private Action? _callback;

    public TimeSpan Now { get; private set; }
    public int Schedules { get; private set; }
    public bool Pending => _callback is not null;

    public void Schedule(TimeSpan delay, Action callback)
    {
        Schedules++;
        _due = Now + delay;
        _callback = callback;
    }

    public void Cancel() => _callback = null;

    public void Advance(TimeSpan by)
    {
        Now += by;
        if (_callback is { } callback && Now >= _due)
        {
            _callback = null;
            callback();
        }
    }

    public void Dispose()
    {
    }
}

[Collection("Localization")]
public class Phase14HostTests : IDisposable
{
    private readonly Language _language = Text.Current;

    public Phase14HostTests() => Text.Current = Language.En;

    public void Dispose() => Text.Current = _language;

    private static OsdSettings On(int seconds = 2) => new() { Enabled = true, DurationSeconds = seconds };

    private sealed class Rig : IDisposable
    {
        public Rig(OsdSettings settings, bool settled = true)
        {
            Settings = settings;
            Coordinator = new OsdCoordinator(View, Clock, () => Settings);
            Coordinator.Seed("a", null, paused: false, EmulationMode.Off);
            if (settled)
            {
                Clock.Advance(OsdCoordinator.StartupSilence);
            }
        }

        public OsdSettings Settings { get; set; }
        public FakeOsdView View { get; } = new();
        public FakeOsdClock Clock { get; } = new();
        public OsdCoordinator Coordinator { get; }

        public OsdNotification Last => View.Shown[^1].Notification;

        public void Dispose() => Coordinator.Dispose();
    }

    [Fact]
    public void Overlay_window_uses_layered_transparent_noactivate_toolwindow_styles()
    {
        using var osd = new OsdOverlayWindow();
        using var keyboard = new OnScreenKeyboardWindow();
        const int flags = OverlayWindow.WsExLayered | OverlayWindow.WsExTransparent | OverlayWindow.WsExToolWindow
            | OverlayWindow.WsExNoActivate | OverlayWindow.WsExTopmost;

        Assert.Equal(flags, osd.ExStyle & flags);
        Assert.Equal(flags, keyboard.ExStyle & flags);
        Assert.True(osd.WithoutActivation);
        Assert.True(keyboard.WithoutActivation);
        Assert.False(osd.IsHandleCreated);
        Assert.False(osd.ShowInTaskbar);
        Assert.True(osd.TopMost);
    }

    [Fact]
    public void Mouse_activation_never_activates_and_clicks_pass_through()
    {
        Assert.True(OverlayWindow.TryHandleMessage(OverlayWindow.WmMouseActivate, out nint activate));
        Assert.Equal(OverlayWindow.MaNoActivate, (int)activate);
        Assert.True(OverlayWindow.TryHandleMessage(OverlayWindow.WmNcHitTest, out nint hit));
        Assert.Equal(OverlayWindow.HtTransparent, (int)hit);
        Assert.True(OverlayWindow.TryHandleMessage(OverlayWindow.WmDpiChanged, out nint dpi));
        Assert.Equal(0, (int)dpi);
        Assert.False(OverlayWindow.TryHandleMessage(0x000F, out _));
    }

    [Theory]
    [InlineData(OsdCorner.TopLeft, 24, 24)]
    [InlineData(OsdCorner.TopRight, 1920 - 24 - 300, 24)]
    [InlineData(OsdCorner.BottomLeft, 24, 1040 - 24 - 68)]
    [InlineData(OsdCorner.BottomRight, 1920 - 24 - 300, 1040 - 24 - 68)]
    public void Osd_is_placed_by_corner_with_a_24px_margin(OsdCorner corner, int x, int y)
    {
        var work = new Rectangle(0, 0, 1920, 1040);

        Rectangle bounds = OverlayLayout.OsdBounds(work, corner, OverlayLayout.OsdCardSize(OsdSize.Medium, 96), 96);

        Assert.Equal(new Rectangle(x, y, 300, 68), bounds);
    }

    [Fact]
    public void Osd_placement_follows_the_monitor_origin_and_scales_with_dpi()
    {
        var second = new Rectangle(-2560, 100, 2560, 1340);
        Size card = OverlayLayout.OsdCardSize(OsdSize.Large, 192);

        Rectangle bounds = OverlayLayout.OsdBounds(second, OsdCorner.TopLeft, card, 192);

        Assert.Equal(new Size(760, 168), card);
        Assert.Equal(new Point(-2560 + 48, 100 + 48), bounds.Location);
        Assert.Equal(new Size(240, 56), OverlayLayout.OsdCardSize(OsdSize.Small, 96));
        Assert.Equal(new Size(300, 68), OverlayLayout.OsdCardSize(OsdSize.Medium, 96));
        Assert.Equal(new Size(450, 102), OverlayLayout.OsdCardSize(OsdSize.Medium, 144));
    }

    [Fact]
    public void Osd_stays_inside_a_work_area_smaller_than_the_card()
    {
        var tiny = new Rectangle(10, 10, 200, 40);

        Rectangle bounds = OverlayLayout.OsdBounds(tiny, OsdCorner.BottomRight, new Size(300, 68), 96);

        Assert.Equal(new Point(10, 10), bounds.Location);
    }

    [Theory]
    [InlineData(30, 77)]
    [InlineData(85, 217)]
    [InlineData(100, 255)]
    [InlineData(0, 77)]
    [InlineData(500, 255)]
    public void Opacity_percent_maps_to_an_explicit_alpha(int percent, int alpha) =>
        Assert.Equal(alpha, OverlayLayout.Alpha(percent));

    [Fact]
    public void Font_pixels_scale_with_dpi_like_the_windows()
    {
        Assert.Equal(16, OverlayLayout.FontPixels(12, 96));
        Assert.Equal(32, OverlayLayout.FontPixels(12, 192));
        Assert.Equal(1, OverlayLayout.FontPixels(0, 96));
        Assert.Equal(10, OverlayLayout.OsdPoints(OsdSize.Small));
        Assert.Equal(12, OverlayLayout.OsdPoints(OsdSize.Medium));
        Assert.Equal(14, OverlayLayout.OsdPoints(OsdSize.Large));
    }

    [Fact]
    public void Keyboard_is_centred_48px_above_the_bottom_and_fits_every_layout()
    {
        var work = new Rectangle(0, 0, 1920, 1040);
        foreach (Language language in new[] { Language.En, Language.Tr })
        {
            IReadOnlyList<IReadOnlyList<string>> grid = OnScreenKeyboard.GridFor(language, uppercase: false);
            KeyboardMetrics m = OverlayLayout.Keyboard(work, grid.Count, grid.Max(r => r.Count), 96);
            Rectangle bounds = OverlayLayout.KeyboardBounds(work, m.Size, 96);

            Assert.Equal(52, m.Cell);
            Assert.Equal(12 * 52 + 11 * 6 + 32, m.Size.Width);
            Assert.Equal(4 * 52 + 3 * 6 + 32 + 36, m.Size.Height);
            Assert.Equal(1040 - 48, bounds.Bottom);
            Assert.Equal((1920 - m.Size.Width) / 2, bounds.X);
        }
    }

    [Fact]
    public void Keyboard_scales_with_dpi_and_shrinks_cells_on_a_narrow_work_area()
    {
        var work = new Rectangle(0, 0, 640, 600);

        KeyboardMetrics wide = OverlayLayout.Keyboard(new Rectangle(0, 0, 3840, 2100), 4, 12, 192);
        KeyboardMetrics narrow = OverlayLayout.Keyboard(work, 4, 12, 96);
        Rectangle bounds = OverlayLayout.KeyboardBounds(work, narrow.Size, 96);

        Assert.Equal(104, wide.Cell);
        Assert.True(narrow.Cell < 52);
        Assert.True(narrow.Size.Width <= work.Width - 2 * 24);
        Assert.True(work.Contains(bounds));
    }

    [Fact]
    public void Keyboard_cells_stay_inside_and_short_rows_are_centred()
    {
        KeyboardMetrics m = OverlayLayout.Keyboard(new Rectangle(0, 0, 1920, 1040), 4, 12, 96);

        Rectangle first = OverlayLayout.KeyboardCell(m, 12, 12, 0, 0);
        Rectangle last = OverlayLayout.KeyboardCell(m, 12, 12, 3, 11);
        Rectangle shortRow = OverlayLayout.KeyboardCell(m, 12, 10, 3, 0);

        Assert.Equal(new Point(m.Pad, m.Pad), first.Location);
        Assert.Equal(m.Size.Width - m.Pad, last.Right);
        Assert.Equal(m.Pad + (m.Cell + m.Gap), shortRow.X);
        Assert.True(last.Bottom <= m.Size.Height - m.Hint);
    }

    [Theory]
    [InlineData("Short", 100, "Short")]
    [InlineData("A very long profile name that cannot fit", 100, "A very lo…")]
    [InlineData("Anything", 5, "…")]
    [InlineData("", 0, "")]
    public void Long_text_is_ellipsized_to_the_width(string text, int width, string expected) =>
        Assert.Equal(expected, OverlayLayout.Ellipsize(text, width, s => s.Length * 10));

    [Fact]
    public void Ellipsis_does_not_split_a_surrogate_pair()
    {
        string result = OverlayLayout.Ellipsize("ab😀cdef", 40, s => s.Length * 10);

        Assert.Equal("ab…", result);
    }

    [Fact]
    public void Osd_stays_silent_when_disabled_and_formats_all_five_events_when_enabled()
    {
        using var off = new Rig(new OsdSettings());
        off.Coordinator.OnProfile("b", "Racing", "manual");
        off.Coordinator.OnLayer("Shift");
        off.Coordinator.OnMapping(true, "shortcut");
        off.Coordinator.OnNotice(new Notice(NoticeKinds.LowBattery, "20%"));
        off.Coordinator.OnEmulationMode(EmulationMode.Xbox360);
        Assert.Empty(off.View.Shown);
        Assert.Equal(0, off.Clock.Schedules);

        using var rig = new Rig(On());
        rig.Coordinator.OnProfile("b", "Racing", "game:forza.exe");
        Assert.Equal(new OsdNotification(OsdEventKind.Profile, "Profile", "Racing (forza.exe)"), rig.Last);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Coordinator.OnLayer("Shift");
        Assert.Equal(new OsdNotification(OsdEventKind.Layer, "Layer", "Shift"), rig.Last);
        rig.Coordinator.OnLayer(null);
        Assert.Equal(new OsdNotification(OsdEventKind.Layer, "Layer", "Base"), rig.Last);

        rig.Coordinator.OnMapping(true, "shortcut");
        Assert.Equal(new OsdNotification(OsdEventKind.Pause, "Mapping paused", "Hotkey"), rig.Last);
        rig.Coordinator.OnMapping(false, null);
        Assert.Equal(new OsdNotification(OsdEventKind.Pause, "Mapping resumed", ""), rig.Last);

        rig.Coordinator.OnNotice(new Notice(NoticeKinds.LowBattery, "20%"));
        Assert.Equal(new OsdNotification(OsdEventKind.LowBattery, "Low battery", "20%"), rig.Last);
        rig.Coordinator.OnNotice(new Notice(NoticeKinds.Info, "ignored"));
        Assert.Equal(OsdEventKind.LowBattery, rig.Last.Kind);

        rig.Coordinator.OnEmulationMode(EmulationMode.DualShock4);
        Assert.Equal(new OsdNotification(OsdEventKind.VirtualController, "Virtual controller", "DualShock 4"), rig.Last);

        Assert.True(rig.Coordinator.Visible);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(rig.Coordinator.Visible);
        Assert.Equal(1, rig.View.Hidden);
    }

    [Fact]
    public void Osd_texts_follow_the_language()
    {
        Text.Current = Language.Tr;
        using var rig = new Rig(On());

        rig.Coordinator.OnLayer(null);
        rig.Coordinator.OnLayer("Shift");
        rig.Coordinator.OnLayer(null);
        Assert.Equal(new OsdNotification(OsdEventKind.Layer, "Katman", "Temel"), rig.Last);
        rig.Coordinator.OnMapping(true, "fullscreen");
        Assert.Equal(new OsdNotification(OsdEventKind.Pause, "Eşleme duraklatıldı", "Tam ekran"), rig.Last);
        rig.Coordinator.OnEmulationMode(EmulationMode.Off);
        rig.Coordinator.OnEmulationMode(EmulationMode.DualSense);
        rig.Coordinator.OnEmulationMode(EmulationMode.Off);
        Assert.Equal(new OsdNotification(OsdEventKind.VirtualController, "Sanal kontrolcü", "Kapalı"), rig.Last);
    }

    [Fact]
    public void Osd_records_initial_values_silently_and_ignores_repeats()
    {
        using var rig = new Rig(On());

        rig.Coordinator.OnProfile("a", "Same", "manual");
        rig.Coordinator.OnLayer(null);
        rig.Coordinator.OnMapping(false, null);
        rig.Coordinator.OnEmulationMode(EmulationMode.Off);
        Assert.Empty(rig.View.Shown);

        using var unseeded = new OsdCoordinator(new FakeOsdView(), new FakeOsdClock(), () => On());
        var view = new FakeOsdView();
        using var fresh = new OsdCoordinator(view, new FakeOsdClock(), () => On());
        fresh.OnProfile("x", "First", null);
        fresh.OnLayer("Shift");
        fresh.OnMapping(true, null);
        fresh.OnEmulationMode(EmulationMode.Auto);
        Assert.Empty(view.Shown);
        fresh.OnProfile("y", "Second", null);
        Assert.Single(view.Shown);
    }

    [Fact]
    public void Osd_layer_reset_that_arrives_with_a_profile_change_is_suppressed()
    {
        using var rig = new Rig(On());
        rig.Coordinator.OnLayer("Shift");
        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        rig.View.Shown.Clear();

        rig.Coordinator.OnProfile("b", "Racing", "manual");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(50));
        rig.Coordinator.OnLayer(null);
        Assert.Equal([OsdEventKind.Profile], rig.View.Shown.Select(s => s.Notification.Kind));

        // Later layer changes speak again, and the reset was still recorded.
        rig.Clock.Advance(OsdCoordinator.LayerSuppression);
        rig.Coordinator.OnLayer("Shift");
        Assert.Equal(OsdEventKind.Layer, rig.Last.Kind);
    }

    [Fact]
    public void Osd_layer_opened_right_after_a_profile_change_still_speaks()
    {
        using var rig = new Rig(On());
        rig.Coordinator.OnProfile("b", "Racing", "manual");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(50));

        rig.Coordinator.OnLayer("Shift");

        Assert.Equal(new OsdNotification(OsdEventKind.Layer, "Layer", "Shift"), rig.Last);
    }

    [Fact]
    public void Osd_is_silent_about_profile_and_mode_changes_while_starting_but_not_about_the_battery()
    {
        using var rig = new Rig(On(), settled: false);

        rig.Coordinator.OnProfile("b", "Racing", "manual");
        rig.Coordinator.OnEmulationMode(EmulationMode.Xbox360);
        Assert.Empty(rig.View.Shown);

        rig.Coordinator.OnNotice(new Notice(NoticeKinds.LowBattery, "20%"));
        Assert.Equal(OsdEventKind.LowBattery, rig.Last.Kind);

        rig.Clock.Advance(OsdCoordinator.StartupSilence);
        rig.View.Shown.Clear();
        rig.Coordinator.OnProfile("b", "Racing", "manual");
        Assert.Empty(rig.View.Shown);
        rig.Coordinator.OnProfile("c", "Other", "manual");
        rig.Coordinator.OnEmulationMode(EmulationMode.DualSense);
        Assert.Equal([OsdEventKind.Profile, OsdEventKind.VirtualController], rig.View.Shown.Select(s => s.Notification.Kind));
    }

    [Fact]
    public void Osd_new_notification_replaces_the_text_and_restarts_the_timer()
    {
        using var rig = new Rig(On(2));
        rig.Coordinator.OnMapping(true, "shortcut");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1500));

        rig.Coordinator.OnMapping(false, null);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.True(rig.Coordinator.Visible);
        Assert.Equal("Mapping resumed", rig.Last.Title);
        Assert.Equal(0, rig.View.Hidden);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(rig.Coordinator.Visible);
        Assert.Equal(1, rig.View.Hidden);
    }

    [Fact]
    public void Osd_duration_comes_from_the_settings_and_is_clamped()
    {
        using var rig = new Rig(On(5));
        rig.Coordinator.OnMapping(true, null);
        rig.Clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.True(rig.Coordinator.Visible);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.False(rig.Coordinator.Visible);

        rig.Settings = On(0);
        rig.Coordinator.OnMapping(false, null);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.True(rig.Coordinator.Visible);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.False(rig.Coordinator.Visible);
    }

    [Fact]
    public void Osd_settings_apply_live_and_disabling_hides_it()
    {
        using var rig = new Rig(On());
        rig.Coordinator.OnMapping(true, null);

        rig.Settings = On() with { Corner = OsdCorner.BottomLeft, Opacity = 50 };
        rig.Coordinator.OnSettingsChanged();
        Assert.Equal(2, rig.View.Shown.Count);
        Assert.Equal(OsdCorner.BottomLeft, rig.View.Shown[^1].Settings.Corner);
        Assert.Equal(50, rig.View.Shown[^1].Settings.Opacity);

        int schedules = rig.Clock.Schedules;
        rig.Coordinator.OnSettingsChanged();
        Assert.Equal(schedules, rig.Clock.Schedules);

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Coordinator.OnSettingsChanged();
        Assert.False(rig.Coordinator.Visible);
        Assert.False(rig.Clock.Pending);
        Assert.Equal(1, rig.View.Hidden);

        rig.Coordinator.OnMapping(false, null);
        Assert.Equal(2, rig.View.Shown.Count);
    }

    [Fact]
    public void Osd_ignores_events_after_it_is_disposed()
    {
        var rig = new Rig(On());
        rig.Coordinator.OnMapping(true, null);

        rig.Coordinator.Dispose();
        rig.Coordinator.OnMapping(false, null);
        rig.Coordinator.OnProfile("z", "Late", null);
        rig.Coordinator.OnSettingsChanged();

        Assert.Single(rig.View.Shown);
        Assert.False(rig.Clock.Pending);
    }

    [Fact]
    public void Primary_emulation_mode_prefers_the_first_slot()
    {
        var status = new EmulationStatus(true, true, EmulationMode.Xbox360, true, false, [], LatencySummary.Empty, null)
        {
            Slots = [new EmulationSlotStatus(1, EmulationMode.DualSense, true, false, null)],
        };
        Assert.Equal(EmulationMode.Xbox360, TactileApp.PrimaryMode(status));

        EmulationStatus withPrimary = status with
        {
            Slots = [new EmulationSlotStatus(1, EmulationMode.DualSense, true, false, null), new EmulationSlotStatus(0, EmulationMode.DualShock4, true, false, null)],
        };
        Assert.Equal(EmulationMode.DualShock4, TactileApp.PrimaryMode(withPrimary));
    }

    [Fact]
    public void Keyboard_renders_are_coalesced_to_one_pending_post()
    {
        var posted = new List<Action>();
        var drawn = new List<string>();
        var queue = new CoalescedRender<string>(posted.Add, drawn.Add);

        queue.Submit("a");
        queue.Submit("b");
        queue.Submit("c");
        Assert.Single(posted);
        Assert.Empty(drawn);

        posted[0]();
        Assert.Equal(["c"], drawn);

        queue.Submit("d");
        Assert.Equal(2, posted.Count);
        posted[1]();
        Assert.Equal(["c", "d"], drawn);
    }

    [Fact]
    public void Keyboard_render_queue_recovers_when_a_post_fails()
    {
        bool fail = true;
        var posted = new List<Action>();
        var queue = new CoalescedRender<string>(a =>
        {
            if (fail)
            {
                throw new InvalidOperationException();
            }

            posted.Add(a);
        }, _ => { });

        Assert.Throws<InvalidOperationException>(() => queue.Submit("a"));
        fail = false;
        queue.Submit("b");

        Assert.Single(posted);
    }
}
