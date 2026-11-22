using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tactile.Input;
using Tactile.Core;
using Tactile.Core.Logging;
using Tactile.Core.Updates;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;
using Tactile.Input.Windows;

namespace Tactile.Host;

/// <summary>
/// Owns the core, the window and the tray icon. Core events arrive on timer and reader
/// threads and are marshalled to the UI thread through the window's handle.
/// </summary>
internal sealed partial class TactileApp : ApplicationContext
{
    // ERROR_CANCELLED: the user said no at the UAC prompt.
    private const int ErrorCancelled = 1223;
    private const uint ModNoRepeat = 0x4000;
    private const uint ScanCodeToVirtualKey = 3;

    private readonly AppCore _core;
    private readonly UpdateService _updates;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private TimeSpan _nextAutomaticCheck = TimeSpan.FromSeconds(60);
    private readonly AppIcon _smallIcon = new(SystemInformation.SmallIconSize.Width);
    private readonly AppIcon _largeIcon = new(SystemInformation.IconSize.Width);
    private readonly MainWindow _window;
    private readonly Tray _tray;
    private readonly System.Windows.Forms.Timer _inputTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _emulationTimer = new() { Interval = 1000 };
    private readonly RegisteredWaitHandle _showWait;
    private readonly EventWaitHandle _restoreSignal;
    private readonly EventWaitHandle _restoredSignal;
    private readonly RegisteredWaitHandle _restoreWait;
    private volatile PrimarySample? _pendingInput;
    private sealed record PrimarySample(ControllerSlot Slot, InputState State);
    private Battery? _battery;
    private bool _toldAboutTray;
    private bool _exiting;
    private nint _hotkeyWindow;
    private IReadOnlyList<Key> _configuredHotkey = [];

