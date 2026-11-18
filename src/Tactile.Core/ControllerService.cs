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
    private readonly Timer _scan;
    private DualSenseController? _controller;
    private HapticStreamer? _haptics;
    private Timer? _hapticStop;
    private OutputState _output = OutputState.Default;

    public ControllerService()
    {
        _scan = new Timer(_ => Scan(), null, TimeSpan.Zero, ScanInterval);
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

    /// <summary>Plays a test tone on the haptic actuators (Bluetooth only).</summary>
    public string? PlayHapticTone(float frequencyHz, float amplitude, bool left, bool right, int milliseconds)
    {
        lock (_lock)
        {
            DualSenseController? c = _controller;
            if (c is null)
            {
                return "Kontrolcü bağlı değil.";
            }

            if (c.Details.Link != Link.Bluetooth)
            {
                return "Haptik test şimdilik yalnız Bluetooth'ta.";
            }

            StopHapticsLocked();
            int blocks = (int)Math.Ceiling(milliseconds / HapticReport.Interval.TotalMilliseconds);
            _haptics = new HapticStreamer(c, HapticStreamer.Tone(frequencyHz, amplitude, left, right, blocks));
            // Keep streaming silence briefly so the controller buffer drains, then stop.
            _hapticStop = new Timer(_ => StopHaptics(), null, milliseconds + 300, Timeout.Infinite);
            return null;
        }
    }

    public void StopHaptics()
    {
        lock (_lock)
        {
            StopHapticsLocked();
        }
    }

    public void Dispose()
    {
        _scan.Dispose();
        lock (_lock)
        {
            StopHapticsLocked();
            _controller?.Dispose();
            _controller = null;
        }
    }

    private void StopHapticsLocked()
    {
        _hapticStop?.Dispose();
        _hapticStop = null;
        _haptics?.Dispose();
        _haptics = null;
    }

    private void Scan()
    {
        lock (_lock)
        {
            if (_controller is { Connected: true })
            {
                return;
            }
        }

        ControllerInfo? info = DualSenseIds.FindControllers().FirstOrDefault();
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

        controller.InputReceived += (_, state) => Input?.Invoke(state);
        controller.Disconnected += OnDisconnected;
        lock (_lock)
        {
            _controller = controller;
            controller.Send(_output, force: true);
        }

        ConnectionChanged?.Invoke(controller);
    }

    private void OnDisconnected(DualSenseController c)
    {
        lock (_lock)
        {
            if (_controller != c)
            {
                return;
            }

            StopHapticsLocked();
            _controller = null;
        }

        c.Dispose();
        ConnectionChanged?.Invoke(null);
    }
}
