using System.Collections.Concurrent;
using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>
/// The one object the host creates. Picks the active profile, turns it into an
/// <see cref="OutputState"/> 30 times a second and sends it when it changes. Live overrides
/// from the UI are layered on top until cleared.
/// </summary>
public sealed class AppCore : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    private const string PreviewLeft = "preview.trigger.left";
    private const string PreviewRight = "preview.trigger.right";

    private readonly Lock _lock = new();
    // Serialises device writes without holding _lock, so input handling never waits on HID I/O.
    private readonly Lock _sendLock = new();
    private long _outputVersion;
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly IForegroundSource _foreground;
    private readonly bool _ownsForeground;
    private readonly Timer? _timer;
    private readonly ProfileSelector _selector = new();
    private readonly List<KeyValuePair<string, Func<OutputState, OutputState>>> _overrides = [];
    private readonly IdleMonitor _idle = new();
    private readonly BatteryMonitor _battery = new();
    private readonly RecentNames _recent = new();
    private readonly ConcurrentQueue<Notice> _outbox = new();

    private ProfileSelection _active;
    private TriggerEffect _leftEffect;
    private TriggerEffect _rightEffect;
    private StickProcessor _leftStick;
    private StickProcessor _rightStick;
    private OutputState? _lastOutput;
    private InputState? _latestInput;
    private GyroCalibrator? _calibrator;
    private TimeSpan _calibrationStarted;
    private Vector3 _gyroBias;
    private bool _activeChanged;
    private bool _tickFailed;
    private volatile bool _disposed;

    private readonly Lock _noticeLock = new();
    private readonly List<Notice> _undelivered = [];
    private Action<Notice>? _notice;

    public AppCore(string? dataDir = null, IForegroundSource? foreground = null)
        : this(dataDir, foreground, new ControllerService(), TimeProvider.System, runTimer: true)
    {
    }

    /// <param name="runTimer">False leaves ticking to the caller (tests call <see cref="Tick(TimeSpan)"/>).</param>
    internal AppCore(string? dataDir, IForegroundSource? foreground, ControllerService controllers, TimeProvider time, bool runTimer)
    {
        _time = time;
        _startTimestamp = time.GetTimestamp();
        Controllers = controllers;
        Profiles = new ProfileStore(dataDir);
        Settings = new SettingsStore(dataDir);

        foreach (string broken in Profiles.BrokenFiles.Concat(Settings.BrokenFiles))
        {
            Queue(new Notice(NoticeKinds.BrokenFile, $"Bozuk dosya atlandı: {Path.GetFileName(broken)}"));
        }

        if (foreground is null)
        {
            foreground = new WindowsForegroundSource();
            _ownsForeground = true;
        }

        _foreground = foreground;
        if (_foreground.Current is { } current)
        {
            _recent.Add(current);
        }

        _active = _selector.Select(_foreground.Current, Profiles.All, Settings.Current, null, TimeSpan.Zero);
        _leftStick = new StickProcessor(_active.Profile.Sticks.Left);
        _rightStick = new StickProcessor(_active.Profile.Sticks.Right);
        ApplyTriggers(_active.Profile);

        _foreground.Changed += OnForeground;
        Profiles.Changed += Refresh;
        Settings.Changed += Refresh;
        Controllers.Input += OnInput;
        Controllers.ConnectionChanged += OnConnection;

        if (runTimer)
        {
            _timer = new Timer(_ => TimerTick(), null, TimeSpan.Zero, TickInterval);
        }
    }

    public ControllerService Controllers { get; }

    public ProfileStore Profiles { get; }

    public SettingsStore Settings { get; }

    public IForegroundSource Foreground => _foreground;

    /// <summary>Last 10 distinct exe names seen in front, most recent first.</summary>
    public IReadOnlyList<string> RecentForeground => _recent.Items;

    public Profile ActiveProfile
    {
        get
        {
            lock (_lock)
            {
                return _active.Profile;
            }
        }
    }

    /// <summary>"manual", "game:&lt;exe&gt;", "controllerDefault" or "default".</summary>
    public string ActiveReason
    {
        get
        {
            lock (_lock)
            {
                return _active.Reason;
            }
        }
    }

    /// <summary>Configured from the active profile. Replaced (not mutated) when it changes.</summary>
    public StickProcessor LeftStick => _leftStick;

    public StickProcessor RightStick => _rightStick;

    /// <summary>Resting gyro drift in deg/s from the last successful calibration.</summary>
    public Vector3 GyroBias
    {
        get
        {
            lock (_lock)
            {
                return _gyroBias;
            }
        }
    }

    public bool GyroCalibrating
    {
        get
        {
            lock (_lock)
            {
                return _calibrator is not null;
            }
        }
    }

    public event Action? ActiveProfileChanged;

    /// <summary>Notices raised before the first handler subscribes are delivered to it on subscribe.</summary>
    public event Action<Notice>? Notice
    {
        add
        {
            Notice[] backlog;
            lock (_noticeLock)
            {
                _notice += value;
                backlog = [.. _undelivered];
                _undelivered.Clear();
            }

            foreach (Notice n in backlog)
            {
                value?.Invoke(n);
            }
        }
        remove
        {
            lock (_noticeLock)
            {
                _notice -= value;
            }
        }
    }

    /// <summary>Manual choice; stays until the foreground game changes.</summary>
    /// <exception cref="KeyNotFoundException">Unknown id.</exception>
    public void ActivateProfile(string id)
    {
        if (Profiles.Get(id) is null)
        {
            throw new KeyNotFoundException($"Profil bulunamadı: {id}");
        }

        lock (_lock)
        {
            _selector.Pin(id);
        }

        Refresh();
    }

    /// <summary>Tries a trigger effect over the profile's. Null goes back to the profile.</summary>
    /// <exception cref="ArgumentException">The config does not make a valid effect.</exception>
    public void PreviewTrigger(bool left, TriggerConfig? config)
    {
        string key = left ? PreviewLeft : PreviewRight;
        if (config is null)
        {
            SetOverride(key, null);
            return;
        }

        TriggerEffect effect = Triggers.ToEffect(config);
        SetOverride(key, o => left ? o with { LeftTrigger = effect } : o with { RightTrigger = effect });
    }

    /// <summary>
    /// Adds, replaces or (with null) removes a named layer applied on top of the profile output,
    /// in the order first added. Used for the live controls in the UI.
    /// </summary>
    public void SetOverride(string key, Func<OutputState, OutputState>? layer)
    {
        lock (_lock)
        {
            int i = _overrides.FindIndex(kv => kv.Key == key);
            if (layer is null)
            {
                if (i >= 0)
                {
                    _overrides.RemoveAt(i);
                }
            }
            else if (i >= 0)
            {
                _overrides[i] = new(key, layer);
            }
            else
            {
                _overrides.Add(new(key, layer));
            }
        }

        Refresh();
    }

    public void ClearOverrides()
    {
        lock (_lock)
        {
            _overrides.Clear();
        }

        Refresh();
    }

    /// <summary>Hold the controller still for 3 s. Progress and result arrive as notices.</summary>
    public void StartGyroCalibration()
    {
        lock (_lock)
        {
            _calibrationStarted = Now();
            _calibrator = new GyroCalibrator(progress: p =>
                _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, "Gyro kalibrasyonu sürüyor; kontrolcüyü oynatmayın.", p)));
        }

        _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, "Gyro kalibrasyonu başladı.", 0));
        Flush();
    }

    /// <summary>Gyro in deg/s: device calibration, then the measured drift removed.</summary>
    public Vector3 GyroDegPerSec(InputState s)
    {
        ImuCalibration cal = Controllers.Controller?.Details.Calibration ?? ImuCalibration.Identity;
        return GyroCalibrationResult.Apply(cal.GyroDegPerSec(s.GyroRaw), GyroBias);
    }

    /// <summary>Recomputes now instead of waiting for the next tick.</summary>
    public void Refresh()
    {
        if (!_disposed)
        {
            Tick(Now());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _foreground.Changed -= OnForeground;
        Profiles.Changed -= Refresh;
        Settings.Changed -= Refresh;
        Controllers.Input -= OnInput;
        Controllers.ConnectionChanged -= OnConnection;
        if (_ownsForeground && _foreground is IDisposable d)
        {
            d.Dispose();
        }

        Controllers.Dispose();
    }

    internal TimeSpan Now() => _time.GetElapsedTime(_startTimestamp);

    internal void Tick(TimeSpan now)
    {
        string? sleepMac = null;
        OutputState? toSend = null;
        long version = 0;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            AppSettings settings = Settings.Current;
            DualSenseController? controller = Controllers.Controller;
            string? mac = controller?.Details.MacAddress;

            ProfileSelection selection = _selector.Select(_foreground.Current, Profiles.All, settings, mac, now);
            if (!ReferenceEquals(selection.Profile, _active.Profile) || selection.Reason != _active.Reason)
            {
                if (!ReferenceEquals(selection.Profile, _active.Profile))
                {
                    _leftStick = new StickProcessor(selection.Profile.Sticks.Left);
                    _rightStick = new StickProcessor(selection.Profile.Sticks.Right);
                    ApplyTriggers(selection.Profile);
                }

                _active = selection;
                _activeChanged = true;
            }

            if (_calibrator is not null && now - _calibrationStarted > GyroCalibrator.DefaultDuration + TimeSpan.FromSeconds(1.5))
            {
                Finish(_calibrator.Fail("Kontrolcüden gyro verisi gelmiyor."));
            }

            if (mac is not null && _idle.ShouldSleep(now, settings.IdleSleepMinutes, controller?.Details.Link))
            {
                _idle.Reset(now);
                sleepMac = mac;
                _outbox.Enqueue(new Notice(NoticeKinds.IdleSleep,
                    $"Kontrolcü {settings.IdleSleepMinutes} dakikadır kullanılmadı, kapatılıyor."));
            }

            OutputState output = Build(now);
            if (!output.Equals(_lastOutput))
            {
                _lastOutput = output;
                toSend = output;
                version = ++_outputVersion;
            }
        }

        if (toSend is not null)
        {
            lock (_sendLock)
            {
                // A newer tick may have computed a later state already; never send an older one after it.
                if (version == Interlocked.Read(ref _outputVersion))
                {
                    Controllers.SetOutput(_ => toSend);
                }
            }
        }

        if (sleepMac is not null)
        {
            _ = Task.Run(() => BluetoothLink.Disconnect(sleepMac));
        }

        Flush();
    }

    internal void OnInput(InputState s)
    {
        TimeSpan now = Now();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _latestInput = s;
            _idle.Observe(s, now);

            if (_battery.Update(s.Battery, Settings.Current.LowBatteryWarnings) is int threshold)
            {
                _outbox.Enqueue(new Notice(NoticeKinds.LowBattery, $"Pil %{threshold} altına düştü (%{s.Battery.Percent})."));
            }

            if (_calibrator is not null)
            {
                ImuCalibration cal = Controllers.Controller?.Details.Calibration ?? ImuCalibration.Identity;
                if (_calibrator.Add(cal.GyroDegPerSec(s.GyroRaw), now) is { } result)
                {
                    Finish(result);
                }
            }
        }

        Flush();
    }

    private void Finish(GyroCalibrationResult result)
    {
        _calibrator = null;
        if (result.Success)
        {
            _gyroBias = result.Bias;
            _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, "Gyro kalibre edildi.", 1));
        }
        else
        {
            _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, result.Reason ?? "Gyro kalibrasyonu başarısız."));
        }
    }

    private OutputState Build(TimeSpan now)
    {
        Profile p = _active.Profile;
        (Rgb color, LedBrightness brightness) = LightbarEngine.Evaluate(
            p.Lightbar, now.TotalSeconds, _latestInput?.Battery.Percent, p.PlayerLeds.Number);

        OutputState o = OutputState.Default with
        {
            Lightbar = color,
            Brightness = brightness,
            PlayerLeds = PlayerLeds.Mask(p.PlayerLeds),
            MicLed = p.MicLed,
            LeftTrigger = _leftEffect,
            RightTrigger = _rightEffect,
            RumbleLeft = 0,
            RumbleRight = 0,
        };

        foreach ((string _, Func<OutputState, OutputState> layer) in _overrides.OrderBy(kv => kv.Key.StartsWith("preview.", StringComparison.Ordinal)))
        {
            o = layer(o);
        }

        return o;
    }

    private void ApplyTriggers(Profile p)
    {
        // Stored profiles are validated, so these only fall back for hand-edited files.
        Triggers.TryToEffect(p.Triggers.Left, out _leftEffect, out _);
        Triggers.TryToEffect(p.Triggers.Right, out _rightEffect, out _);
    }

    private void OnForeground(string exe)
    {
        _recent.Add(exe);
        Refresh();
    }

    private void OnConnection(DualSenseController? controller)
    {
        lock (_lock)
        {
            _idle.Reset(Now());
            _battery.Reset();
            _latestInput = null;
        }

        Refresh();
    }

    private void TimerTick()
    {
        try
        {
            Refresh();
        }
        catch (Exception e) when (!_tickFailed)
        {
            // Report once; the next tick tries again.
            _tickFailed = true;
            Queue(new Notice(NoticeKinds.Info, $"Çıktı motoru hatası: {e.Message}"));
        }
        catch (Exception)
        {
        }
    }

    private void Flush()
    {
        bool changed;
        lock (_lock)
        {
            changed = _activeChanged;
            _activeChanged = false;
        }

        if (changed)
        {
            ActiveProfileChanged?.Invoke();
        }

        while (_outbox.TryDequeue(out Notice? n))
        {
            Queue(n);
        }
    }

    private void Queue(Notice n)
    {
        Action<Notice>? handler;
        lock (_noticeLock)
        {
            handler = _notice;
            if (handler is null)
            {
                _undelivered.Add(n);
                return;
            }
        }

        handler(n);
    }
}
