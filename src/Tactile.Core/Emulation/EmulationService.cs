using System.Diagnostics;
using Tactile.Audio;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;

namespace Tactile.Core;

public enum EmulationAudioStatus { Off, Ready, Playing }

/// <param name="Available">False when the USB/IP server or the driver layer could not start.</param>
/// <param name="Mode">The mode of the pad attached right now, Off when none.</param>
public sealed record EmulationStatus(
    bool Available,
    bool Enabled,
    EmulationMode Mode,
    bool Attached,
    bool Hidden,
    IReadOnlyList<DriverStatus> Drivers,
    LatencySummary Latency,
    string? Error,
    EmulationAudioStatus Audio = EmulationAudioStatus.Off)
{
    public static EmulationStatus Unavailable { get; } = new(false, false, EmulationMode.Off, false, false, [], LatencySummary.Empty, null);

    /// <summary>Equal apart from latency, which moves all the time.</summary>
    public bool SameState(EmulationStatus other) =>
        Available == other.Available && Enabled == other.Enabled && Mode == other.Mode && Attached == other.Attached &&
        Hidden == other.Hidden && Error == other.Error && Audio == other.Audio && Drivers.SequenceEqual(other.Drivers);
}

/// <summary>The real controller as the emulation needs it.</summary>
internal sealed record RealController(string Path, Link Link, DualSenseIdentity? Identity);

internal interface IEmulationHost
{
    /// <summary>Sets or clears an output layer and sends at once; true when a controller write went out.</summary>
    bool SetOutputLayer(string key, Func<OutputState, OutputState>? layer);

    void Report(Notice notice);
}

/// <summary>
/// Owns the virtual pad: decides whether one should exist, creates, attaches and hides in that
/// order, and undoes it in reverse. Lifecycle work runs on its own thread so device calls never
/// block the output tick or the UI; feedback from the game arrives on the server thread.
/// </summary>
public sealed class EmulationService : IDisposable
{
    public const string XboxLayer = "emulation";
    public const string GameLayer = "game";

    private static readonly TimeSpan DriverCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AudioDefaultInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AudioDefaultWindow = TimeSpan.FromSeconds(15);

    private readonly DriverPlatform _platform;
    private readonly IUsbIpServer? _server;
    private readonly IEmulationHost _host;
    private readonly IAudioDefaults? _audioDefaults;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<bool> _steamRunning;
    private readonly Func<long> _timestamp;
    private readonly bool _threaded;
    private readonly Thread? _worker;
    private readonly AutoResetEvent _wake = new(false);
    private readonly LatencyStats _latency = new();

    // Serialises lifecycle work (start, stop, hide, restore).
    private readonly Lock _opLock = new();
    // Guards the wanted state and status fields read from other threads.
    private readonly Lock _stateLock = new();
    // Feedback handling and clearing the game layers never interleave.
    private readonly Lock _feedbackLock = new();

    private Wanted? _wanted;
    private (string ProfileId, EmulationMode Mode, bool Audio)? _failedFor;
    private string? _error;
    private DriverStatus[] _drivers;
    private TimeSpan? _driversCheckedAt;
    private EmulationStatus _lastStatus = EmulationStatus.Unavailable;
    private bool _steamNoticed;
    private volatile InputPlan _plan = new(new EmulationConfig(), Buttons.None, 1);
    private volatile bool _disposed;

    // Current pad; only the worker (under _opLock) changes these.
    private volatile IVirtualPad? _pad;
    private Action<PadFeedback>? _padHandler;
    private VirtualKind _kind;
    private bool _padAudioEnabled;
    private int _hubPort;
    private string? _padPath;
    private string? _hiddenId;
    private bool _hideTried;
    private bool _unhideFailNoticed;
    private Dictionary<AudioDefaultSlot, string>? _audioDefaultsToRestore;
    private TimeSpan _audioWatchUntil;
    private TimeSpan _nextAudioCheck;
    private bool _audioCheckFailed;

