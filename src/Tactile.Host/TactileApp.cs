using System.ComponentModel;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Input.Windows;

namespace Tactile.Host;

/// <summary>
/// Owns the core, the window and the tray icon. Core events arrive on timer and reader
/// threads and are marshalled to the UI thread through the window's handle.
/// </summary>
internal sealed class TactileApp : ApplicationContext
{
    // ERROR_CANCELLED: the user said no at the UAC prompt.
    private const int ErrorCancelled = 1223;

    private readonly AppCore _core;
    private readonly Glyph _smallGlyph = new(SystemInformation.SmallIconSize.Width);
    private readonly Glyph _largeGlyph = new(SystemInformation.IconSize.Width);
    private readonly MainWindow _window;
    private readonly Tray _tray;
    private readonly System.Windows.Forms.Timer _inputTimer = new() { Interval = 33 };
    private readonly RegisteredWaitHandle _showWait;
    private volatile InputState? _pendingInput;
    private Battery? _battery;
    private bool _toldAboutTray;
    private bool _exiting;

    public TactileApp(bool startHidden, EventWaitHandle showSignal)
    {
        _core = new AppCore(platform: WindowsInputPlatform.Create());
        _window = new MainWindow(_core, _largeGlyph.Icon);
        // A handle now, so core events can be marshalled even while the window was never shown.
        _ = _window.Handle;
        _window.FormClosing += OnWindowClosing;
        _window.FormClosed += (_, _) => Exit();

        // Exit later: the tray disposes itself, which must not happen inside its own menu click.
        _tray = new Tray(_smallGlyph.Icon, ShowWindow, Activate, () => Ui(Exit));
        _tray.SetProfiles(_core.Profiles.All, _core.ActiveProfile.Id);
        UpdateTrayStatus();

        _core.Controllers.Input += OnInput;
        _core.Controllers.ConnectionChanged += OnConnection;
        _core.ActiveProfileChanged += OnActiveProfile;
        _core.Profiles.Changed += OnProfiles;
        _core.Settings.Changed += OnSettings;
        _core.Notice += OnNotice;
        _core.MappingChanged += OnMappingChanged;
        _core.ShowAppRequested += OnShowAppRequested;
        _window.Bridge.RestartElevatedRequested += RestartElevated;

        _inputTimer.Tick += (_, _) => FlushInput();
        _inputTimer.Start();

        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Ui(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!startHidden)
        {
            _window.Show();
        }
    }

    private void ShowWindow()
    {
        if (!_exiting)
        {
            _window.ShowAndActivate();
        }
    }

    private void Activate(string id)
    {
        try
        {
            _core.ActivateProfile(id);
        }
        catch (KeyNotFoundException)
        {
            _tray.SetProfiles(_core.Profiles.All, _core.ActiveProfile.Id);
        }
    }

    private void OnWindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != CloseReason.UserClosing || !_core.Settings.Current.CloseToTray)
        {
            return;
        }

        e.Cancel = true;
        _window.Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray.Balloon("Tactile arka planda çalışıyor");
        }
    }

    private void OnInput(InputState s) => _pendingInput = s;

    private void OnConnection(DualSenseController? c) => Ui(() =>
    {
        _battery = null;
        _window.Bridge.PostStatus();
        UpdateTrayStatus();
    });

    private void OnActiveProfile() => Ui(() =>
    {
        _window.Bridge.PostStatus();
        _window.Bridge.PostProfiles();
        _tray.SetProfiles(_core.Profiles.All, _core.ActiveProfile.Id);
    });

    private void OnProfiles() => Ui(() =>
    {
        _window.Bridge.PostProfiles();
        _tray.SetProfiles(_core.Profiles.All, _core.ActiveProfile.Id);
    });

    private void OnMappingChanged() => Ui(_window.Bridge.PostStatus);

    private void OnShowAppRequested() => Ui(ShowWindow);

    private void RestartElevated()
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            Program.StartElevatedCopy();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            _window.Bridge.PostNotice(new Notice(NoticeKinds.Info, "Yönetici izni verilmedi."));
            return;
        }
        catch (Win32Exception)
        {
            _window.Bridge.PostNotice(new Notice(NoticeKinds.Info, "Tactile yönetici olarak yeniden başlatılamadı."));
            return;
        }

        // The new copy waits for this process to end before it opens the controller.
        Exit();
    }

    private void OnSettings() => Ui(_window.Bridge.PostSettings);

    private void OnNotice(Notice n) => Ui(() =>
    {
        _window.Bridge.PostNotice(n);
        if (!_window.OnScreen && n.Kind is NoticeKinds.LowBattery or NoticeKinds.IdleSleep)
        {
            _tray.Balloon(n.Message, n.Kind == NoticeKinds.LowBattery ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
    });

    private void FlushInput()
    {
        InputState? s = Interlocked.Exchange(ref _pendingInput, null);
        if (s is not null)
        {
            if (_battery != s.Battery)
            {
                _battery = s.Battery;
                UpdateTrayStatus();
            }

            if (_window.OnScreen)
            {
                _window.Bridge.PostInput(s);
            }
        }

        if (_window.OnScreen)
        {
            _window.Bridge.PostOutputIfChanged();
        }
    }

    private void UpdateTrayStatus()
    {
        DualSenseController? c = _core.Controllers.Controller;
        string? name = c is null ? null : $"{c.Details.Model} · {c.Details.Link}";
        _tray.SetStatus(name, _battery?.Percent, _battery is { Charge: not ChargeState.Discharging });
    }

    /// <summary>Runs on the UI thread later; dropped once exiting.</summary>
    private void Ui(Action action)
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            _window.BeginInvoke(() =>
            {
                if (!_exiting)
                {
                    action();
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Handle gone during shutdown.
        }
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _showWait.Unregister(null);
        _inputTimer.Stop();
        _core.Controllers.Input -= OnInput;
        _core.Controllers.ConnectionChanged -= OnConnection;
        _core.ActiveProfileChanged -= OnActiveProfile;
        _core.Profiles.Changed -= OnProfiles;
        _core.Settings.Changed -= OnSettings;
        _core.Notice -= OnNotice;
        _core.MappingChanged -= OnMappingChanged;
        _core.ShowAppRequested -= OnShowAppRequested;
        _window.Bridge.RestartElevatedRequested -= RestartElevated;
        _tray.Dispose();

        // Puts the controller back to neutral.
        _core.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inputTimer.Dispose();
            // No-op after Exit; covers the loop ending some other way (session end).
            _core.Dispose();
            if (!_window.IsDisposed)
            {
                _window.Dispose();
            }

            _smallGlyph.Dispose();
            _largeGlyph.Dispose();
        }

        base.Dispose(disposing);
    }
}
