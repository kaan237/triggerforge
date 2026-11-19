using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>
/// Keeps one controller connected: scans for it, reopens after a disconnect, and owns the
/// output state the rest of the app edits.
/// </summary>
public sealed class ControllerService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1.5);

    private readonly Lock _lock = new();
    private readonly Lock _scanLock = new();
    private readonly Timer _scan;
    private volatile DualSenseController? _controller;
    private HapticStreamer? _haptics;
    private long _hapticsStarted;
    private Timer? _hapticStop;
    private volatile HapticBlockSource? _hapticSource;
    internal SpeakerFrameSource? SpeakerSource { get; set; }
    private volatile HapticBlockSource? _tone;
    private bool _disposed;
    private readonly Func<string, bool>? _isVirtual;
    private volatile OutputState _output = OutputState.Default;

    public ControllerService()
        : this(autoConnect: true)
    {
    }

    /// <param name="autoConnect">False never looks for hardware; outputs are only stored (tests).</param>
    /// <param name="isVirtual">True for HID paths of our own virtual pads, which the scan must skip.</param>
    public ControllerService(bool autoConnect, Func<string, bool>? isVirtual = null)
    {
        _isVirtual = isVirtual;
        _scan = autoConnect
            ? new Timer(_ => ScanIfIdle(), null, TimeSpan.Zero, ScanInterval)
            : new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public DualSenseController? Controller => _controller;

    public OutputState Output => _output;

    public event Action<DualSenseController?>? ConnectionChanged;

    public event Action<InputState>? Input;

    public void SetOutput(Func<OutputState, OutputState> change)
    {
        lock (_lock)
        {
            _output = change(_output);
            _controller?.Send(_output);
        }
    }

    internal bool SetHapticSource(HapticBlockSource? source)
    {
        lock (_lock)
        {
            _hapticSource = source;
            return ReconcileHapticsLocked();
        }
    }

    /// <summary>Plays a test tone on the haptic actuators (Bluetooth only).</summary>
    public string? PlayHapticTone(float frequencyHz, float amplitude, bool left, bool right, int milliseconds)
    {
        lock (_lock)
        {
            DualSenseController? c = _controller;
            if (c is not { Connected: true })
            {
                return "Kontrolcü bağlı değil.";
            }

            if (c.Details.Link != Link.Bluetooth)
            {
                return "Haptik test şimdilik yalnız Bluetooth'ta.";
            }

            ClearToneLocked();
            int blocks = (int)Math.Ceiling(milliseconds / HapticReport.Interval.TotalMilliseconds);
            HapticBlockSource tone = HapticStreamer.Tone(frequencyHz, amplitude, left, right, blocks);
            int firstBlock = -1;
            HapticBlockSource playback = (block, samples) =>
            {
                if (firstBlock < 0)
                {
                    firstBlock = block;
                }

                tone(block - firstBlock, samples);
            };
            _tone = playback;
            ReconcileHapticsLocked();
            // Keep streaming silence briefly so the controller buffer drains, then stop.
            _hapticStop = new Timer(_ => FinishTone(playback), null, milliseconds + 300, Timeout.Infinite);
            return null;
        }
    }

    public void StopHaptics()
    {
        lock (_lock)
        {
            ClearToneLocked();
            ReconcileHapticsLocked();
        }
    }

    public void Dispose()
    {
        _scan.Dispose();
        DualSenseController? controller;
        lock (_lock)
        {
            _disposed = true;
            ClearToneLocked();
            _hapticSource = null;
            StopHapticsLocked();
            controller = _controller;
            _controller = null;
        }

        controller?.Dispose();
    }

    private void FinishTone(HapticBlockSource playback)
    {
        lock (_lock)
        {
            if (_tone == playback)
            {
                ClearToneLocked();
                ReconcileHapticsLocked();
            }
        }
    }

    private void ClearToneLocked()
    {
        _tone = null;
        _hapticStop?.Dispose();
        _hapticStop = null;
    }

    private bool ReconcileHapticsLocked()
    {
        if (_disposed || _controller is not { Connected: true, Details.Link: Link.Bluetooth } controller ||
            (_hapticSource is null && _tone is null))
        {
            StopHapticsLocked();
            return false;
        }

        if (_haptics is not { Running: true })
        {
            // A streamer that stopped on a failed write is retried at most once a second.
            if (_haptics is not null && Environment.TickCount64 - _hapticsStarted < 1000)
            {
                return false;
            }

            StopHapticsLocked();
            _hapticsStarted = Environment.TickCount64;
            _haptics = new HapticStreamer(controller, FillHaptics, FillSpeaker);
        }

        return _haptics.Running;
    }

    private void FillHaptics(int block, Span<sbyte> samples)
    {
        HapticBlockSource? source = _hapticSource;
        if (source is null)
        {
            samples.Clear();
        }
        else
        {
            source(block, samples);
        }

        _tone?.Invoke(block, samples);
    }

    private bool FillSpeaker(int block, Span<byte> frame) => SpeakerSource?.Invoke(block, frame) ?? false;

    private void StopHapticsLocked()
    {
        _haptics?.Dispose();
        _haptics = null;
    }

    private void ScanIfIdle()
    {
        if (!_scanLock.TryEnter())
        {
            return;
        }

        try
        {
            Scan();
        }
        finally
        {
            _scanLock.Exit();
        }
    }

    private void Scan()
    {
        lock (_lock)
        {
            if (_disposed || _controller is not null)
            {
                return;
            }
        }

        ControllerInfo? info = DualSenseIds.FindControllers().FirstOrDefault(c => !IsVirtual(c.Hid.Path));
        if (info is null)
        {
            return;
        }

        DualSenseController controller;
        try
        {
            controller = DualSenseController.Open(info);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        bool accepted;
        lock (_lock)
        {
            accepted = !_disposed && _controller is null && controller.Connected;
            if (accepted)
            {
                _controller = controller;
                controller.InputReceived += OnInput;
                controller.Disconnected += OnDisconnected;
                controller.Send(_output, force: true);
                ReconcileHapticsLocked();
            }
        }

        if (!accepted)
        {
            controller.Dispose();
        }
        else if (controller.Connected)
        {
            ConnectionChanged?.Invoke(controller);
        }
        else
        {
            OnDisconnected(controller);
        }
    }

    internal bool IsVirtual(string path)
    {
        if (_isVirtual is null)
        {
            return false;
        }

        try
        {
            return _isVirtual(path);
        }
        catch (Exception)
        {
            // A device-tree query that fails says nothing; treat the pad as real.
            return false;
        }
    }

    /// <summary>Like <see cref="SetOutput"/>, but true only when the controller write itself went out.</summary>
    internal bool SetOutputAndSend(OutputState state)
    {
        lock (_lock)
        {
            _output = state;
            return _controller?.Send(_output) ?? false;
        }
    }

    private void OnInput(DualSenseController controller, InputState state)
    {
        if (_controller == controller)
        {
            Input?.Invoke(state);
        }
    }

    private void OnDisconnected(DualSenseController c)
    {
        lock (_lock)
        {
            if (_controller != c)
            {
                return;
            }

            ClearToneLocked();
            StopHapticsLocked();
            _controller = null;
        }

        c.Dispose();
        ConnectionChanged?.Invoke(null);
    }
}