    // The game's accumulated DualSense output, under _feedbackLock.
    private OutputState _game = OutputState.Default;
    private OutputFields _gameFields;

    internal EmulationService(DriverPlatform platform, IUsbIpServer? server, IEmulationHost host, Func<TimeSpan> clock, bool threaded,
        Func<bool>? steamRunning = null, Func<long>? timestamp = null, IAudioDefaults? audioDefaults = null)
    {
        _platform = platform;
        _server = server;
        _host = host;
        _audioDefaults = audioDefaults;
        _clock = clock;
        _threaded = threaded;
        _steamRunning = steamRunning ?? SteamRunning;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _drivers = [Missing(DriverKind.UsbIp), Missing(DriverKind.HidHide)];

        // A crash last time may have left a pad attached or the controller hidden.
        bool unclean;
        try
        {
            unclean = platform.Recovery.UncleanShutdown;
        }
        catch (Exception)
        {
            unclean = false;
        }

        if (unclean)
        {
            try
            {
                platform.Recovery.RestoreAll();
                Notify("Tactile geçen sefer düzgün kapanmadı; sanal kontrolcü kaldırıldı ve gerçek kontrolcü yeniden görünür yapıldı.");
            }
            catch (Exception e)
            {
                Notify($"Geçen oturumdan kalanlar geri alınamadı: {e.Message} Ayarlar'daki “Kontrolcüyü geri getir” ile yeniden deneyin.");
            }
        }

        if (server is null)
        {
            _error = "Sanal USB sunucusu başlatılamadı.";
        }

        if (threaded)
        {
            _worker = new Thread(Run) { IsBackground = true, Name = "tactile-emulation" };
            _worker.Start();
        }
        else
        {
            Reconcile();
        }
    }

    /// <summary>Raised when the status (apart from latency) changes. Any thread.</summary>
    public event Action? Changed;

    internal IVirtualAudio? VirtualAudio => _pad as IVirtualAudio;

