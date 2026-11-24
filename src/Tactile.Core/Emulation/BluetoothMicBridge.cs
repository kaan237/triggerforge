using Tactile.Audio;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Core;

/// <summary>
/// Carries the primary Bluetooth DualSense microphone to the virtual pad's microphone, only while an app records.
/// Everything that touches the controller (opening, closing, the mute light) runs on this class's own worker, or on
/// <see cref="Pump"/> when there is none (tests); the events and readers it hooks only record a wish or move samples.
/// </summary>
internal sealed class BluetoothMicBridge : IDisposable
{
    // 50 ms only while a session runs (mute and the light follow the report); otherwise a slow poll plus signals.
    private const int SessionPollMs = 50;
    private const int IdlePollMs = 1000;
    private const int RetryDelayMs = 1000;

    private readonly ControllerService _controllers;
    private readonly Func<EmulationSettings> _settings;
    private readonly Func<IVirtualMic?> _virtualMic;
    private readonly Func<string, Func<OutputState, OutputState>?, bool> _setLayer;
    private readonly Action<Notice> _report;
    private readonly Action _changed;
    private readonly Func<IMicDecoder>? _decoderFactory;
    private readonly Func<long> _tickMs;
    private readonly MicAudioReader _reader;
    private readonly Action<bool> _onActive;
    private readonly PrimaryMicFrameHandler _onFrame;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _worker;
    // Serialises opening and closing; nothing else is taken while it is held except the layer and controller calls.
    private readonly Lock _work = new();
    private readonly Lock _toggleLock = new();
    private volatile BluetoothMicBuffer? _buffer;
    private volatile bool _muted;
    private volatile bool _toggled;
    private volatile bool _disposed;
    // True from the start of a close until the next open is set up: the ISO reader outputs silence.
    private volatile bool _closing;
    private int _disposeState;
    // Read by the ISO thread (mute) as well as the worker.
    private volatile IVirtualMic? _session;
    private volatile DualSenseController? _sessionController;
    private IVirtualMic? _subscribed;
    private MicLed? _ledState;
    private volatile bool _unavailable;
    private bool _noticed;
    private long _retryAt;
    private long _offRetryAt;
    // Release(): the controller that was asked to sleep stays closed until the time is up; guarded by _work.
    private DualSenseController? _holdController;
    private long _holdUntil;
    // The controller the toggle belongs to; guarded by _toggleLock.
    private DualSenseController? _toggleController;
    private readonly Action<DualSenseController?> _onConnection;
    private int _evaluations;
    private EmulationMicStatus _lastStatus;

    /// <param name="decoderFactory">Makes one decoder per recording session; null uses the Opus library.</param>
    /// <param name="threaded">False leaves the work to <see cref="Pump"/> (tests).</param>
    /// <param name="tickMs">Millisecond clock for the retry delay; null uses the system tick count.</param>
    public BluetoothMicBridge(ControllerService controllers, Func<EmulationSettings> settings, Func<IVirtualMic?> virtualMic,
        Func<string, Func<OutputState, OutputState>?, bool> setLayer, Action<Notice> report, Action changed,
        Func<IMicDecoder>? decoderFactory = null, bool threaded = true, Func<long>? tickMs = null)
    {
        _controllers = controllers;
        _settings = settings;
        _virtualMic = virtualMic;
        _setLayer = setLayer;
        _report = report;
        _changed = changed;
        _decoderFactory = decoderFactory;
        _tickMs = tickMs ?? (() => Environment.TickCount64);
        _reader = Read;
        _onActive = _ => Signal();
        _onFrame = OnFrame;
        _onConnection = _ => Signal();
        _controllers.PrimaryMicFrame += _onFrame;
        _controllers.ConnectionChanged += _onConnection;
        if (threaded)
        {
            _worker = new Thread(Run) { IsBackground = true, Name = "TriggerForge: mic-bridge" };
            _worker.Start();
        }
    }

    /// <summary>Whether an app records right now and the controller side is open.</summary>
    internal bool SessionOpen => _buffer is not null;

    /// <summary>Mono samples waiting for the reader.</summary>
    internal int BufferedSamples => _buffer?.BufferedSamples ?? 0;

    /// <summary>The effective mute: the button toggle, the report's own bit or the Windows mute.</summary>
    internal bool Muted => _muted;

    /// <summary>How many times the worker (or <see cref="Pump"/>) looked at the state; a test hook.</summary>
    internal int Evaluations => Volatile.Read(ref _evaluations);

    /// <summary>Whether the bridge listens to the virtual mic's active events; a test hook.</summary>
    internal bool Listening => Volatile.Read(ref _subscribed) is not null;

    public EmulationMicStatus Status
    {
        get
        {
            if (_unavailable || !Eligible(out IVirtualMic? mic, out DualSenseController? controller))
            {
                return EmulationMicStatus.Off;
            }

            // Active only once the controller side really is open.
            if (!mic.MicActive || !SessionOpen)
            {
                return EmulationMicStatus.Ready;
            }

            return IsMuted(mic, controller) ? EmulationMicStatus.Muted : EmulationMicStatus.Active;
        }
    }

