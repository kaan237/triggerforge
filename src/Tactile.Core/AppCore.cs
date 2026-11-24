using System.Collections.Concurrent;
using Tactile.Audio;
using Tactile.Core.Integration;
using Tactile.Core.Logging;
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
    private readonly Lock _primaryInputLock = new();
    private ControllerSlot? _inputSlot;
    private string? _gyroDevice;
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
    private readonly OnScreenKeyboard _osk;
    private readonly RadialMenuController _radial = new();
    // Buttons whose radial menu was cancelled while held; withheld from mapping and the virtual pad until let go.
    private Buttons _radialHoldMask;
    // Buttons held when the keyboard closed; withheld from mapping and the virtual pad until let go.
    private Buttons _oskHoldMask;
    private readonly OrderedEvents<OnScreenKeyboardState> _oskEvents = new();
    // Set by a mapped action while the mapper runs; the input pass that ran it consumes it right after.
    private int _oskToggleRequested;
    private string? _layerName;
    // Serialises mapper configuration and status, apart from _lock so input never waits on it.
    private readonly Lock _mappingLock = new();
    private readonly HashSet<string> _elevatedNoticed = new(StringComparer.OrdinalIgnoreCase);
    private MappingStatus _mappingStatus = MappingStatus.Off;
    private string? _blockedBy;
    // Foreground exe the probe last answered for while mapping was active; null = not checked / inactive.
    private string? _elevationCheckedFor;
    private volatile bool _uiPaused;
    private int _userPaused;
    private volatile bool _fullscreenPaused;
    private readonly PauseShortcut _pauseShortcut = new();
    private TimeSpan? _confirmUntil;

    private readonly EmulationService? _emulation;
    private readonly BluetoothMicBridge _micBridge;
    private readonly UdpApiServer _udpApi;
    private readonly TelemetryListener _telemetry;
    private readonly DsuServer _dsu;
    private int _emulationMode = -1;
    private volatile SecondaryControllers? _secondary;
    private (DualSenseController? Source, RealController? Value) _real;

    /// <param name="platform">Keyboard, mouse, microphone and launcher; null = <see cref="InputPlatform.None"/> (no output).</param>
    /// <param name="drivers">Driver layer for the virtual controller; null = emulation unavailable.</param>
    /// <param name="server">USB/IP server; null with drivers set = drivers shown, no virtual pad.</param>
    public AppCore(string? dataDir = null, IForegroundSource? foreground = null, InputPlatform? platform = null,
        DriverPlatform? drivers = null, IUsbIpServer? server = null, IAudioSystem? audio = null, bool portable = false)
        : this(dataDir, foreground, new ControllerService(autoConnect: false, drivers is null ? null : drivers.Tree.IsVirtual),
            TimeProvider.System, runTimer: true, platform, drivers, server, audio: audio, audioDefaults: new AudioDefaults(),
            portable: portable, scanControllers: true)
    {
    }

    /// <param name="runTimer">
    /// False leaves ticking to the caller (tests call <see cref="Tick(TimeSpan)"/>); the mapping
    /// worker and macros then also run on the calling thread, driven by the same clock.
    /// </param>
    internal AppCore(string? dataDir, IForegroundSource? foreground, ControllerService controllers, TimeProvider time, bool runTimer,
        InputPlatform? platform = null, DriverPlatform? drivers = null, IUsbIpServer? server = null, Func<bool>? steamRunning = null,
        IAudioSystem? audio = null, IAudioDefaults? audioDefaults = null, bool portable = false, bool scanControllers = false,
        Func<IVirtualMic?>? virtualMic = null, Func<IMicDecoder>? micDecoder = null)
    {
        _time = time;
        _platform = platform ?? InputPlatform.None;
        _mapper = new Mapper(_platform, new MappingHost(this), Now, threaded: runTimer, _radial);
        _osk = new OnScreenKeyboard(_platform.Injector);
        _oskEvents.Handler = state => OnScreenKeyboardChanged?.Invoke(state);
        _osk.Changed += _oskEvents.Enqueue;
        _radial.Changed += state => RadialMenuChanged?.Invoke(state);
        _startTimestamp = time.GetTimestamp();
        Controllers = controllers;
        Settings = new SettingsStore(dataDir, portable);
        Text.Current = Settings.Current.Language;
        _haptics = new HapticEngine(audio ?? AudioSystemFactory.Create(),
            source =>
            {
                EmulationSettings settings = Settings.Current.Emulation;
                return Controllers.SetHapticSource(source, (byte)settings.HapticBuffer, settings.SpeakerPacket);
            },
            () => Controllers.Output, _outbox.Enqueue, time);
        Controllers.SpeakerSource = _haptics.ReadSpeakerFrame;
        Profiles = new ProfileStore(dataDir, () => Settings.Current.BackupCount,
            () => DateOnly.FromDateTime(_time.GetLocalNow().DateTime), _outbox.Enqueue);

        foreach (string broken in Profiles.BrokenFiles.Concat(Settings.BrokenFiles))
        {
            Queue(new Notice(NoticeKinds.BrokenFile, $"Skipped damaged file: {Path.GetFileName(broken)}", $"Bozuk dosya atlandı: {Path.GetFileName(broken)}"));
        }

        if (drivers is not null)
        {
            // Restores a crashed session's leftovers before the controller is used.
            _emulation = new EmulationService(drivers, server, new EmulationHost(this), Now, threaded: runTimer, steamRunning,
                audioDefaults: audioDefaults);
            EmulationService emulation = _emulation;
            Controllers.VirtualPadMayBeActive = () => emulation.MayHavePad;
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

        _udpApi = new UdpApiServer(Now, (index, layer) =>
        {
            if (index == 0)
            {
                SetOutputLayerNow(IntegrationLayers.Udp, layer);
            }
        }, SetControllerProfile, GetUdpControllerStatus, _outbox.Enqueue, getSlots: () => Controllers.Slots);
        _telemetry = new TelemetryListener(Now, () => ActiveProfile.Telemetry,
            layer => SetOutputLayerNow(IntegrationLayers.Telemetry, layer), _outbox.Enqueue);
        _dsu = new DsuServer(Now, _outbox.Enqueue);
        _foreground.Changed += OnForeground;
        Profiles.Changed += Refresh;
        Settings.Changed += OnSettingsChanged;
        Controllers.PrimaryInput += OnPrimaryInput;
        Controllers.IdentifiedSlotInput += OnSecondaryInput;
        Controllers.SlotDisconnected += _dsu.Disconnect;
        Controllers.ConnectionChanged += OnConnection;
        ConfigureControllers();
        ConfigureIntegration();
        _micBridge = new BluetoothMicBridge(Controllers, () => Settings.Current.Emulation, virtualMic ?? (() => _emulation?.VirtualMic),
            SetOutputLayerNow, _outbox.Enqueue, () => _emulation?.NotifyMicChanged(), micDecoder, threaded: runTimer);
        _emulation?.MicStatusSource = () => _micBridge.Status;
        // The virtual pad appearing or going away is an input of the bridge as well.
        _emulation?.PadChanged += _micBridge.Poke;

        if (runTimer)
        {
            _timer = new Timer(_ => TimerTick(), null, TimeSpan.Zero, TickInterval);
        }

        if (scanControllers)
        {
            Controllers.StartScanning();
        }
    }

    public ControllerService Controllers { get; }

    public SecondaryControllers? SecondaryControllers => _secondary;

    public Profile ProfileForController(ControllerSlot slot) =>
        _secondary?.GetProfile(slot) ?? ActiveProfile;

    public void SetPrimaryController(string mac)
    {
        if (Validation.NormalizeMac(mac) is not { } selected)
        {
            throw new ArgumentException(Text.Pick("Invalid controller MAC address.", "Kontrolcü MAC adresi geçersiz."));
        }

        Settings.Save(Settings.Current with { PrimaryController = selected });
        // The saved MAC may already match when the controller first appeared in a secondary slot.
        Controllers.SelectPrimary(selected);
    }

    public void SetSecondaryProfile(int index, string id)
    {
        if (_secondary is not { } secondary)
        {
            throw new ArgumentException(Text.Pick("Secondary controller is not connected.", "İkincil kontrolcü bağlı değil."));
        }

        secondary.SetProfile(index, id);
    }

    public bool TrySetSecondaryProfile(int index, string mac, string id) =>
        _secondary?.SetProfile(index, id, mac) == true;

    private void SetControllerProfile(int index, string id)
    {
        if (index == 0)
        {
            ActivateProfile(id);
        }
        else
        {
            SetSecondaryProfile(index, id);
        }
    }

    public HapticStatus Haptics => _haptics.Status;

    public IReadOnlyList<AudioApp> ListAudioApps() => _haptics.ListApps();

    public ProfileStore Profiles { get; }

    public SettingsStore Settings { get; }

    public IntegrationStatus Integration => new(_udpApi.Status, _telemetry.Status, _dsu.Status,
        _telemetry.F1Status, _telemetry.AcStatus, _telemetry.BeamNgStatus, _telemetry.ActiveSource);

    public void ReleaseIntegration() => _udpApi.Release();

    public IForegroundSource Foreground => _foreground;

    /// <summary>Folder for settings, profiles and the recovery journal.</summary>
    public static string DataDirectory(string? dataDir = null) => JsonFile.ResolveDataDir(dataDir);

    /// <summary>Null when the driver layer could not start.</summary>
    public EmulationService? Emulation => _emulation;

    internal EmulationMicStatus MicBridgeStatus => _micBridge.Status;

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
    public event Action? UiMappingPausedChanged;

    /// <summary>While true the mapper releases everything and ignores input (UI key capture, macro recording).</summary>
    public bool UiMappingPaused
    {
        get => _uiPaused;
        set
        {
            if (_uiPaused == value)
            {
                return;
            }

            _uiPaused = value;
            UiMappingPausedChanged?.Invoke();
            UpdateMapping();
        }
    }

    public bool MappingPaused
    {
        get => UiMappingPaused;
        set => UiMappingPaused = value;
    }

    public bool UserMappingPaused => Volatile.Read(ref _userPaused) != 0;
    public bool FullscreenPaused => _fullscreenPaused;

    public void StartTestTurn(int counts, int delayMs)
    {
        lock (_mappingLock)
        {
            if (GetPauseReason() is null)
            {
                _mapper.StartTestTurn(counts, delayMs, Now());
            }
        }
    }

    public void ToggleUserPause()
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref _userPaused);
        } while (Interlocked.CompareExchange(ref _userPaused, 1 - previous, previous) != previous);

        UpdateMapping(notify: true);
        PulseConfirm(Now());
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

    /// <summary>
    /// The primary controller's topmost active shift layer name, or null when none. Raised on a
    /// background thread.
    /// </summary>
    public event Action<string?>? ActiveLayerChanged;

    /// <summary>The on-screen keyboard was shown, hidden or moved. Raised on the input thread; marshal before drawing.</summary>
    public event Action<OnScreenKeyboardState>? OnScreenKeyboardChanged;

    /// <summary>A radial menu was shown, hidden or its selection moved. Raised outside the core's locks; marshal before drawing.</summary>
    public event Action<RadialMenuState>? RadialMenuChanged;

    public bool RadialMenuOpen => _radial.IsOpen;

    public RadialMenuState RadialMenuSnapshot => _radial.State;

    /// <summary>Closes an open radial menu without firing its slice; the button that opened it stays withheld until let go.</summary>
    public void CancelRadialMenu()
    {
        _radial.Cancel();
        DrainKeyboardEvents();
    }

    public bool OnScreenKeyboardOpen => _osk.IsOpen;

    public OnScreenKeyboardState OnScreenKeyboardSnapshot => _osk.State;

    /// <summary>Opens the on-screen keyboard, or closes it when open. Needs a controller input and a profile with mapping on.</summary>
    public void ToggleOnScreenKeyboard()
    {
        lock (_primaryInputLock)
        {
            if (_disposed)
            {
                return;
            }

            InputState? current;
            lock (_lock)
            {
                current = _latestInput;
            }

            if (_osk.IsOpen)
            {
                CloseKeyboardLocked(current?.Buttons ?? Buttons.None);
            }
            else if (current is not null && CanOpenKeyboard)
            {
                OpenKeyboardLocked(current, sendNeutral: true);
            }
        }

        DrainKeyboardEvents();
    }

    public void CloseOnScreenKeyboard()
    {
        lock (_primaryInputLock)
        {
            if (_osk.IsOpen)
            {
                InputState? current;
                lock (_lock)
                {
                    current = _latestInput;
                }

                CloseKeyboardLocked(current?.Buttons ?? Buttons.None);
            }
        }

        DrainKeyboardEvents();
    }

    // Subscribers run outside the input lock and see states in the order they happened.
    private void DrainKeyboardEvents()
    {
        if (!_primaryInputLock.IsHeldByCurrentThread)
        {
            // The radial first: opening the keyboard cancels an open menu, and that cancel happened before the keyboard state.
            _radial.Drain();
            _oskEvents.Drain();
        }
    }

    private void OpenKeyboardLocked(InputState current, bool sendNeutral)
    {
        _oskHoldMask = Buttons.None;
        _mapper.ReleaseAll();
        _osk.Open(Settings.Current.Language, current);
        if (sendNeutral)
        {
            // Nothing held on the controller may stay held on the virtual pad or the motion server.
            if (Controllers.PrimarySlot is { } slot && Controllers.IsCurrent(slot))
            {
                _dsu.PublishInput(0, Neutral(current), default, default, slot.Controller.Details.Link, slot.Mac, _dsu.Generation(0));
            }

            _emulation?.OnInput(Neutral(current), gyroDegPerSec: default, now: Now(), mappingActive: _mapper.Active);
        }
    }

    private void CloseKeyboardLocked(Buttons held)
    {
        _osk.Close();
        _oskHoldMask = held;
        if (held != Buttons.None)
        {
            _mapper.SuppressButtons(held);
        }
    }

    // What mapping, the virtual pad and the motion server see while the keyboard owns the controller.
    private static InputState Neutral(InputState s) => s with
    {
        Buttons = Buttons.None,
        AccelRaw = default,
        Left = new Stick(128, 128),
        Right = new Stick(128, 128),
        L2 = 0,
        R2 = 0,
        GyroRaw = default,
        Touch1 = default,
        Touch2 = default,
    };

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

    public int RestoreProfileBackup(string id) => Profiles.RestoreBackup(id);

    /// <summary>Manual choice; stays until the foreground game changes.</summary>
    /// <exception cref="KeyNotFoundException">Unknown id.</exception>
    public void ActivateProfile(string id)
    {
        if (Profiles.Get(id) is null)
        {
            throw new LocalizedMessage($"Profile not found: {id}", $"Profil bulunamadı: {id}").CreateException(message => new KeyNotFoundException(message));
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
    /// Adds, replaces or (with null) removes a named layer applied on top of the profile output.
    /// Layers with the same priority are applied in the order first added.
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
            _overrides.RemoveAll(kv => kv.Key is not (IntegrationLayers.Udp or IntegrationLayers.Telemetry or IntegrationLayers.MicLed));
            _speakerVolume = null;
        }

        Refresh();
    }

    /// <summary>Hold the controller still for 3 s. Progress and result arrive as notices.</summary>
    public void StartGyroCalibration()
    {
        lock (_primaryInputLock)
        {
            SynchronizePrimary(Controllers.PrimarySlot);
            lock (_lock)
            {
                _calibrationStarted = Now();
                _calibrator = new GyroCalibrator(progress: p =>
                    _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, Text.Pick("Calibrating gyro. Keep the controller still.", "Gyro kalibrasyonu sürüyor; kontrolcüyü oynatmayın."), p)));
            }
        }

        DrainKeyboardEvents();

        _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, Text.Pick("Gyro calibration started.", "Gyro kalibrasyonu başladı."), 0));
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

        // Stop receiving integration commands while output still works.
        Settings.Changed -= OnSettingsChanged;
        // Closes the microphone stream while output still works.
        _emulation?.PadChanged -= _micBridge.Poke;
        _micBridge.Dispose();
        _udpApi.Dispose();
        _telemetry.Dispose();
        _dsu.Dispose();
        // The game layers clear and the journal is closed before output stops.
        _emulation?.Dispose();
        _disposed = true;
        _timer?.Dispose();
        _secondary?.Dispose();
        _foreground.Changed -= OnForeground;
        Profiles.Changed -= Refresh;
        Controllers.PrimaryInput -= OnPrimaryInput;
        Controllers.IdentifiedSlotInput -= OnSecondaryInput;
        Controllers.SlotDisconnected -= _dsu.Disconnect;
        Controllers.ConnectionChanged -= OnConnection;
        _mapper.Dispose();
        lock (_primaryInputLock)
        {
            _radial.Reset();
            _radialHoldMask = Buttons.None;
            _oskHoldMask = Buttons.None;
            Volatile.Write(ref _oskToggleRequested, 0);
            _osk.Close();
        }

        DrainKeyboardEvents();
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
        bool profileChanged = false;
        AppSettings activeSettings;
        ControllerSlot? activeSlot;
        string? settledExe;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            AppSettings settings = Settings.Current;
            activeSlot = Controllers.PrimarySlot;
            DualSenseController? controller = activeSlot?.Controller;
            string? mac = controller?.Details.MacAddress;
            activeSettings = settings;

            IReadOnlyList<Profile> profiles = Profiles.All;
            ProfileSelection selection = _selector.Select(_foreground.Current, profiles, settings, mac, now);
            settledExe = _selector.SettledForeground;
            if (!ReferenceEquals(selection.Profile, _active.Profile) || selection.Reason != _active.Reason)
            {
                if (!ReferenceEquals(selection.Profile, _active.Profile))
                {
                    profileChanged = selection.Profile.Id != _active.Profile.Id;
                    _leftStick = new StickProcessor(selection.Profile.Sticks.Left);
                    _rightStick = new StickProcessor(selection.Profile.Sticks.Right);
                    ApplyTriggers(selection.Profile);
                }

                _active = selection;
                _activeChanged = true;
            }

            _fullscreenPaused = settings.PauseOnFullscreen &&
                _foreground.GetFullscreenWindow() is (true, var exe) &&
                (!settings.AutoProfile || ProfileSelector.MatchGame(exe, profiles) is null);

            if (_confirmUntil <= now)
            {
                _confirmUntil = null;
                SetLayerLocked("confirm", null);
            }

            if (_calibrator is not null && now - _calibrationStarted > GyroCalibrator.DefaultDuration + TimeSpan.FromSeconds(1.5))
            {
                Finish(_calibrator.Fail(Text.Pick("No gyro data from the controller.", "Kontrolcüden gyro verisi gelmiyor.")));
            }

            if (mac is not null && _idle.ShouldSleep(now, settings.IdleSleepMinutes, controller?.Details.Link))
            {
                _idle.Reset(now);
                sleepMac = mac;
                _outbox.Enqueue(new Notice(NoticeKinds.IdleSleep,
                    Text.Pick($"The controller is turning off after {settings.IdleSleepMinutes} minutes of inactivity.", $"Kontrolcü {settings.IdleSleepMinutes} dakikadır kullanılmadı, kapatılıyor.")));
            }

            activeProfile = _active.Profile;
            HapticsConfig haptics = activeProfile.Haptics;
            bool gameSpeaker = haptics.GameAudio is GameAudioMode.Both or GameAudioMode.Speaker &&
                _emulation?.VirtualAudio?.SpeakerActive == true;
            byte? speakerVolume = controller is { Connected: true, Details.Link: Link.Bluetooth, Details.Model: not ControllerModel.DualShock4 } &&
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

        UpdateMapping();
        Send(toSend, version, activeSlot);
        _secondary?.Update(now, activeProfile, activeSettings);
        lock (_hapticsLock)
        {
            if (!_disposed)
            {
                var current = Controllers.Controller;
                Link? link = current is { Connected: true } connected ? connected.Details.Link : null;
                bool supported = current?.Details.Model != ControllerModel.DualShock4;
                _haptics.Configure(ActiveProfile.Haptics, link, _emulation?.VirtualAudio, supported);
            }
        }

        if (profileChanged)
        {
            PulseConfirm(now);
        }

        if (ReferenceEquals(activeSlot, Controllers.PrimarySlot) && _emulation is { } emulation)
        {
            emulation.Update(activeProfile, activeSettings, RealOf(activeSlot?.Controller), settledExe,
                ProfileSelector.MatchGame(settledExe, Profiles.All) is not null || IsFullscreenGame(settledExe));
            // The first tick establishes the baseline; later mode changes release held inputs.
            int mode = (int)emulation.ResolvedMode;
            int previousMode = Interlocked.Exchange(ref _emulationMode, mode);
            if (previousMode >= 0 && previousMode != mode)
            {
                // Keys still held on the controller stay suppressed instead of counting as new presses.
                _mapper.ReleaseAll();
            }
        }

        if (sleepMac is not null)
        {
            _ = Task.Run(() =>
            {
                // The microphone stream is closed while the link still exists.
                _micBridge?.Release(sleepMac);
                BluetoothLink.Disconnect(sleepMac);
            });
        }

        bool radialWasOpen = _radial.IsOpen;
        _mapper.Tick(now);
        if (!radialWasOpen && _radial.IsOpen)
        {
            NeutralizeRadialOpenedByTick();
        }

        ApplyKeyboardRequest();
        DrainKeyboardEvents();
        _micBridge?.Pump();
        Flush();
    }

    // A chord window that ran out in Tick opened a menu outside any input frame: take the menu stick from the last
    // report, and let the virtual pad and the motion server see it neutral at once (no new report needed).
    // Runs after Mapper.Tick returned, so no mapper lock is held when the input lock is taken.
    private void NeutralizeRadialOpenedByTick()
    {
        lock (_primaryInputLock)
        {
            if (_disposed || !_radial.IsOpen || _osk.IsOpen)
            {
                return;
            }

            InputState? current;
            lock (_lock)
            {
                current = _latestInput;
            }

            if (current is null)
            {
                return;
            }

            InputState input = ApplyRadial(current, current);
            if (Controllers.PrimarySlot is { } slot && Controllers.IsCurrent(slot))
            {
                _dsu.PublishInput(0, input, default, default, slot.Controller.Details.Link, slot.Mac, _dsu.Generation(0));
            }

            _emulation?.OnInput(input, gyroDegPerSec: default, now: Now(), mappingActive: _mapper.Active);
        }
    }

    // A mapped timer action (hold, double tap, chord) asked outside any input frame.
    private void ApplyKeyboardRequest()
    {
        if (Volatile.Read(ref _oskToggleRequested) == 0)
        {
            return;
        }

        lock (_primaryInputLock)
        {
            if (_disposed || Interlocked.Exchange(ref _oskToggleRequested, 0) == 0)
            {
                return;
            }

            InputState? current;
            lock (_lock)
            {
                current = _latestInput;
            }

            if (current is not null)
            {
                if (_osk.IsOpen)
                {
                    CloseKeyboardLocked(current.Buttons);
                }
                else if (CanOpenKeyboard)
                {
                    OpenKeyboardLocked(current, sendNeutral: true);
                }
            }
        }

        DrainKeyboardEvents();
    }

    /// <summary>A full-screen window of the settled exe counts as a game even when nothing lists it.</summary>
    private bool IsFullscreenGame(string? settledExe) =>
        settledExe is not null && _foreground.GetFullscreenWindow() is (true, { } exe) &&
        string.Equals(AutoEmulationResolver.NormalizeExe(exe), AutoEmulationResolver.NormalizeExe(settledExe), StringComparison.Ordinal);

    /// <summary>Writes a computed state unless a newer one was computed meanwhile. True when the controller write went out.</summary>
    private bool Send(OutputState? toSend, long version, ControllerSlot? slot)
    {
        if (toSend is null)
        {
            return false;
        }

        lock (_sendLock)
        {
            // A newer tick may have computed a later state already; never send an older one after it.
            return version == Interlocked.Read(ref _outputVersion) && Controllers.SetOutputAndSend(toSend, slot);
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
        ControllerSlot? slot;
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            SetLayerLocked(key, layer);
            slot = Controllers.PrimarySlot;
            OutputState output = Build(Now());
            if (!output.Equals(_lastOutput))
            {
                _lastOutput = output;
                toSend = output;
                version = ++_outputVersion;
            }
        }

        return Send(toSend, version, slot);
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
        var real = new RealController(c.DevicePath, c.Details.Link, identity, Model: c.Details.Model);
        _real = (c, real);
        return real;
    }

    internal void OnInput(InputState s) => OnPrimaryInput(Controllers.PrimarySlot, s);

    private void OnPrimaryInput(ControllerSlot? slot, InputState s)
    {
        TimeSpan now = Now();
        // Taken before anything else, so a disconnect that follows makes this report stale for the DSU server.
        int dsuGeneration = _dsu.Generation(0);
        bool refresh;
        try
        {
            lock (_primaryInputLock)
            {
                if (_disposed || !ReferenceEquals(slot, Controllers.PrimarySlot))
                {
                    return;
                }

                SynchronizePrimary(slot);
                refresh = ProcessPrimaryInput(slot, s, now, dsuGeneration);
            }
        }
        finally
        {
            DrainKeyboardEvents();
        }

        if (refresh)
        {
            Refresh();
        }

        Flush();
    }

    private void PublishDsu(ControllerSlot? slot, InputState input, Vector3 gyro, Vector3 accel, int generation)
    {
        if (slot is not null && Controllers.IsCurrent(slot))
        {
            _dsu.PublishInput(0, input, gyro, accel, slot.Controller.Details.Link, slot.Mac, generation);
        }
    }

    private bool ProcessPrimaryInput(ControllerSlot? slot, InputState s, TimeSpan now, int dsuGeneration)
    {
        bool firstInput;
        Vector3 gyro;
        Vector3 accel;
        EdgeShortcut? shortcut;
        bool micPressed;
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(slot, Controllers.PrimarySlot))
            {
                return false;
            }

            firstInput = _latestInput is null;
            // The raw button, before mapping, layers, pause or the keyboard see it.
            micPressed = (s.Buttons & Buttons.Mic) != 0 && _latestInput is { } before && (before.Buttons & Buttons.Mic) == 0;
            shortcut = Settings.Current.EdgeShortcuts && slot is not null && _latestInput is { } previous
                ? EdgeShortcuts.Read(slot.Controller.Details.Model, previous.Buttons, s.Buttons) : null;
            _latestInput = s;
            ImuCalibration imu = slot?.Controller.Details.Calibration ?? ImuCalibration.Identity;
            gyro = GyroCalibrationResult.Apply(imu.GyroDegPerSec(s.GyroRaw), _gyroBias);
            accel = imu.AccelG(s.AccelRaw);
            _idle.Observe(s, now);

            if (_battery.Update(s.Battery, Settings.Current.LowBatteryWarnings) is int threshold)
            {
                _outbox.Enqueue(new Notice(NoticeKinds.LowBattery, Text.Pick($"Battery dropped below {threshold}% ({s.Battery.Percent}% remaining).", $"Pil %{threshold} altına düştü (%{s.Battery.Percent}).")));
            }

            if (_calibrator is not null)
            {
                if (_calibrator.Add(imu.GyroDegPerSec(s.GyroRaw), now) is { } result)
                {
                    Finish(result);
                }
            }
        }

        if (!ReferenceEquals(slot, Controllers.PrimarySlot))
        {
            return false;
        }

        if (micPressed)
        {
            _micBridge?.OnMicButton();
        }

        // While the keyboard is open it owns the controller: mapping, the virtual pad and motion output see a neutral one.
        InputState input = s;
        Vector3 inputGyro = gyro;
        Vector3 inputAccel = accel;
        if (_osk.IsOpen && shortcut is not null && shortcut != EdgeShortcut.OnScreenKeyboard)
        {
            shortcut = null;
        }

        if (shortcut == EdgeShortcut.OnScreenKeyboard)
        {
            if (_osk.IsOpen)
            {
                CloseKeyboardLocked(s.Buttons);
            }
            else if (CanOpenKeyboard)
            {
                OpenKeyboardLocked(s, sendNeutral: false);
            }
        }
        else if (_osk.IsOpen)
        {
            _osk.HandleInput(s, now);
            if (!_osk.IsOpen)
            {
                CloseKeyboardLocked(s.Buttons);
            }
        }

        if (_osk.IsOpen)
        {
            input = Neutral(s);
            inputGyro = default;
            inputAccel = default;
        }
        else if (_oskHoldMask != Buttons.None)
        {
            _oskHoldMask &= s.Buttons;
            if (_oskHoldMask != Buttons.None)
            {
                input = s with
                {
                    Buttons = s.Buttons & ~_oskHoldMask,
                    L2 = (_oskHoldMask & Buttons.L2) != 0 ? (byte)0 : s.L2,
                    R2 = (_oskHoldMask & Buttons.R2) != 0 ? (byte)0 : s.R2,
                };
            }
        }

        input = ApplyRadial(input, s);
        _haptics.Observe(s);

        if (firstInput)
        {
            // Input proves a connection; mapping may become active.
            UpdateMapping();
        }

        if (!ReferenceEquals(slot, Controllers.PrimarySlot))
        {
            return false;
        }

        bool togglePause = _pauseShortcut.Observe(input.Buttons, Settings.Current.PauseCombo);
        bool resuming = togglePause && UserMappingPaused;
        if (resuming)
        {
            ToggleUserPause();
        }

        _mapper.OnInput(input, inputGyro, now, suppressLayerPulse: togglePause);
        if (Volatile.Read(ref _oskToggleRequested) != 0 && Interlocked.Exchange(ref _oskToggleRequested, 0) != 0)
        {
            // A mapped action asked in this very frame; the pad and the motion server must not get the frame that did it.
            if (_osk.IsOpen)
            {
                CloseKeyboardLocked(s.Buttons);
            }
            else if (CanOpenKeyboard)
            {
                OpenKeyboardLocked(s, sendNeutral: false);
            }

            if (_osk.IsOpen)
            {
                input = Neutral(s);
                inputGyro = default;
                inputAccel = default;
            }
        }

        if (togglePause && !resuming)
        {
            ToggleUserPause();
        }

        // After mapping, so a radial menu that opened or closed in this frame is already neutral for the motion server.
        input = ApplyRadial(input, s);
        PublishDsu(slot, input, inputGyro, inputAccel, dsuGeneration);

        _emulation?.OnInput(input, gyroDegPerSec: inputGyro, now: now, mappingActive: _mapper.Active);

        if (shortcut is { } action && EdgeShortcuts.Resolve(action, Profiles.All, ActiveProfile.Id) is { } profile)
        {
            lock (_lock)
            {
                if (ReferenceEquals(slot, Controllers.PrimarySlot) && Profiles.Get(profile.Id) is not null)
                {
                    _selector.Pin(profile.Id);
                    _outbox.Enqueue(EdgeShortcuts.Changed(profile, 0));
                    return true;
                }
            }
        }

        return false;
    }

    // A radial menu owns one stick while it is open (and until that stick is centred once after it closes), and a
    // cancelled menu's button stays withheld until let go. Runs before and after mapping; applying it twice is harmless.
    private InputState ApplyRadial(InputState input, InputState raw)
    {
        _radial.Update(raw.Left, raw.Right);
        _radialHoldMask = (_radialHoldMask | _radial.TakeCancelledButtons()) & raw.Buttons;
        if (_osk.IsOpen)
        {
            return input;
        }

        if (_radialHoldMask != Buttons.None)
        {
            input = input with
            {
                Buttons = input.Buttons & ~_radialHoldMask,
                L2 = (_radialHoldMask & Buttons.L2) != 0 ? (byte)0 : input.L2,
                R2 = (_radialHoldMask & Buttons.R2) != 0 ? (byte)0 : input.R2,
            };
        }

        if (_radial.LeftNeutral)
        {
            input = input with { Left = new Stick(128, 128) };
        }

        return _radial.RightNeutral ? input with { Right = new Stick(128, 128) } : input;
    }

    private void OnSecondaryInput(ControllerSlot slot, InputState s)
    {
        int dsuGeneration = _dsu.Generation(slot.Index);
        if (slot.Index == 0 || _disposed || !Controllers.IsCurrent(slot))
        {
            return;
        }

        // The primary controller goes through its bias-corrected path in ProcessPrimaryInput.
        ImuCalibration imu = slot.Controller.Details.Calibration;
        _dsu.PublishInput(slot.Index, s, imu.GyroDegPerSec(s.GyroRaw), imu.AccelG(s.AccelRaw),
            slot.Controller.Details.Link, slot.Mac, dsuGeneration);
    }

    private void Finish(GyroCalibrationResult result)
    {
        _calibrator = null;
        if (result.Success)
        {
            _gyroBias = result.Bias;
            _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, Text.Pick("Gyro calibrated.", "Gyro kalibre edildi."), 1));
        }
        else
        {
            _outbox.Enqueue(new Notice(NoticeKinds.GyroCalibration, result.Reason ?? Text.Pick("Gyro calibration failed.", "Gyro kalibrasyonu başarısız.")));
        }
    }

    private OutputState Build(TimeSpan now)
    {
        OutputState o = ProfileOutput.Build(_active.Profile, (_leftEffect, _rightEffect), now,
            _latestInput?.Battery.Percent, Settings.Current.AutoPlayerLeds && Controllers.Slots.Count >= 2 ? 1 : null);

        foreach ((string _, Func<OutputState, OutputState> layer) in _overrides.OrderBy(kv => IntegrationLayers.Order(kv.Key)))
        {
            o = layer(o);
        }

        return o;
    }

    private void ApplyTriggers(Profile p)
    {
        (_leftEffect, _rightEffect) = ProfileOutput.TriggersFor(p);
    }

    private void OnSettingsChanged()
    {
        // First, so everything this change produces is already in the new language.
        Text.Current = Settings.Current.Language;
        _osk.SetLanguage(Settings.Current.Language);
        DrainKeyboardEvents();
        _micBridge?.Poke();
        ConfigureControllers();
        ConfigureIntegration();
        Refresh();
    }

    private void ConfigureControllers()
    {
        AppSettings settings = Settings.Current;
        if (settings.MaxControllers > 1)
        {
            _secondary ??= new SecondaryControllers(Controllers, Profiles, Settings, _udpApi.StateAt,
                Now, () => ActiveProfile, Refresh, Queue);
        }

        Controllers.Configure(settings);
        if (settings.MaxControllers == 1)
        {
            _secondary?.Dispose();
            _secondary = null;
        }

        // Emulation per slot
        _emulation?.BindSecondary(Controllers, _secondary);
    }

    private void ConfigureIntegration()
    {
        IntegrationSettings settings = Settings.Current.Integration;
        if (_dsu.Status is { State: DsuListenerState.Listening } dsu &&
            ((settings.UdpEnabled && dsu.Port == settings.UdpPort) ||
             (settings.ForzaEnabled && dsu.Port == settings.ForzaPort) ||
             (settings.F1Enabled && dsu.Port == settings.F1Port) ||
             (settings.BeamNgEnabled && dsu.Port == settings.BeamNgPort)))
        {
            _dsu.Configure(settings with { DsuEnabled = false });
        }

        if (settings.UdpEnabled)
        {
            // Release a telemetry port before handing it to the UDP API; the Configure below rebinds what still fits.
            _telemetry.Release(settings.UdpPort);
        }

        _udpApi.Configure(settings);
        _telemetry.Configure(settings);
        _dsu.Configure(settings);
    }

    private UdpControllerStatus GetUdpControllerStatus(int index) =>
        Controllers.Slots.FirstOrDefault(s => s.Index == index)?.Controller is { Connected: true } controller
            ? new(controller.Details.Link, controller.Latest?.Battery.Percent)
            : default;

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
            if (!string.Equals(key, _elevationCheckedFor, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _blockedBy = blocks ? exe : null;
            first = blocks && _elevatedNoticed.Add(exe!);
        }

        if (first)
        {
            Queue(new Notice(NoticeKinds.Elevated,
                Text.Pick($"{exe} is running as administrator. Windows blocks keyboard and mouse input to this window. Restart TriggerForge as administrator.", $"{exe} yönetici olarak çalışıyor; Windows bu pencereye tuş ve fare girdisi gönderilmesini engelliyor. TriggerForge'u yönetici olarak yeniden başlatın.")));
        }
    }

    /// <summary>Pushes the active profile and state to the mapper; raises <see cref="MappingChanged"/> when the status moves.</summary>
    private void UpdateMapping(bool notify = false)
    {
        if (_disposed)
        {
            return;
        }

        bool active;
        bool mappingOff;
        lock (_mappingLock)
        {
            Profile profile;
            bool connected;
            lock (_lock)
            {
                profile = _active.Profile;
                connected = Controllers.Controller is not null || _latestInput is not null;
            }

            bool allowTestTurn = GetPauseReason() is null && connected;
            mappingOff = !profile.Mapping.Enabled;
            active = profile.Mapping.Enabled && allowTestTurn;
            _mapper.Configure(profile);
            _mapper.SetActive(active, allowTestTurn);
        }

        if ((mappingOff || GetPauseReason() is not null) && _osk.IsOpen)
        {
            CloseOnScreenKeyboard();
        }

        // The mapper cancels an open radial menu on pause, profile change or mapping off; announce it outside every lock.
        DrainKeyboardEvents();

        // The probe is a few system calls; keep it outside the lock.
        CheckElevation(active);

        bool changed;
        lock (_mappingLock)
        {
            string? reason = GetPauseReason();
            active = _mapper.Active && reason is null;
            var status = new MappingStatus(active, reason is not null, active ? _blockedBy : null, reason);
            changed = status != _mappingStatus;
            _mappingStatus = status;
        }

        if (changed || notify)
        {
            MappingChanged?.Invoke();
        }
    }

    private bool CanOpenKeyboard => ActiveProfile.Mapping.Enabled && GetPauseReason() is null;

    private string? GetPauseReason() => UiMappingPaused ? "ui" :
        UserMappingPaused ? "shortcut" : FullscreenPaused ? "fullscreen" : null;

    private void PulseConfirm(TimeSpan now)
    {
        OutputState? output = null;
        long version = 0;
        ControllerSlot? slot;
        lock (_hapticsLock)
        {
            lock (_lock)
            {
                slot = Controllers.PrimarySlot;
                if (_disposed || !_active.Profile.Haptics.ConfirmPulses ||
                    (slot is null && _latestInput is null))
                {
                    return;
                }

                if (slot?.Controller.Details is { Link: Link.Bluetooth, Model: not ControllerModel.DualShock4 } && _haptics.PulseConfirm())
                {
                    return;
                }

                _confirmUntil = Now() + TimeSpan.FromMilliseconds(60);
                SetLayerLocked("confirm", static state => state with { RumbleLeft = 64, RumbleRight = 160 });
                output = Build(now);
                _lastOutput = output;
                version = ++_outputVersion;
            }
        }

        Send(output, version, slot);
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
            Queue(new Notice(NoticeKinds.Mapping, "The mapped profile was not found.", "Eşlemedeki profil bulunamadı."));
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

        public void ToggleOnScreenKeyboard() => Volatile.Write(ref core._oskToggleRequested, 1);

        public void LayerChanged(string? name)
        {
            Volatile.Write(ref core._layerName, name);
            core.ActiveLayerChanged?.Invoke(name);
        }

        public void PulseConfirm() => core.PulseConfirm(core.Now());

        public void Report(Notice notice) => core.Queue(notice);
    }

    internal void OnConnection(DualSenseController? controller)
    {
        _micBridge?.Poke();
        lock (_primaryInputLock)
        {
            ControllerSlot? slot = Controllers.PrimarySlot;
            if (_disposed || !ReferenceEquals(controller, slot?.Controller))
            {
                return;
            }

            SynchronizePrimary(slot, disconnected: slot is null);
        }

        DrainKeyboardEvents();
        Refresh();
    }

    private void SynchronizePrimary(ControllerSlot? slot, bool disconnected = false)
    {
        if (ReferenceEquals(_inputSlot, slot) && !disconnected)
        {
            return;
        }

        lock (_lock)
        {
            _inputSlot = slot;
            _idle.Reset(Now());
            _battery.Reset();
            _latestInput = null;
            _lastOutput = null;
            _confirmUntil = null;
            SetLayerLocked("confirm", null);
            if (_calibrator is not null)
            {
                Finish(_calibrator.Fail(Text.Pick("Gyro calibration cancelled because the primary controller changed.", "Birincil kontrolcü değiştiği için gyro kalibrasyonu iptal edildi.")));
            }

            if (slot is not null)
            {
                string device = slot.Mac ?? slot.Controller.DevicePath;
                if (!string.Equals(_gyroDevice, device, StringComparison.OrdinalIgnoreCase))
                {
                    _gyroBias = default;
                    _gyroDevice = device;
                }
            }
        }

        _pauseShortcut.Reset();
        _oskHoldMask = Buttons.None;
        _radialHoldMask = Buttons.None;
        Volatile.Write(ref _oskToggleRequested, 0);
        _osk.Close();
        _mapper.ResetController();
        _radial.Reset();
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
            Queue(new Notice(NoticeKinds.Info, Text.Pick($"Output engine error: {e.Message}", $"Çıktı motoru hatası: {e.Message}")));
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
            // The mapper resets layers on a profile change without announcing it; say so after the profile.
            string? layer = _mapper.ActiveLayerName;
            if (!string.Equals(Interlocked.Exchange(ref _layerName, layer), layer, StringComparison.Ordinal))
            {
                ActiveLayerChanged?.Invoke(layer);
            }
        }

        while (_outbox.TryDequeue(out Notice? n))
        {
            Queue(n);
        }
    }

    private void Queue(Notice n)
    {
        Log.Notice(n);
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