    public TactileApp(bool startHidden, EventWaitHandle showSignal, AppPaths paths)
    {
        (DriverPlatform? drivers, IUsbIpServer? server) = StartEmulation(paths.DataDirectory);
        _core = new AppCore(paths.DataDirectory, platform: WindowsInputPlatform.Create(),
            drivers: drivers, server: server, portable: paths.Portable);
        _updates = new UpdateService(paths, typeof(TactileApp).Assembly.GetName().Version!);
        _window = new MainWindow(_core, _largeIcon.Icon, paths, _updates);
        // A handle now, so core events can be marshalled even while the window was never shown.
        _ = _window.Handle;
        _window.FormClosing += OnWindowClosing;
        _window.PauseHotkeyPressed += OnPauseHotkeyPressed;
        _window.HandleCreated += OnHotkeyHandleCreated;
        _window.HandleDestroyed += OnHotkeyHandleDestroyed;
        _window.FormClosed += (_, _) => Exit();

        // Exit later: the tray disposes itself, which must not happen inside its own menu click.
        _tray = new Tray(_smallIcon.Icon, ShowWindow, Activate, RestoreController, _core.ToggleUserPause, () => Ui(Exit));
        _tray.SetProfiles(_core.Profiles.All, _core.ActiveProfile.Id);
        UpdateTrayStatus();
        UpdatePauseHotkey();

        _core.Controllers.PrimaryInput += OnInput;
        _core.Controllers.ConnectionChanged += OnConnection;
        _core.Controllers.SlotConnected += OnSlotConnected;
        _core.Controllers.SlotDisconnected += OnSlotDisconnected;
        _core.ActiveProfileChanged += OnActiveProfile;
        _core.Profiles.Changed += OnProfiles;
        _core.Settings.Changed += OnSettings;
        _core.Notice += OnNotice;
        _core.MappingChanged += OnMappingChanged;
        _core.UiMappingPausedChanged += OnUiMappingPausedChanged;
        _core.ShowAppRequested += OnShowAppRequested;
        _window.Bridge.RestartElevatedRequested += RestartElevated;
        _window.Bridge.RestoreRequested += RestoreController;
        _window.Bridge.UpdateStarted += Exit;
        _updates.Changed += OnUpdateChanged;
        _updates.Notice += _window.Bridge.PostNotice;
        if (_core.Emulation is { } emulation)
        {
            emulation.Changed += OnEmulationChanged;
        }

        _inputTimer.Tick += (_, _) => FlushInput();
        _inputTimer.Start();
        _emulationTimer.Tick += (_, _) =>
        {
            CheckForUpdatesAutomatically();
            _window.Bridge.PostUpdateStatus();
            if (_window.OnScreen)
            {
                _window.Bridge.PostStatus();
                _window.Bridge.PostEmulation();
                _window.Bridge.PostIntegration();
            }
        };
        _emulationTimer.Start();

        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Ui(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        // A restore command started while we run: undo here, then acknowledge it.
        _restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.RestoreEventName);
        _restoredSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.RestoredEventName);
        _restoreWait = ThreadPool.RegisterWaitForSingleObject(_restoreSignal, (_, _) =>
        {
            RestoreNow();
            _restoredSignal.Set();
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        if (!paths.Portable)
        {
            _ = Task.Run(StartMenu.EnsureRestoreShortcut);
        }

        if (!startHidden)
        {
            _window.Show();
        }
    }

    /// <summary>Any failure (including the placeholder factories) leaves emulation off; the app still runs.</summary>
    private static (DriverPlatform?, IUsbIpServer?) StartEmulation(string dataDirectory)
    {
        DriverPlatform? drivers = null;
        try
        {
            drivers = DriverPlatform.CreateWindows(dataDirectory);
        }
        catch (Exception e)
        {
            Log.Warn("Driver layer could not start.", e);
            return (null, null);
        }

        try
        {
            return (drivers, UsbIpServerFactory.Start());
        }
        catch (Exception e)
        {
            Log.Warn("USB/IP server could not start.", e);
            return (drivers, null);
        }
    }

    private void OnEmulationChanged() => Ui(_window.Bridge.PostEmulation);

    /// <summary>Tray, settings page: same as <c>--restore</c>, in this process, off the UI thread.</summary>
    private void RestoreController()
    {
        if (_exiting)
        {
            return;
        }

        _ = Task.Run(RestoreNow);
    }

    private void RestoreNow()
    {
        (string En, string Tr) message;
        try
        {
            message = _core.RestoreEmulation()
                ? ("Controller restored. The virtual controller was removed and emulation was turned off.", "Kontrolcü geri getirildi: sanal kontrolcü kaldırıldı, gizleme kapatıldı, sanal kontrolcü ayarı kapandı.")
                : ("Emulation was turned off. No driver changes to undo.", "Sanal kontrolcü ayarı kapatıldı. Sürücü katmanı çalışmadığı için geri alınacak bir şey yoktu.");
        }
        catch (Exception e)
        {
            Log.Error("Could not restore the controller.", e);
            message = ($"Could not restore the controller: {Text.English(e)}", $"Kontrolcü geri getirilemedi: {e.Message}");
        }

        var notice = new Notice(NoticeKinds.Emulation, message.En, message.Tr);
        Log.Notice(notice);
        Ui(() =>
        {
            _window.Bridge.PostNotice(notice);
            _window.Bridge.PostEmulation();
            if (!_window.OnScreen)
            {
                _tray.Balloon(notice.Message);
            }
        });
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
        catch (KeyNotFoundException e)
        {
            Log.Warn("Tray profile was no longer available.", e);
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
            _tray.Balloon(Text.Pick("TriggerForge is running in the background", "TriggerForge arka planda çalışıyor"));
        }
    }

    private void OnInput(ControllerSlot slot, InputState state) => _pendingInput = new(slot, state);

    private void OnConnection(DualSenseController? c)
    {
        Ui(() =>
        {
            if (!ReferenceEquals(c, _core.Controllers.Controller))
            {
                return;
            }

            _battery = null;
            _window.Bridge.PostStatus();
            UpdateTrayStatus();
        });
    }

    private void OnSlotConnected(ControllerSlot slot)
    {
        if (slot.Index > 0)
        {
            Ui(_window.Bridge.PostStatus);
        }
    }

    private void OnSlotDisconnected(int index)
    {
        if (index > 0)
        {
            Ui(_window.Bridge.PostStatus);
        }
    }

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

    private void OnMappingChanged() => Ui(() =>
    {
        _window.Bridge.PostStatus();
        UpdateTrayStatus();
    });

    private void OnShowAppRequested() => Ui(ShowWindow);

    private void CheckForUpdatesAutomatically()
    {
        TimeSpan elapsed = _uptime.Elapsed;
        if (elapsed >= _nextAutomaticCheck && _core.Settings.Current.Updates.AutoCheck && !_updates.Busy)
        {
            _nextAutomaticCheck = elapsed + TimeSpan.FromHours(24);
            _ = _updates.CheckAsync();
        }
    }

    private void OnUpdateChanged() => _window.Bridge.PostUpdateStatus(force: true);

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
            Log.Info("Elevation was cancelled.", e);
            _window.Bridge.PostNotice(new Notice(NoticeKinds.Info, Text.Pick("Administrator permission was declined.", "Yönetici izni verilmedi.")));
            return;
        }
        catch (Win32Exception e)
        {
            Log.Error("Elevated restart failed.", e);
            _window.Bridge.PostNotice(new Notice(NoticeKinds.Info, Text.Pick("Could not restart TriggerForge as administrator.", "TriggerForge yönetici olarak yeniden başlatılamadı.")));
            return;
        }