    /// <summary>The physical mic button was pressed (rising edge of the raw input). Only records the toggle.</summary>
    public void OnMicButton()
    {
        if (_disposed || _unavailable || !Eligible(out _, out DualSenseController? controller))
        {
            return;
        }

        lock (_toggleLock)
        {
            if (!ReferenceEquals(_toggleController, controller))
            {
                // A press belongs to one controller; another one starts unmuted.
                _toggleController = controller;
                _toggled = false;
            }

            _toggled = !_toggled;
        }

        Signal();
    }

    /// <summary>Something the bridge looks at changed (setting, controller); evaluated soon.</summary>
    public void Poke() => Signal();

    /// <summary>Does the pending work on the calling thread when the bridge has no worker.</summary>
    public void Pump()
    {
        if (_worker is null)
        {
            Evaluate();
        }
    }

    /// <summary>
    /// Closes the session now (the controller with this address is about to be switched off) and holds off reopening
    /// on that controller only.
    /// </summary>
    public void Release(string? mac)
    {
        lock (_work)
        {
            DualSenseController? target = _sessionController ?? _controllers.Controller;
            if (target is null || mac is null || !string.Equals(target.Details.MacAddress, mac, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _holdController = target;
            _holdUntil = _tickMs() + 10 * RetryDelayMs;
            if (ReferenceEquals(_sessionController, target))
            {
                CloseLocked();
            }
        }

        RaiseStatusChange();
        Signal();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _disposed = true;
        _controllers.PrimaryMicFrame -= _onFrame;
        _controllers.ConnectionChanged -= _onConnection;
        _wake.Set();
        if (_worker is not null && _worker != Thread.CurrentThread)
        {
            _worker.Join(TimeSpan.FromSeconds(2));
        }

        lock (_work)
        {
            Unsubscribe();
            CloseLocked();
        }

        _wake.Dispose();
    }

    private void Signal()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile.
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            try
            {
                int wait = Evaluate();
                if (!_disposed)
                {
                    _wake.WaitOne(wait);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // The next pass starts clean; the bridge never takes the process down.
                Thread.Sleep(SessionPollMs);
            }
        }
    }

    private bool Eligible(out IVirtualMic mic, out DualSenseController controller)
    {
        EmulationSettings settings = _settings();
        IVirtualMic? candidate = _virtualMic();
        DualSenseController? primary = _controllers.Controller;
        if (settings.BluetoothMic && settings.Audio && candidate is not null &&
            primary is { Connected: true, Details: { Link: Link.Bluetooth, Model: not ControllerModel.DualShock4 } })
        {
            mic = candidate;
            controller = primary;
            return true;
        }

        mic = null!;
        controller = null!;
        return false;
    }

    private bool IsMuted(IVirtualMic mic, DualSenseController controller) =>
        _toggled || controller.Latest?.MicMuted == true || mic.MicMuted;

    /// <returns>How long to wait before looking again; infinite while the feature is off.</returns>
    private int Evaluate()
    {
        int wait;
        lock (_work)
        {
            Interlocked.Increment(ref _evaluations);
            long now = _tickMs();
            if (_disposed)
            {
                CloseLocked();
                return Timeout.Infinite;
            }

            bool enabled = _settings().BluetoothMic;
            bool eligible = Eligible(out IVirtualMic? mic, out DualSenseController? controller);
            // With the feature off nothing is listened to and the worker sleeps until a signal.
            Subscribe(enabled ? _virtualMic() : null);
            bool wanted = eligible && mic!.MicActive;
            if (_buffer is not null && (!wanted || !ReferenceEquals(_session, mic) || !ReferenceEquals(_sessionController, controller)))
            {
                CloseLocked();
            }

            if (!wanted)
            {
                _unavailable = false;
            }

            lock (_toggleLock)
            {
                // No pad, setting or controller to listen to, or another controller: a press left over does not carry on.
                DualSenseController? owner = eligible ? controller : null;
                if (!ReferenceEquals(_toggleController, owner))
                {
                    _toggleController = owner;
                    _toggled = false;
                }
            }

            if (_holdController is not null && (now >= _holdUntil || !_holdController.Connected))
            {
                _holdController = null;
            }

            bool held = eligible && ReferenceEquals(_holdController, controller);
            if (_controllers.MicOffPending && now >= _offRetryAt && !_controllers.ConfirmMicOff())
            {
                // A failed off is written again while the controller stays connected.
                _offRetryAt = now + RetryDelayMs;
            }

            if (wanted && _buffer is null && !_unavailable && !held && now >= _retryAt)
            {
                OpenLocked(mic!, controller!);
            }

            if (_buffer is not null)
            {
                bool muted = IsMuted(mic!, controller!);
                _muted = muted;
                SetLed(muted ? MicLed.On : MicLed.Off);
            }

            long untilRetry = Math.Max(_retryAt, held ? _holdUntil : 0) - _tickMs();
            wait = !enabled ? Timeout.Infinite
                : _buffer is not null ? SessionPollMs
                : wanted && !_unavailable && untilRetry > 0 ? (int)Math.Clamp(untilRetry, 1, IdlePollMs)
                : IdlePollMs;
            if (_controllers.MicOffPending)
            {
                // An unconfirmed off is a reason to wake up even with the setting off.
                int untilOff = (int)Math.Clamp(_offRetryAt - _tickMs(), 1, RetryDelayMs);
                wait = wait == Timeout.Infinite ? untilOff : Math.Min(wait, untilOff);
            }
        }

        RaiseStatusChange();
        return wait;
    }

    private void RaiseStatusChange()
    {
        EmulationMicStatus status = Status;
        if (status != _lastStatus)
        {
            _lastStatus = status;
            try
            {
                _changed();
            }
            catch (Exception)
            {
                // A status listener must not stop the bridge.
            }
        }
    }

    private void OpenLocked(IVirtualMic mic, DualSenseController controller)
    {
        BluetoothMicBuffer buffer;
        try
        {
            // A new decoder for every session: nothing of an earlier recording is left in it.
            buffer = _decoderFactory is null ? new BluetoothMicBuffer() : new BluetoothMicBuffer(_decoderFactory());
        }
        catch (AudioUnavailableException e)
        {
            _unavailable = true;
            if (!_noticed)
            {
                _noticed = true;
                _report(new Notice(NoticeKinds.Emulation,
                    $"The controller microphone is unavailable: {e.Translation.En}",
                    $"Kontrolcü mikrofonu kullanılamıyor: {e.Translation.Tr}"));
            }

            return;
        }

        _session = mic;
        _sessionController = controller;
        _muted = IsMuted(mic, controller);
        _closing = false;
        _buffer = buffer;
        mic.SetMicReader(_reader);
        if (!_controllers.SetMicStream(true))
        {
            // The controller did not take it: close cleanly and try again in a second.
            _retryAt = _tickMs() + RetryDelayMs;
            CloseLocked();
        }
    }

    private void CloseLocked()
    {
        IVirtualMic? mic = _session;
        BluetoothMicBuffer? buffer = _buffer;
        if (mic is null && buffer is null)
        {
            return;
        }

        // Silence first: from here the ISO reader outputs zeros whatever the session fields say.
        _closing = true;
        // A press from now on belongs to the next session; it is cleared before the writes so none is lost.
        lock (_toggleLock)
        {
            _toggled = false;
        }

        _session = null;
        _sessionController = null;
        // Reader first, so the scheduler stops calling in before the buffer is wiped.
        try
        {
            mic?.SetMicReader(null);
        }
        catch (Exception)
        {
            // The pad may be gone already.
        }

        _buffer = null;
        buffer?.Dispose();
        _muted = false;
        SetLed(null);
        if (!_controllers.SetMicStream(false))
        {
            _offRetryAt = _tickMs() + RetryDelayMs;
        }
    }

    /// <summary>While a session runs the light is forced (on when muted, off otherwise); null hands it back.</summary>
    private void SetLed(MicLed? state)
    {
        if (_ledState == state)
        {
            return;
        }

        _ledState = state;
        try
        {
            _setLayer(IntegrationLayers.MicLed, state is { } forced ? o => o with { MicLed = forced } : null);
        }
        catch (Exception)
        {
            // The light is cosmetic.
        }
    }

    private void Subscribe(IVirtualMic? mic)
    {
        if (ReferenceEquals(mic, _subscribed))
        {
            return;
        }

        Unsubscribe();
        if (mic is not null)
        {
            mic.MicActiveChanged += _onActive;
            _subscribed = mic;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is { } old)
        {
            old.MicActiveChanged -= _onActive;
            _subscribed = null;
        }
    }

    private void OnFrame(byte sequence, ReadOnlySpan<byte> opus71)
    {
        // Runs on the controller's reader: decode and queue only.
        try
        {
            _buffer?.Push(sequence, opus71);
        }
        catch (Exception)
        {
            // Never throws into the reader.
        }
    }

    private bool ReadMuted() =>
        _closing || _session is null || _toggled || _sessionController?.Latest?.MicMuted == true || _session?.MicMuted == true;

    private void Read(Span<byte> stereoS16Le)
    {
        // Runs on the ISO scheduler: drains the buffer even while muted so an unmute starts fresh. The mute is
        // worked out here from lock-free state, so it never waits for the worker.
        BluetoothMicBuffer? buffer = _buffer;
        if (buffer is not null)
        {
            buffer.ReadStereoS16(stereoS16Le, ReadMuted());
        }
    }
}
