using System.Diagnostics;
using Tactile.Audio;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;

namespace Tactile.Core;

public enum EmulationAudioStatus { Off, Ready, Playing }

public sealed record EmulationSlotStatus(int Index, EmulationMode Mode, bool Attached, bool Hidden, string? Error);

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
    public IReadOnlyList<EmulationSlotStatus> Slots { get; init; } = [];

    public static EmulationStatus Unavailable { get; } = new(false, false, EmulationMode.Off, false, false, [], LatencySummary.Empty, null);

    /// <summary>Equal apart from latency, which moves all the time.</summary>
    public bool SameState(EmulationStatus other) =>
        Available == other.Available && Enabled == other.Enabled && Mode == other.Mode && Attached == other.Attached &&
        Hidden == other.Hidden && Error == other.Error && Audio == other.Audio && Drivers.SequenceEqual(other.Drivers) &&
        Slots.SequenceEqual(other.Slots);
}

/// <summary>The real controller as the emulation needs it.</summary>
internal sealed record RealController(string Path, Link Link, DualSenseIdentity? Identity, SecondaryEmulationConnection? Secondary = null);

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
public sealed partial class EmulationService : IDisposable
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
    private readonly AutoResetEvent? _wake;
    private readonly LatencyStats _latency = new();
    private readonly int _slotIndex;

    // Serialises lifecycle work (start, stop, hide, restore).
    private readonly Lock _opLock = new();
    // Guards the wanted state and status fields read from other threads.
    private readonly Lock _stateLock = new();
    // Feedback handling and clearing the game layers never interleave.
    private readonly Lock _feedbackLock = new();

    private Wanted? _wanted;
    private (string ProfileId, EmulationMode Mode, bool Audio, SecondaryEmulationConnection? Secondary)? _failedFor;
    private (string En, string Tr)? _error;
    private DriverStatus[] _drivers;
    private TimeSpan? _driversCheckedAt;
    private EmulationStatus _lastStatus = EmulationStatus.Unavailable;
    private bool _steamNoticed;
    private GyroStick? _gyroStick;
    private GyroStickConfig? _gyroConfig;
    private string? _gyroProfile;
    private IVirtualPad? _gyroPad;
    private volatile InputPlan _plan = new(new EmulationConfig(), Buttons.None, 1);
    private volatile bool _disposed;

    // Current pad; only the worker (under _opLock) changes these.
    private volatile IVirtualPad? _pad;
    private Action<PadFeedback>? _padHandler;
    private VirtualKind _kind;
    private bool _padAudioEnabled;
    private int _hubPort;
    private string? _padPath;
    private volatile string? _hiddenId;
    private string? _hiddenPath;
    private volatile LocalizedMessage? _hideError;
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
        Func<bool>? steamRunning = null, Func<long>? timestamp = null, IAudioDefaults? audioDefaults = null, int slotIndex = 0)
    {
        _platform = platform;
        _server = server;
        _host = host;
        _audioDefaults = audioDefaults;
        _clock = clock;
        _threaded = threaded;
        _slotIndex = slotIndex;
        _steamRunning = steamRunning ?? SteamRunning;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _drivers = [Missing(DriverKind.UsbIp), Missing(DriverKind.HidHide)];

        if (slotIndex != 0)
        {
            return;
        }

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
                Notify("TriggerForge did not close cleanly. The virtual controller was removed and the physical controller is visible again.", "Tactile geçen sefer düzgün kapanmadı; sanal kontrolcü kaldırıldı ve gerçek kontrolcü yeniden görünür yapıldı.");
            }
            catch (Exception e)
            {
                Notify($"Could not undo the previous session's changes: {Text.English(e)} Try Restore controller in Settings.", $"Geçen oturumdan kalanlar geri alınamadı: {e.Message} Ayarlar'daki “Kontrolcüyü geri getir” ile yeniden deneyin.");
            }
        }

        if (server is null)
        {
            _error = ("Could not start the virtual USB server.", "Sanal USB sunucusu başlatılamadı.");
        }

        if (threaded)
        {
            _wake = new AutoResetEvent(false);
            _worker = new Thread(Run) { IsBackground = true, Name = "TriggerForge: emulation" };
            _worker.Start();
        }
        else
        {
            Reconcile();
        }
    }

    /// <summary>Raised when the status (apart from latency) changes. Any thread.</summary>
    public event Action? Changed;

    internal IVirtualAudio? VirtualAudio => _route is { IsCurrent: true } route && ReferenceEquals(route.Pad, _pad)
        ? route.Pad as IVirtualAudio : null;

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
        if (UpdateWanted(profile, settings.Emulation ?? new(), controller) ||
            (!_threaded && (_secondarySource is not null || SecondarySlots().Any())))
        {
            Signal();
        }
    }

    private bool UpdateWanted(Profile? profile, EmulationSettings settings, RealController? controller)
    {
        EmulationConfig config = profile?.Emulation ?? new();
        _plan = new InputPlan(config,
            _slotIndex == 0 && config.HideMappedButtons && profile is not null ? EmulationInput.MappedButtons(profile) : Buttons.None,
            profile?.Rumble?.Strength ?? 1,
            _slotIndex == 0 || profile is null ? null : new SecondaryAxes(profile), profile?.Id);
        var wanted = new Wanted(profile?.Id ?? "", config.Mode, settings, controller);
        lock (_stateLock)
        {
            if (wanted == _wanted)
            {
                return false;
            }

            if (_wanted is null || _wanted.FailureKey != wanted.FailureKey)
            {
                ClearFailureLocked();
            }

            _wanted = wanted;
        }

        return true;
    }

    /// <summary>Forgets an attach failure and tries again.</summary>
    public void Retry()
    {
        ResetFailure();
        foreach (EmulationService slot in SecondarySlots())
        {
            slot.ResetFailure();
        }

        Signal();
    }

    private void ResetFailure()
    {
        lock (_stateLock)
        {
            ClearFailureLocked();
            _driversCheckedAt = null;
            if (_slotIndex > 0)
            {
                _retryHide = true;
            }
        }
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
            StopAllLocked();
            try
            {
                _platform.Recovery.RestoreAll();
            }
            catch (Exception e)
            {
                Notify($"Could not undo changes: {Text.English(e)}", $"Geri alma tamamlanamadı: {e.Message}");
            }
        }

        RaiseIfChanged();
    }

    /// <summary>Real controller input → remap → pad. Reader thread.</summary>
    internal void OnInput(InputState s, ControllerSlot? slot = null, Vector3 gyroDegPerSec = default, TimeSpan now = default)
    {
        IVirtualPad? pad = _pad;
        if (pad is null || _route is not { IsCurrent: true } route || !ReferenceEquals(route.Pad, pad) ||
            !ReferenceEquals(route.Secondary?.Slot, slot))
        {
            return;
        }

        InputPlan plan = _plan;
        try
        {
            (float X, float Y) offset = default;
            if (_slotIndex == 0)
            {
                GyroStickConfig? gyro = plan.Config.GyroStick is { Mode: not GyroMouseMode.Off } configured
                    ? configured : null;
                if (gyro != _gyroConfig || plan.ProfileId != _gyroProfile || !ReferenceEquals(pad, _gyroPad))
                {
                    (_gyroConfig, _gyroProfile, _gyroPad) = (gyro, plan.ProfileId, pad);
                    _gyroStick = gyro is null ? null : new GyroStick(gyro);
                }

                offset = _gyroStick?.Input(gyroDegPerSec, s, now) ?? default;
            }

            pad.Submit(EmulationInput.Transform(plan.Axes?.Apply(s) ?? s, plan.Config, plan.Hidden, offset));
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
        UnbindSecondary();
        _wake?.Set();
        _worker?.Join(TimeSpan.FromSeconds(5));
        bool clean;
        lock (_opLock)
        {
            clean = StopAllLocked();
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
            _wake!.WaitOne(ReconcileDelay());
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
                Fail($"Virtual controller error: {Text.English(e)}", $"Sanal kontrolcü hatası: {e.Message}");
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
                _wake!.Set();
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
            (Wanted? wanted, VirtualKind? kind) = Desired();
            if (!UpdateSecondaries(wanted))
            {
                return;
            }
            var secondaryPlans = SecondarySlots().Select(slot => (Slot: slot, Plan: slot.Desired())).ToArray();

            // A primary swap can move both devices. Release their former hiding entries before either reattaches.
            StopObsolete(wanted, kind);
            foreach (var (slot, (secondaryWanted, secondaryKind)) in secondaryPlans)
            {
                slot.StopObsolete(secondaryWanted, secondaryKind);
            }

            string?[] pendingUnhide = secondaryPlans.Select(p => p.Slot.PendingUnhide).Append(PendingUnhide).ToArray();
            Apply(wanted, kind, pendingUnhide);
            foreach (var (slot, (secondaryWanted, secondaryKind)) in secondaryPlans)
            {
                slot.Apply(secondaryWanted, secondaryKind, pendingUnhide);
            }

            if (_pad is not null || secondaryPlans.Any(p => p.Slot._pad is not null))
            {
                CheckSteam();
            }
        }

        RaiseIfChanged();
    }

    private (Wanted? Wanted, VirtualKind? Kind) Desired()
    {
        lock (_stateLock)
        {
            return (_wanted, WantedKindLocked(_wanted));
        }
    }

    private string? PendingUnhide => _pad is null ? _hiddenPath : null;

    private void StopObsolete(Wanted? w, VirtualKind? kind)
    {
        if (_pad is not null && (kind != _kind || w?.Controller?.Path != _padPath ||
            _kind == VirtualKind.DualSense && w?.Settings.Audio != _padAudioEnabled ||
            w?.Controller?.Secondary != _route?.Secondary || _route?.IsCurrent == false))
        {
            StopLocked();
        }

        if (PendingUnhide is not null)
        {
            Unhide();
        }
    }

    private void Apply(Wanted? w, VirtualKind? kind, string?[] pendingUnhide)
    {
        if (kind is VirtualKind wanted && _pad is null && w?.Controller is { } controller &&
            controller.Secondary?.IsCurrent != false &&
            PendingUnhide is null && !pendingUnhide.Contains(controller.Path, StringComparer.OrdinalIgnoreCase))
        {
            Start(wanted, w, controller);
        }

        if (_pad is not null && w is not null)
        {
            CheckAudioDefaults(w.Settings.KeepAudioDefaults);
            SyncHide(w);
        }
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

        // The primary already exposes a wired DualSense on USB. Secondary modes follow their own profiles.
        if (_slotIndex == 0 && w.Mode == EmulationMode.DualSense && w.Controller.Link == Link.Usb)
        {
            return null;
        }

        return w.Mode == EmulationMode.DualSense ? VirtualKind.DualSense : VirtualKind.Xbox360;
    }

    private void Start(VirtualKind kind, Wanted w, RealController controller)
    {
        Func<bool>? primaryCurrent = null;
        if (_slotIndex == 0 && _controllers is { } controllers)
        {
            if (controllers.Slots.FirstOrDefault() is not { Index: 0 } primary || primary.Controller.DevicePath != controller.Path)
                return;

            primaryCurrent = () => ReferenceEquals(controllers.Slots.FirstOrDefault(), primary);
        }

        IUsbIpServer server = _server!;
        IVirtualPad pad;
        try
        {
            pad = _slotIndex > 0 ? server.CreateSecondaryPad(_slotIndex, kind, controller.Identity)
                : kind == VirtualKind.DualSense
                ? server.CreateDualSense(controller.Identity, w.Settings.Audio)
                : server.Create(kind, null);
        }
        catch (Exception e)
        {
            Fail($"Could not create the virtual controller: {Text.English(e)}", $"Sanal kontrolcü oluşturulamadı: {e.Message}", w);
            return;
        }

        var route = new PadRoute(pad, controller.Secondary, primaryCurrent);
        Action<PadFeedback> handler = f => OnFeedback(route, f);
        pad.Feedback += handler;
        var defaults = kind == VirtualKind.DualSense && w.Settings.Audio && w.Settings.KeepAudioDefaults
            ? SnapshotAudioDefaults() : null;
        int port;
        try
        {
            port = _platform.Vhci.Attach(server.Port, pad.BusId);
            if (port < 0)
            {
                throw new LocalizedMessage($"Invalid hub port {port}", $"geçersiz hub portu {port}").CreateException(message => new InvalidOperationException(message));
            }
        }
        catch (Exception e)
        {
            pad.Feedback -= handler;
            TryRemove(pad);
            Fail($"Could not attach the virtual controller: {Text.English(e)}", $"Sanal kontrolcü takılamadı: {e.Message}", w);
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
        _route = route;
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
            Notify($"Could not save the default audio devices: {Text.English(e)}", $"Varsayılan ses cihazları kaydedilemedi: {e.Message}");
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
                Notify($"Could not check the default audio devices: {Text.English(e)}", $"Varsayılan ses cihazları denetlenemedi: {e.Message}");
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
                Notify($"Could not restore the default audio device: {Text.English(e)}", $"Varsayılan ses cihazı geri yüklenemedi: {e.Message}");
            }
        }

        if (pending.Count == 0)
            _audioDefaultsToRestore = null;
    }

    private void SyncHide(Wanted w)
    {
        if (_retryHide)
        {
            _retryHide = false;
            _hideTried = false;
            _hideError = null;
        }

        bool want = w.Settings.HideController && Usable(DriverKind.HidHide);
        if (!want)
        {
            _hideTried = false;
            _hideError = null;
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
                HideError("Could not find the physical controller's device ID to hide it. Games may see two controllers.", "Gerçek kontrolcünün aygıt kimliği bulunamadı, gizlenemedi. Oyunlar iki kontrolcü görebilir.");
            }
            else if (_platform.Hider.Hide(id))
            {
                _hiddenId = id;
                _hiddenPath = _padPath;
            }
            else
            {
                HideError("Could not hide the physical controller. Games may see two controllers.", "Gerçek kontrolcü gizlenemedi. Oyunlar iki kontrolcü görebilir.");
            }
        }
        catch (Exception e)
        {
            HideError($"Could not hide the physical controller: {Text.English(e)}", $"Gerçek kontrolcü gizlenemedi: {e.Message}");
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
            _hiddenPath = null;
            _unhideFailNoticed = false;
            return true;
        }
        catch (Exception e)
        {
            // Kept, so the next reconcile tries again; the journal covers a crash.
            if (!_unhideFailNoticed)
            {
                _unhideFailNoticed = true;
                Notify($"Could not make the physical controller visible again: {Text.English(e)}", $"Gerçek kontrolcü yeniden görünür yapılamadı: {e.Message}");
            }

            return false;
        }
    }

    /// <summary>Unhide, detach, remove. True when every step worked (or nothing was attached).</summary>
    private bool StopLocked()
    {
        _audioDefaultsToRestore = null;
        _hideError = null;
        IVirtualPad? pad = _pad;
        if (pad is null)
        {
            return Unhide();
        }

        _pad = null;
        SecondaryEmulationConnection? secondary = _route?.Secondary;
        _route = null;
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
            Notify($"Could not remove the virtual controller: {Text.English(e)}", $"Sanal kontrolcü çıkarılamadı: {e.Message}");
        }

        TryRemove(pad);
        _padPath = null;
        _hideTried = false;

        lock (_feedbackLock)
        {
            _game = OutputState.Default;
            _gameFields = OutputFields.None;
            if (secondary is not null)
            {
                secondary.SetFeedback(null, OutputFields.None);
            }
            else
            {
                _host.SetOutputLayer(XboxLayer, null);
                _host.SetOutputLayer(GameLayer, null);
            }
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

    private void OnFeedback(PadRoute route, PadFeedback f)
    {
        lock (_feedbackLock)
        {
            if (!ReferenceEquals(route.Pad, _pad) || !route.IsCurrent)
            {
                return;
            }

            double strength = _plan.RumbleStrength;
            SecondaryEmulationConnection? secondary = route.Secondary;
            bool sent;
            switch (f)
            {
                case RumbleFeedback r:
                {
                    if (secondary is not null)
                    {
                        secondary.SetFeedback(OutputState.Default with { RumbleLeft = r.Large, RumbleRight = r.Small }, OutputFields.Rumble);
                        return;
                    }

                    byte large = GameOutput.ScaleRumble(r.Large, strength);
                    byte small = GameOutput.ScaleRumble(r.Small, strength);
                    sent = _host.SetOutputLayer(XboxLayer, o => route.IsCurrent ? o with { RumbleLeft = large, RumbleRight = small } : o);
                    break;
                }

                case DualSenseOutputFeedback d when Block(d.State) is { } block:
                {
                    _gameFields |= OutputReport.ValidFields(block);
                    _game = OutputReport.ApplyState(block, _game);
                    if (secondary is not null)
                    {
                        secondary.SetFeedback(_game, _gameFields);
                        return;
                    }

                    OutputState game = _game;
                    OutputFields fields = _gameFields;
                    sent = _host.SetOutputLayer(GameLayer, o => route.IsCurrent ? GameOutput.Overlay(o, game, fields, strength) : o);
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
            Notify("If the game shows two controllers, turn off Steam Input in its Steam settings.", "Steam Input bu oyunda açıksa çift kontrolcü görünür; oyunun Steam ayarlarında Steam Input'u kapatın.");
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

    private void Fail(string en, string tr, Wanted? w = null)
    {
        lock (_stateLock)
        {
            _error = (en, tr);
            w ??= _wanted;
            if (w is not null)
            {
                _failedFor = w.FailureKey;
            }
        }

        Notify(en, tr);
        if (_slotIndex == 0)
        {
            RaiseIfChanged();
        }
    }

    private void ClearFailureLocked()
    {
        _failedFor = null;
        _error = _server is null ? ("Could not start the virtual USB server.", "Sanal USB sunucusu başlatılamadı.") : null;
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
            Error: _error is { } error ? Text.Pick(error.En, error.Tr) : null,
            Audio: pad is IVirtualAudio audio
                ? audio.SpeakerActive ? EmulationAudioStatus.Playing : EmulationAudioStatus.Ready
                : EmulationAudioStatus.Off)
        {
            Slots = BuildSlots(new(0, mode, pad is not null, pad is not null && _hiddenId is not null,
                _error is { } slotError ? Text.Pick(slotError.En, slotError.Tr) : null)),
        };
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

    private void HideError(string en, string tr)
    {
        _hideError = new LocalizedMessage(en, tr);
        Notify(en, tr);
    }

    private void Notify(string en, string tr) => _host.Report(_slotIndex == 0
        ? new Notice(NoticeKinds.Emulation, en, tr)
        : new Notice(NoticeKinds.Emulation, $"Controller {_slotIndex + 1}. {en}", $"Kontrolcü {_slotIndex + 1}. {tr}"));

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
        public (string ProfileId, EmulationMode Mode, bool Audio, SecondaryEmulationConnection? Secondary) FailureKey =>
            (ProfileId, Mode, Mode == EmulationMode.DualSense && Settings.Audio, Controller?.Secondary);
    }

    private sealed record InputPlan(EmulationConfig Config, Buttons Hidden, double RumbleStrength, SecondaryAxes? Axes = null,
        string? ProfileId = null);
}