        // The new copy waits for this process to end before it opens the controller.
        Exit();
    }

    private void OnSettings() => Ui(() =>
    {
        _window.Bridge.PostSettings();
        _tray.UpdateLanguage();
        UpdatePauseHotkey();
        UpdateTrayStatus();
        _window.Bridge.PostEmulation();
        _window.Bridge.PostHapticsIfChanged();
        if (_updates.Status.State != UpdateState.Downloading)
        {
            _window.Bridge.PostUpdateStatus();
        }
    });

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
        if (Interlocked.Exchange(ref _pendingInput, null) is { } sample && _core.Controllers.IsCurrent(sample.Slot))
        {
            InputState s = sample.State;
            if (_battery != s.Battery)
            {
                _battery = s.Battery;
                UpdateTrayStatus();
            }

            if (_window.OnScreen)
            {
                _window.Bridge.PostInput(sample.Slot, s);
            }
        }

        if (_window.OnScreen)
        {
            _window.Bridge.PostOutputIfChanged();
        }

        _window.Bridge.PostHapticsIfChanged();
    }

    private void UpdateTrayStatus()
    {
        DualSenseController? c = _core.Controllers.Controller;
        string? name = c is null ? null : $"{c.Details.Model} · {c.Details.Link}";
        _tray.SetStatus(name, _battery?.Percent, _battery is { Charge: not ChargeState.Discharging },
            _core.Mapping.Paused, _core.UserMappingPaused);
    }

    private void OnHotkeyHandleCreated(object? sender, EventArgs args) => UpdatePauseHotkey();

    private void OnPauseHotkeyPressed()
    {
        if (!_core.UiMappingPaused)
        {
            _core.ToggleUserPause();
        }
    }

    private void OnUiMappingPausedChanged()
    {
        if (_window.InvokeRequired)
        {
            Ui(UpdatePauseHotkey);
        }
        else
        {
            UpdatePauseHotkey();
        }
    }

    private void OnHotkeyHandleDestroyed(object? sender, EventArgs args)
    {
        UnregisterPauseHotkey();
        _configuredHotkey = [];
    }

    private void UpdatePauseHotkey()
    {
        IReadOnlyList<Key> keys = PauseHotkeyKeys(_core);
        if (_exiting || !_window.IsHandleCreated || keys.SequenceEqual(_configuredHotkey))
        {
            return;
        }

        _configuredHotkey = keys;
        UnregisterPauseHotkey();
        if (keys.Count == 0)
        {
            return;
        }

        nint window = _window.Handle;
        bool valid = PauseShortcut.TryGetHotkey(keys, out uint modifiers, out Key trigger);
        uint key = valid ? VirtualKey(trigger) : 0;
        if (key != 0 && RegisterHotKey(window, MainWindow.PauseHotkeyId, modifiers | ModNoRepeat, key))
        {
            _hotkeyWindow = window;
            return;
        }

        var notice = new Notice(NoticeKinds.Mapping,
            "The pause hotkey could not be registered. Use one key with optional modifiers and choose a shortcut that is not already in use.",
            "Duraklatma kısayolu kaydedilemedi. İsteğe bağlı değiştiricilerle tek tuş kullanın ve kullanılmayan bir kısayol seçin.");
        Log.Notice(notice);
        _window.Bridge.PostNotice(notice);
        if (!_window.OnScreen)
        {
            _tray.Balloon(notice.Message, ToolTipIcon.Warning);
        }
    }

    private void UnregisterPauseHotkey()
    {
        if (_hotkeyWindow != 0)
        {
            _ = UnregisterHotKey(_hotkeyWindow, MainWindow.PauseHotkeyId);
            _hotkeyWindow = 0;
        }
    }

    internal static IReadOnlyList<Key> PauseHotkeyKeys(AppCore core) =>
        core.UiMappingPaused ? [] : core.Settings.Current.PauseHotkey;

    internal static uint VirtualKey(Key key)
    {
        uint fixedKey = key switch
        {
            Key.Numpad0 => 0x60,
            Key.Numpad1 => 0x61,
            Key.Numpad2 => 0x62,
            Key.Numpad3 => 0x63,
            Key.Numpad4 => 0x64,
            Key.Numpad5 => 0x65,
            Key.Numpad6 => 0x66,
            Key.Numpad7 => 0x67,
            Key.Numpad8 => 0x68,
            Key.Numpad9 => 0x69,
            Key.NumpadDecimal => 0x6E,
            Key.Up => 0x26,
            Key.Down => 0x28,
            Key.Left => 0x25,
            Key.Right => 0x27,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Menu => 0x5D,
            Key.NumpadDivide => 0x6F,
            Key.NumpadEnter => 0x0D,
            _ => WindowsInputInjector.VirtualKey(key),
        };
        return fixedKey != 0 ? fixedKey : MapVirtualKey((uint)key, ScanCodeToVirtualKey);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);

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
        catch (InvalidOperationException e)
        {
            Log.Info("Window handle was unavailable for an update.", e);
        }
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        UnregisterPauseHotkey();
        _window.PauseHotkeyPressed -= OnPauseHotkeyPressed;
        _window.HandleCreated -= OnHotkeyHandleCreated;
        _window.HandleDestroyed -= OnHotkeyHandleDestroyed;
        _showWait.Unregister(null);
        _restoreWait.Unregister(null);
        _inputTimer.Stop();
        _emulationTimer.Stop();
        _window.Bridge.UpdateStarted -= Exit;
        _updates.Changed -= OnUpdateChanged;
        _updates.Notice -= _window.Bridge.PostNotice;
        _updates.Dispose();
        if (_core.Emulation is { } emulation)
        {
            emulation.Changed -= OnEmulationChanged;
        }

        _window.Bridge.RestoreRequested -= RestoreController;
        _core.Controllers.PrimaryInput -= OnInput;
        _core.Controllers.ConnectionChanged -= OnConnection;
        _core.Controllers.SlotConnected -= OnSlotConnected;
        _core.Controllers.SlotDisconnected -= OnSlotDisconnected;
        _core.ActiveProfileChanged -= OnActiveProfile;
        _core.Profiles.Changed -= OnProfiles;
        _core.Settings.Changed -= OnSettings;
        _core.Notice -= OnNotice;
        _core.MappingChanged -= OnMappingChanged;
        _core.UiMappingPausedChanged -= OnUiMappingPausedChanged;
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
            _emulationTimer.Dispose();
            _updates.Dispose();
            _restoreSignal.Dispose();
            _restoredSignal.Dispose();
            // No-op after Exit; covers the loop ending some other way (session end).
            _core.Dispose();
            if (!_window.IsDisposed)
            {
                _window.Dispose();
            }

            // The tray shows the small icon, so it goes first (a no-op after Exit).
            _tray.Dispose();
            _smallIcon.Dispose();
            _largeIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
