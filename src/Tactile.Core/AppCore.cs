using System.Collections.Concurrent;
using Tactile.Audio;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;
using Tactile.Input;

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
    private const string MicOverride = "mapping.mic";

    private readonly Lock _lock = new();
    // Serialises device writes without holding _lock, so input handling never waits on HID I/O.
    private readonly Lock _sendLock = new();
    private readonly Lock _hapticsLock = new();
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
    private readonly HapticEngine _haptics;
    private byte? _speakerVolume;

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

    private readonly InputPlatform _platform;
    private readonly Mapper _mapper;
    // Serialises mapper configuration and status, apart from _lock so input never waits on it.
    private readonly Lock _mappingLock = new();
    private readonly HashSet<string> _elevatedNoticed = new(StringComparer.OrdinalIgnoreCase);
    private MappingStatus _mappingStatus = MappingStatus.Off;
    private string? _blockedBy;
    // Foreground exe the probe last answered for while mapping was active; null = not checked / inactive.
    private string? _elevationCheckedFor;
    private volatile bool _mappingPaused;

    private readonly EmulationService? _emulation;
    private (DualSenseController? Source, RealController? Value) _real;

    /// <param name="platform">Keyboard, mouse, microphone and launcher; null = <see cref="InputPlatform.None"/> (no output).</param>
    /// <param name="drivers">Driver layer for the virtual controller; null = emulation unavailable.</param>
    /// <param name="server">USB/IP server; null with drivers set = drivers shown, no virtual pad.</param>
    public AppCore(string? dataDir = null, IForegroundSource? foreground = null, InputPlatform? platform = null,
        DriverPlatform? drivers = null, IUsbIpServer? server = null, IAudioSystem? audio = null)
        : this(dataDir, foreground, new ControllerService(autoConnect: true, drivers is null ? null : drivers.Tree.IsVirtual),
            TimeProvider.System, runTimer: true, platform, drivers, server, audio: audio)
    {
    }

    /// <param name="runTimer">
    /// False leaves ticking to the caller (tests call <see cref="Tick(TimeSpan)"/>); the mapping
    /// worker and macros then also run on the calling thread, driven by the same clock.
    /// </param>
    internal AppCore(string? dataDir, IForegroundSource? foreground, ControllerService controllers, TimeProvider time, bool runTimer,
        InputPlatform? platform = null, DriverPlatform? drivers = null, IUsbIpServer? server = null, Func<bool>? steamRunning = null,
        IAudioSystem? audio = null)
    {
        _time = time;
        _platform = platform ?? InputPlatform.None;
        _mapper = new Mapper(_platform, new MappingHost(this), Now, threaded: runTimer);
        _startTimestamp = time.GetTimestamp();
        Controllers = controllers;
        Settings = new SettingsStore(dataDir);
        _haptics = new HapticEngine(audio ?? AudioSystemFactory.Create(),
            source => Controllers.SetHapticSource(source, (byte)Settings.Current.Emulation.HapticBuffer),
            () => Controllers.Output, _outbox.Enqueue, time);
        Controllers.SpeakerSource = _haptics.ReadSpeakerFrame;
        Profiles = new ProfileStore(dataDir);

        foreach (string broken in Profiles.BrokenFiles.Concat(Settings.BrokenFiles))
        {
            Queue(new Notice(NoticeKinds.BrokenFile, $"Bozuk dosya atlandı: {Path.GetFileName(broken)}"));
        }

        if (drivers is not null)
        {
            // Restores a crashed session's leftovers before the controller is used.
            _emulation = new EmulationService(drivers, server, new EmulationHost(this), Now, threaded: runTimer, steamRunning);
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
        _mapper.Configure(_active.Profile);

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

    public HapticStatus Haptics => _haptics.Status;

    public IReadOnlyList<AudioApp> ListAudioApps() => _haptics.ListApps();

    public ProfileStore Profiles { get; }

    public SettingsStore Settings { get; }

    public IForegroundSource Foreground => _foreground;

    /// <summary>Folder for settings, profiles and the recovery journal.</summary>
    public static string DataDirectory(string? dataDir = null) => JsonFile.ResolveDataDir(dataDir);

    /// <summary>Null when the driver layer could not start.</summary>
    public EmulationService? Emulation => _emulation;

    /// <summary>
    /// Stops emulation for good: the master switch goes off, our pad is removed and everything
    /// in the recovery journal is undone. False when the driver layer is not available.
    /// </summary>
    public bool RestoreEmulation()
    {
        AppSettings s = Settings.Current;
        if (s.Emulation.Enabled)
        {
            Settings.Save(s with { Emulation = s.Emulation with { Enabled = false } });
        }

        if (_emulation is null)
        {
            return false;
        }

        _emulation.RestoreAll();
        return true;
    }

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

    /// <summary>While true the mapper releases everything and ignores input (UI key capture, macro recording).</summary>
    public bool MappingPaused
    {
        get => _mappingPaused;
        set
        {
            _mappingPaused = value;
            UpdateMapping();
        }
    }

    public MappingStatus Mapping
    {
        get
        {
            lock (_mappingLock)
            {
                return _mappingStatus;
            }
        }
    }

    /// <summary>Raised when <see cref="Mapping"/> changes (active, paused, blockedBy).</summary>
    public event Action? MappingChanged;

    /// <summary>A <c>special: showApp</c> action asked for the main window.</summary>
    public event Action? ShowAppRequested;

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
            SetLayerLocked(key, layer);
        }

        Refresh();
    }

    public void ClearOverrides()
    {
        lock (_lock)
        {
            _overrides.Clear();
            _speakerVolume = null;
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

        // First, while output still works: the game layers clear and the journal is closed.
        _emulation?.Dispose();
        _disposed = true;
        _timer?.Dispose();
        _foreground.Changed -= OnForeground;
        Profiles.Changed -= Refresh;
        Settings.Changed -= Refresh;
        Controllers.Input -= OnInput;
        Controllers.ConnectionChanged -= OnConnection;
        _mapper.Dispose();
        if (_ownsForeground && _foreground is IDisposable d)
        {
            d.Dispose();
        }

        lock (_hapticsLock)
        {
            _haptics.Dispose();
        }

        Controllers.Dispose();
    }

    internal TimeSpan Now() => _time.GetElapsedTime(_startTimestamp);

    internal void Tick(TimeSpan now)
    {
        string? sleepMac = null;
        OutputState? toSend = null;
        long version = 0;
        Profile activeProfile;
        AppSettings activeSettings;
        DualSenseController? activeController;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            AppSettings settings = Settings.Current;
            DualSenseController? controller = Controllers.Controller;
            string? mac = controller?.Details.MacAddress;
            activeSettings = settings;
            activeController = controller;

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

            activeProfile = _active.Profile;
            HapticsConfig haptics = activeProfile.Haptics;
            bool gameSpeaker = haptics.GameAudio is GameAudioMode.Both or GameAudioMode.Speaker &&
                _emulation?.VirtualAudio?.SpeakerActive == true;
            byte? speakerVolume = controller is { Connected: true, Details.Link: Link.Bluetooth } &&
                (haptics.Speaker != AudioHapticSource.Off || gameSpeaker)
                ? (byte)Math.Round(haptics.SpeakerVolume * 100)
                : null;
            if (_speakerVolume != speakerVolume)
            {
                _speakerVolume = speakerVolume;
                SetLayerLocked("speaker", speakerVolume is byte volume ? o => o with { SpeakerVolume = volume } : null);
            }

            OutputState output = Build(now);
            if (!output.Equals(_lastOutput))
            {
                _lastOutput = output;
                toSend = output;
                version = ++_outputVersion;
            }
        }

        Send(toSend, version);
        lock (_hapticsLock)
        {
            if (!_disposed)
            {
                Link? link = Controllers.Controller is { Connected: true } connected ? connected.Details.Link : null;
                _haptics.Configure(ActiveProfile.Haptics, link, _emulation?.VirtualAudio);
            }
        }

        _emulation?.Update(activeProfile, activeSettings, RealOf(activeController));

        if (sleepMac is not null)
        {
            _ = Task.Run(() => BluetoothLink.Disconnect(sleepMac));
        }

        UpdateMapping();
        _mapper.Tick(now);
        Flush();
    }

    /// <summary>Writes a computed state unless a newer one was computed meanwhile. True when the controller write went out.</summary>
    private bool Send(OutputState? toSend, long version)
    {
        if (toSend is null)
        {
            return false;
        }

        lock (_sendLock)
        {
            // A newer tick may have computed a later state already; never send an older one after it.
            return version == Interlocked.Read(ref _outputVersion) && Controllers.SetOutputAndSend(toSend);
        }
    }

    /// <summary>
    /// <see cref="SetOverride"/> without the rest of the tick: rebuilds the output and sends it
    /// right away. Used for game feedback, which can arrive a few hundred times a second.
    /// </summary>
    internal bool SetOutputLayerNow(string key, Func<OutputState, OutputState>? layer)
    {
        OutputState? toSend = null;
        long version = 0;
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            SetLayerLocked(key, layer);
            OutputState output = Build(Now());
            if (!output.Equals(_lastOutput))
            {
                _lastOutput = output;
                toSend = output;
                version = ++_outputVersion;
            }
        }

        return Send(toSend, version);
    }

    private void SetLayerLocked(string key, Func<OutputState, OutputState>? layer)
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

    private RealController? RealOf(DualSenseController? c)
    {
        if (c is null)
        {
            return null;
        }

        // One record per controller, so the emulation sees "unchanged" between ticks.
        (DualSenseController? source, RealController? value) = _real;
        if (ReferenceEquals(source, c))
        {
            return value;
        }

        UsbFeatureReports f = c.Features;
        DualSenseIdentity? identity = f is { Calibration: { } cal, Pairing: { } pair, Firmware: { } fw } ? new DualSenseIdentity(cal, pair, fw) : null;
        var real = new RealController(c.DevicePath, c.Details.Link, identity);
        _real = (c, real);
        return real;
    }

    internal void OnInput(InputState s)
    {
        TimeSpan now = Now();
        bool firstInput;
        Vector3 gyro;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            firstInput = _latestInput is null;
            _latestInput = s;
            ImuCalibration imu = Controllers.Controller?.Details.Calibration ?? ImuCalibration.Identity;
            gyro = GyroCalibrationResult.Apply(imu.GyroDegPerSec(s.GyroRaw), _gyroBias);
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

        _haptics.Observe(s);
        _emulation?.OnInput(s);

        if (firstInput)
        {
            // Input proves a connection; mapping may become active.
            UpdateMapping();
        }

        _mapper.OnInput(s, gyro, now);
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
        // The tick re-checks elevation for the new foreground app.
        Refresh();
    }

    /// <summary>
    /// Asks the probe once per (foreground app, mapping active) change, not only on foreground
    /// changes: mapping often turns on after the game is already in front (auto-profile delay).
    /// </summary>
    private void CheckElevation(bool active)
    {
        string? exe = _foreground.Current;
        string? key = active ? exe : null;
        lock (_mappingLock)
        {
            if (string.Equals(key, _elevationCheckedFor, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _elevationCheckedFor = key;
        }

        bool blocks = false;
        if (key is not null)
        {
            try
            {
                blocks = _platform.Elevation.ForegroundBlocksInput();
            }
            catch (Exception)
            {
                // Probe errors count as "not blocked".
            }
        }

        bool first;
        lock (_mappingLock)
        {
            _blockedBy = blocks ? exe : null;
            first = blocks && _elevatedNoticed.Add(exe!);
        }

        if (first)
        {
            Queue(new Notice(NoticeKinds.Elevated,
                $"{exe} yönetici olarak çalışıyor; Windows bu pencereye tuş ve fare girdisi gönderilmesini engelliyor. Tactile'ı yönetici olarak yeniden başlatın."));
        }
    }

    /// <summary>Pushes the active profile and state to the mapper; raises <see cref="MappingChanged"/> when the status moves.</summary>
    private void UpdateMapping()
    {
        if (_disposed)
        {
            return;
        }

        bool active;
        bool paused;
        lock (_mappingLock)
        {
            Profile profile;
            bool connected;
            lock (_lock)
            {
                profile = _active.Profile;
                connected = Controllers.Controller is not null || _latestInput is not null;
            }

            paused = _mappingPaused;
            active = profile.Mapping.Enabled && !paused && connected;
            _mapper.Configure(profile);
            _mapper.SetActive(active);
        }

        // The probe is a few system calls; keep it outside the lock.
        CheckElevation(active);

        bool changed;
        lock (_mappingLock)
        {
            var status = new MappingStatus(active, paused, active ? _blockedBy : null);
            changed = status != _mappingStatus;
            _mappingStatus = status;
        }

        if (changed)
        {
            MappingChanged?.Invoke();
        }
    }

    private void SwitchProfile(string target)
    {
        IReadOnlyList<Profile> all = Profiles.All;
        string id = target;
        if (target is "next" or "previous" && all.Count > 0)
        {
            string current = ActiveProfile.Id;
            int i = Math.Max(0, all.ToList().FindIndex(p => p.Id == current));
            int step = target == "next" ? 1 : -1;
            id = all[(i + step + all.Count) % all.Count].Id;
        }

        if (Profiles.Get(id) is null)
        {
            Queue(new Notice(NoticeKinds.Mapping, "Eşlemedeki profil bulunamadı."));
            return;
        }

        ActivateProfile(id);
    }

    private sealed class EmulationHost(AppCore core) : IEmulationHost
    {
        public bool SetOutputLayer(string key, Func<OutputState, OutputState>? layer) => core.SetOutputLayerNow(key, layer);

        public void Report(Notice notice) => core.Queue(notice);
    }

    private sealed class MappingHost(AppCore core) : IMappingHost
    {
        public void SwitchProfile(string target) => core.SwitchProfile(target);

        // Unmuted hands the LED back to the profile's own micLed setting.
        public void MicMuteChanged(bool muted) =>
            core.SetOverride(MicOverride, muted ? o => o with { MicLed = MicLed.On } : null);

        public void ShowApp() => core.ShowAppRequested?.Invoke();

        public void Report(string message) => core.Queue(new Notice(NoticeKinds.Mapping, message));
    }

    internal void OnConnection(DualSenseController? controller)
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
            // Starting a WASAPI capture can take seconds; keep it off the output tick.
            // PollAudio skips itself while a previous poll is still running.
            ThreadPool.QueueUserWorkItem(static engine =>
            {
                try
                {
                    engine.PollAudio();
                }
                catch (Exception)
                {
                    // A failed poll is retried on the next tick.
                }
            }, _haptics, preferLocal: false);
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