    public EmulationStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return BuildStatus();
            }
        }
    }

    /// <summary>Called with every tick's view of the world; cheap when nothing changed.</summary>
    internal void Update(Profile profile, AppSettings settings, RealController? controller)
    {
        EmulationConfig config = profile.Emulation ?? new();
        _plan = new InputPlan(config, config.HideMappedButtons ? EmulationInput.MappedButtons(profile) : Buttons.None, profile.Rumble?.Strength ?? 1);
        var wanted = new Wanted(profile.Id, config.Mode, settings.Emulation ?? new(), controller);
        lock (_stateLock)
        {
            if (wanted == _wanted)
            {
                return;
            }

            if (_wanted is null || _wanted.FailureKey != wanted.FailureKey)
            {
                ClearFailureLocked();
            }

            _wanted = wanted;
        }

        Signal();
    }

    /// <summary>Forgets an attach failure and tries again.</summary>
    public void Retry()
    {
        lock (_stateLock)
        {
            ClearFailureLocked();
            _driversCheckedAt = null;
        }

        Signal();
    }

    /// <summary>Forces a fresh driver check (after an install).</summary>
    public void RecheckDrivers()
    {
        lock (_stateLock)
        {
            _driversCheckedAt = null;
        }

        Signal();
    }

    public Task<InstallResult> InstallAsync(DriverKind kind, IProgress<string>? progress, CancellationToken ct) =>
        _platform.Drivers.InstallAsync(kind, progress, ct);

    /// <summary>Removes our pad, then undoes everything in the recovery journal. Synchronous.</summary>
    public void RestoreAll()
    {
        lock (_opLock)
        {
            StopLocked();
            try
            {
                _platform.Recovery.RestoreAll();
            }
            catch (Exception e)
            {
                Notify($"Geri alma tamamlanamadı: {e.Message}");
            }
        }

        RaiseIfChanged();
    }

    /// <summary>Real controller input → remap → pad. Reader thread.</summary>
    internal void OnInput(InputState s)
    {
        IVirtualPad? pad = _pad;
        if (pad is null)
        {
            return;
        }

        InputPlan plan = _plan;
        try
        {
            pad.Submit(EmulationInput.Transform(s, plan.Config, plan.Hidden));
        }
        catch (ObjectDisposedException)
        {
            // Removed between the read and the call.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wake.Set();
        _worker?.Join(TimeSpan.FromSeconds(5));
        bool clean;
        lock (_opLock)
        {
            clean = StopLocked();
        }

        try
        {
            _server?.Dispose();
        }
        catch (Exception)
        {
        }

        // A failed undo keeps the journal, so the next start (or --restore) finishes the job.
        if (clean)
        {
            try
            {
                _platform.Recovery.MarkCleanExit();
            }
            catch (Exception)
            {
            }
        }

        // _wake stays undisposed: a late tick on the timer thread may still signal it.
    }

    private void Run()
    {
        while (!_disposed)
        {
            _wake.WaitOne(ReconcileDelay());
            if (_disposed)
            {
                break;
            }

            try
            {
                Reconcile();
            }
            catch (Exception e)
            {
                Fail($"Sanal kontrolcü hatası: {e.Message}");
            }
        }
    }

    private TimeSpan ReconcileDelay()
    {
        lock (_opLock)
        {
            if (_audioDefaultsToRestore is null)
                return TimeSpan.FromSeconds(1);

            TimeSpan next = _nextAudioCheck < _audioWatchUntil ? _nextAudioCheck : _audioWatchUntil;
            TimeSpan delay = next - _clock();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private void Signal()
    {
        if (_threaded)
        {
            if (!_disposed)
            {
                _wake.Set();
            }
        }
        else
        {
            Reconcile();
        }
    }

    internal void Reconcile()
    {
        lock (_opLock)
        {
            if (_disposed)
            {
                return;
            }

            RefreshDriversIfDue();
            Wanted? w;
            VirtualKind? want;
            lock (_stateLock)
            {
                w = _wanted;
                want = WantedKindLocked(w);
            }

            if (_pad is not null && (want != _kind || w?.Controller?.Path != _padPath ||
                _kind == VirtualKind.DualSense && w?.Settings.Audio != _padAudioEnabled))
            {
                StopLocked();
            }

            if (_pad is null && _hiddenId is not null)
            {
                Unhide();
            }

            if (want is VirtualKind kind && _pad is null && w?.Controller is { } controller)
            {
                Start(kind, w, controller);
            }

            if (_pad is not null && w is not null)
            {
                CheckAudioDefaults(w.Settings.KeepAudioDefaults);
                SyncHide(w);
                CheckSteam();
            }
        }

        RaiseIfChanged();
    }

    private VirtualKind? WantedKindLocked(Wanted? w)
    {
        if (_server is null || w?.Controller is null || !w.Settings.Enabled || w.Mode == EmulationMode.Off)
        {
            return null;
        }

        if (_failedFor == w.FailureKey)
        {
            return null;
        }

        if (!Usable(DriverKind.UsbIp) || (w.Settings.HideController && !Usable(DriverKind.HidHide)))
        {
            return null;
        }

        // A controller on USB already is a wired DualSense.
        if (w.Mode == EmulationMode.DualSense && w.Controller.Link == Link.Usb)
        {
            return null;
        }

        return w.Mode == EmulationMode.DualSense ? VirtualKind.DualSense : VirtualKind.Xbox360;
    }

    private void Start(VirtualKind kind, Wanted w, RealController controller)
    {
        IUsbIpServer server = _server!;
        IVirtualPad pad;
        try
        {
            pad = kind == VirtualKind.DualSense
                ? server.CreateDualSense(controller.Identity, w.Settings.Audio)
                : server.Create(kind, null);
        }
        catch (Exception e)
        {
            Fail($"Sanal kontrolcü oluşturulamadı: {e.Message}", w);
            return;
        }

        Action<PadFeedback> handler = f => OnFeedback(pad, f);
        pad.Feedback += handler;
        var defaults = kind == VirtualKind.DualSense && w.Settings.Audio && w.Settings.KeepAudioDefaults
            ? SnapshotAudioDefaults() : null;
        int port;
        try
        {
            port = _platform.Vhci.Attach(server.Port, pad.BusId);
            if (port < 0)
            {
                throw new InvalidOperationException($"geçersiz hub portu {port}");
            }
        }
        catch (Exception e)
        {
            pad.Feedback -= handler;
            TryRemove(pad);
            Fail($"Sanal kontrolcü takılamadı: {e.Message}", w);
            return;
        }

        if (defaults is { Count: > 0 })
        {
            TimeSpan attachedAt = _clock();
            _audioDefaultsToRestore = defaults;
            _audioWatchUntil = attachedAt + AudioDefaultWindow;
            _nextAudioCheck = attachedAt + AudioDefaultInterval;
            _audioCheckFailed = false;
        }

        lock (_feedbackLock)
        {
            _game = OutputState.Default;
            _gameFields = OutputFields.None;
        }

        _latency.Clear();
        _kind = kind;
        _padAudioEnabled = w.Settings.Audio;
        _hubPort = port;
        _padPath = controller.Path;
        _padHandler = handler;
        _hideTried = false;
        // Published last: input starts flowing only once the pad is attached.
        _pad = pad;
    }

    private Dictionary<AudioDefaultSlot, string>? SnapshotAudioDefaults()
    {
        try
        {
            return _audioDefaults?.ReadDefaults(includeNames: false).ToDictionary(entry => entry.Key, entry => entry.Value.Id);
        }
        catch (Exception e)
        {
            Notify($"Varsayılan ses cihazları kaydedilemedi: {e.Message}");
            return null;
        }
    }

    private void CheckAudioDefaults(bool enabled)
    {
        if (_audioDefaultsToRestore is not { } pending || _audioDefaults is not { } audio)
            return;

        TimeSpan now = _clock();
        if (!enabled || now >= _audioWatchUntil)
        {
            _audioDefaultsToRestore = null;
            return;
        }

        if (now < _nextAudioCheck)
            return;

        _nextAudioCheck = now + AudioDefaultInterval;
        IReadOnlyDictionary<AudioDefaultSlot, AudioDefaultEndpoint> current;
        try
        {
            current = audio.ReadDefaults(includeNames: true);
        }
        catch (Exception e)
        {
            if (!_audioCheckFailed)
            {
                _audioCheckFailed = true;
                Notify($"Varsayılan ses cihazları denetlenemedi: {e.Message}");
            }
            return;
        }

        foreach (var (slot, endpoint) in current)
        {
            if (_clock() >= _audioWatchUntil)
                break;

            if (!pending.TryGetValue(slot, out string? original) || original == endpoint.Id ||
                endpoint.FriendlyName?.Contains("DualSense", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            pending.Remove(slot);
            try
            {
                audio.Restore(original, slot.Role);
            }
            catch (Exception e)
            {
                Notify($"Varsayılan ses cihazı geri yüklenemedi: {e.Message}");
            }
        }

        if (pending.Count == 0)
            _audioDefaultsToRestore = null;
    }

    private void SyncHide(Wanted w)
    {
        bool want = w.Settings.HideController && Usable(DriverKind.HidHide);
        if (!want)
        {
            _hideTried = false;
            Unhide();
            return;
        }

        if (_hiddenId is not null || _hideTried || _padPath is null)
        {
            return;
        }

        _hideTried = true;
        try
        {
            string? id = _platform.Tree.InstanceId(_padPath);
            if (id is null)
            {
                Notify("Gerçek kontrolcünün aygıt kimliği bulunamadı, gizlenemedi. Oyunlar iki kontrolcü görebilir.");
            }
            else if (_platform.Hider.Hide(id))
            {
                _hiddenId = id;
            }
            else
            {
                Notify("Gerçek kontrolcü gizlenemedi. Oyunlar iki kontrolcü görebilir.");
            }
        }
        catch (Exception e)
        {
            Notify($"Gerçek kontrolcü gizlenemedi: {e.Message}");
        }
    }

    private bool Unhide()
    {
        if (_hiddenId is not { } id)
        {
            return true;
        }

        try
        {
            _platform.Hider.Unhide(id);
            _hiddenId = null;
            _unhideFailNoticed = false;
            return true;
        }
        catch (Exception e)
        {
            // Kept, so the next reconcile tries again; the journal covers a crash.
            if (!_unhideFailNoticed)
            {
                _unhideFailNoticed = true;
                Notify($"Gerçek kontrolcü yeniden görünür yapılamadı: {e.Message}");
            }

            return false;
        }
    }

    /// <summary>Unhide, detach, remove. True when every step worked (or nothing was attached).</summary>
    private bool StopLocked()
    {
        _audioDefaultsToRestore = null;
        IVirtualPad? pad = _pad;
        if (pad is null)
        {
            return Unhide();
        }

        _pad = null;
        if (_padHandler is not null)
        {
            pad.Feedback -= _padHandler;
            _padHandler = null;
        }

        bool clean = Unhide();
        try
        {
            _platform.Vhci.Detach(_hubPort);
        }
        catch (Exception e)
        {
            clean = false;
            Notify($"Sanal kontrolcü çıkarılamadı: {e.Message}");
        }

        TryRemove(pad);
        _padPath = null;
        _hideTried = false;

        lock (_feedbackLock)
        {
            _game = OutputState.Default;
            _gameFields = OutputFields.None;
            _host.SetOutputLayer(XboxLayer, null);
            _host.SetOutputLayer(GameLayer, null);
        }

        return clean;
    }

    private void TryRemove(IVirtualPad pad)
    {
        try
        {
            _server?.Remove(pad);
        }
        catch (Exception)
        {
        }
    }

    private void OnFeedback(IVirtualPad from, PadFeedback f)
    {
        lock (_feedbackLock)
        {
            if (!ReferenceEquals(from, _pad))
            {
                return;
            }

            double strength = _plan.RumbleStrength;
            bool sent;
            switch (f)
            {
                case RumbleFeedback r:
                {
                    byte large = GameOutput.ScaleRumble(r.Large, strength);
                    byte small = GameOutput.ScaleRumble(r.Small, strength);
                    sent = _host.SetOutputLayer(XboxLayer, o => o with { RumbleLeft = large, RumbleRight = small });
                    break;
                }

                case DualSenseOutputFeedback d when Block(d.State) is { } block:
                {
                    _gameFields |= OutputReport.ValidFields(block);
                    _game = OutputReport.ApplyState(block, _game);
                    OutputState game = _game;
                    OutputFields fields = _gameFields;
                    sent = _host.SetOutputLayer(GameLayer, o => GameOutput.Overlay(o, game, fields, strength));
                    break;
                }

                default:
                    // Xbox ring LED and malformed reports.
                    return;
            }

            if (sent)
            {
                _latency.Add((_timestamp() - f.Timestamp) * 1000.0 / Stopwatch.Frequency);
            }
        }
    }

    /// <summary>The 47-byte state block: given as is, or inside a 48/63/64-byte report 0x02.</summary>
    internal static byte[]? Block(byte[]? state) => state?.Length switch
    {
        OutputReport.StateLength => state,
        48 or OutputReport.UsbLength or 64 when state![0] == 0x02 => state[1..(1 + OutputReport.StateLength)],
        _ => null,
    };

    private void CheckSteam()
    {
        if (_steamNoticed)
        {
            return;
        }

        bool running;
        try
        {
            running = _steamRunning();
        }
        catch (Exception)
        {
            running = false;
        }

        if (running)
        {
            _steamNoticed = true;
            Notify("Steam Input bu oyunda açıksa çift kontrolcü görünür; oyunun Steam ayarlarında Steam Input'u kapatın.");
        }
    }

    private void RefreshDriversIfDue()
    {
        TimeSpan now = _clock();
        lock (_stateLock)
        {
            if (_driversCheckedAt is { } at && now - at < DriverCheckInterval)
            {
                return;
            }

            _driversCheckedAt = now;
        }

        var fresh = new DriverStatus[2];
        DriverKind[] kinds = [DriverKind.UsbIp, DriverKind.HidHide];
        for (int i = 0; i < kinds.Length; i++)
        {
            try
            {
                fresh[i] = _platform.Drivers.Status(kinds[i]);
            }
            catch (Exception e)
            {
                fresh[i] = new DriverStatus(kinds[i], DriverState.Error, null, e.Message);
            }
        }

        lock (_stateLock)
        {
            _drivers = fresh;
        }
    }

    /// <summary>Ready, or a passing error (e.g. HidHide busy) while a pad is attached, which must not tear it down mid-game.</summary>
    private bool Usable(DriverKind kind) =>
        State(kind) == DriverState.Ready || (_pad is not null && State(kind) == DriverState.Error);

    private DriverState State(DriverKind kind) => _drivers.FirstOrDefault(d => d.Kind == kind)?.State ?? DriverState.Missing;

    private void Fail(string message, Wanted? w = null)
    {
        lock (_stateLock)
        {
            _error = message;
            w ??= _wanted;
            if (w is not null)
            {
                _failedFor = w.FailureKey;
            }
        }

        Notify(message);
        RaiseIfChanged();
    }

    private void ClearFailureLocked()
    {
        _failedFor = null;
        _error = _server is null ? "Sanal USB sunucusu başlatılamadı." : null;
    }

    private EmulationStatus BuildStatus()
    {
        IVirtualPad? pad = _pad;
        EmulationMode mode = pad is null ? EmulationMode.Off : _kind == VirtualKind.DualSense ? EmulationMode.DualSense : EmulationMode.Xbox360;
        return new EmulationStatus(
            Available: _server is not null,
            Enabled: _wanted?.Settings.Enabled ?? true,
            Mode: mode,
            Attached: pad is not null,
            Hidden: pad is not null && _hiddenId is not null,
            Drivers: _drivers,
            Latency: _latency.Summary(),
            Error: _error,
            Audio: pad is IVirtualAudio audio
                ? audio.SpeakerActive ? EmulationAudioStatus.Playing : EmulationAudioStatus.Ready
                : EmulationAudioStatus.Off);
    }

    private void RaiseIfChanged()
    {
        bool changed;
        lock (_stateLock)
        {
            EmulationStatus now = BuildStatus();
            changed = !now.SameState(_lastStatus);
            _lastStatus = now;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void Notify(string message) => _host.Report(new Notice(NoticeKinds.Emulation, message));

    private static DriverStatus Missing(DriverKind kind) => new(kind, DriverState.Missing, null, null);

    private static bool SteamRunning()
    {
        Process[] found = Process.GetProcessesByName("steam");
        foreach (Process p in found)
        {
            p.Dispose();
        }

        return found.Length > 0;
    }

    private sealed record Wanted(string ProfileId, EmulationMode Mode, EmulationSettings Settings, RealController? Controller)
    {
        public (string ProfileId, EmulationMode Mode, bool Audio) FailureKey =>
            (ProfileId, Mode, Mode == EmulationMode.DualSense && Settings.Audio);
    }

    private sealed record InputPlan(EmulationConfig Config, Buttons Hidden, double RumbleStrength);
}
