using System.Diagnostics;
using System.Runtime.InteropServices;
using Tactile.Device.Hid;

namespace Tactile.Device.DualSense;

public sealed record ControllerDetails(
    ControllerModel Model,
    Link Link,
    string? MacAddress,
    FirmwareInfo? Firmware,
    ImuCalibration Calibration);

public sealed class ControllerStats
{
    public long Reports;
    public long BadCrc;
    public long Reduced;
    public long Writes;
    public long WriteErrors;
}

/// <summary>
/// One connected DualSense: a reader thread that publishes <see cref="InputState"/>,
/// and thread-safe writes of <see cref="OutputState"/> and haptic reports.
/// </summary>
public sealed class DualSenseController : IDisposable
{
    private readonly HidDevice _device;
    private readonly Thread _reader;
    private readonly Lock _writeLock = new();
    private readonly byte[] _writeBuffer;
    private volatile bool _running = true;
    private OutputState? _lastSent;
    private int _sequence;
    private bool _sentFirst;
    private volatile bool _hapticsActive;

    private DualSenseController(HidDevice device, ControllerDetails details)
    {
        _device = device;
        Details = details;
        _writeBuffer = new byte[Math.Max(device.Info.OutputReportLength, HapticReport.Length)];
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "dualsense-reader", Priority = ThreadPriority.AboveNormal };
    }

    public ControllerDetails Details { get; }

    public ControllerStats Stats { get; } = new();

    public InputState? Latest { get; private set; }

    public bool Connected => _running;

    public event Action<DualSenseController, InputState>? InputReceived;

    public event Action<DualSenseController>? Disconnected;

    public static DualSenseController Open(ControllerInfo info)
    {
        HidDevice device = HidDevice.Open(info.Hid);
        try
        {
            // Calibration first: on Bluetooth this read also switches the controller to full reports.
            byte[]? calibration = ReadFeature(device, 0x05);
            byte[]? firmware = ReadFeature(device, 0x20);
            byte[]? pairing = ReadFeature(device, 0x09);

            var details = new ControllerDetails(
                info.Model,
                info.Link,
                pairing is null ? null : ParseMac(pairing),
                firmware is null ? null : FirmwareInfo.Parse(firmware),
                calibration is null ? ImuCalibration.Identity : ImuCalibration.Parse(calibration));

            var controller = new DualSenseController(device, details);
            controller._reader.Start();
            return controller;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>Sends the state if it differs from what was last sent.</summary>
    public bool Send(OutputState state, bool force = false)
    {
        lock (_writeLock)
        {
            if (!force && state.Equals(_lastSent))
            {
                return false;
            }

            OutputOptions options = CurrentOptions();

            byte[] report = Details.Link == Link.Usb
                ? OutputReport.BuildUsb(state, options)
                : OutputReport.BuildBluetooth(state, _sequence++, options);

            if (Write(report))
            {
                _lastSent = state;
                _sentFirst = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>Options matching the controller and the current haptic stream state.</summary>
    public OutputOptions CurrentOptions() => new(
        FirstReport: !_sentFirst,
        RumbleV2: Details.Model == ControllerModel.DualSenseEdge || (Details.Firmware?.UpdateVersion ?? 0) >= OutputReport.RumbleV2UpdateVersion,
        Rumble: !_hapticsActive,
        AudioStream: _hapticsActive);

    /// <summary>The state most recently written, or the default before the first write.</summary>
    public OutputState LastSent => _lastSent ?? OutputState.Default;

    /// <summary>Raw report write (e.g. a prebuilt haptic report). Bluetooth only for 0x32.</summary>
    public bool WriteRaw(ReadOnlySpan<byte> report)
    {
        lock (_writeLock)
        {
            return Write(report);
        }
    }

    /// <summary>While true, the main report stops driving rumble so streamed haptics are not overridden.</summary>
    public bool HapticsActive
    {
        get => _hapticsActive;
        set
        {
            if (_hapticsActive == value)
            {
                return;
            }

            _hapticsActive = value;
            Send(LastSent, force: true);
        }
    }

    public void Dispose()
    {
        if (!_running)
        {
            _device.Dispose();
            return;
        }

        _running = false;
        try
        {
            // Leave the controller in a neutral state: triggers free, motors off.
            Send(OutputState.Default, force: true);
        }
        catch (IOException)
        {
        }

        _device.Dispose();
        _reader.Join(TimeSpan.FromSeconds(1));
    }

    private bool Write(ReadOnlySpan<byte> report)
    {
        // Windows wants the full capability length; the driver sends the report's real size.
        Span<byte> buffer = _writeBuffer;
        buffer.Clear();
        report.CopyTo(buffer);
        try
        {
            _device.Write(buffer[..Math.Max(report.Length, _device.Info.OutputReportLength)]);
            Interlocked.Increment(ref Stats.Writes);
            return true;
        }
        catch (IOException)
        {
            Interlocked.Increment(ref Stats.WriteErrors);
            return false;
        }
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[_device.Info.InputReportLength];
        while (_running)
        {
            int read;
            try
            {
                read = _device.Read(buffer);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                continue;
            }

            switch (InputParser.TryParse(buffer.AsSpan(0, read), Details.Link, out InputState? state))
            {
                case ParseResult.Ok:
                    Stats.Reports++;
                    Latest = state;
                    InputReceived?.Invoke(this, state!);
                    break;
                case ParseResult.BadCrc:
                    Stats.BadCrc++;
                    break;
                case ParseResult.ReducedReport:
                    Stats.Reduced++;
                    break;
            }
        }

        bool wasRunning = _running;
        _running = false;
        if (wasRunning)
        {
            Disconnected?.Invoke(this);
        }
    }

    private static byte[]? ReadFeature(HidDevice device, byte id)
    {
        byte[] buffer = new byte[device.Info.FeatureReportLength];
        buffer[0] = id;
        return device.TryGetFeature(buffer) ? buffer : null;
    }

    private static string? ParseMac(byte[] pairing)
    {
        if (pairing.Length < 7 || pairing[0] != 0x09)
        {
            return null;
        }

        // Stored least significant byte first.
        return string.Join(':', Enumerable.Range(1, 6).Reverse().Select(i => pairing[i].ToString("X2")));
    }
}

/// <summary>Streams 0x32 haptic reports on a fixed 10.667 ms clock from a sample source.</summary>
public sealed class HapticStreamer : IDisposable
{
    private readonly DualSenseController _controller;
    private readonly Func<int, sbyte[]> _nextBlock;
    private readonly HapticReport _report = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    private const int PrerollBlocks = 8;

    /// <param name="nextBlock">Called with the block index; returns 64 interleaved s8 samples.</param>
    public HapticStreamer(DualSenseController controller, Func<int, sbyte[]> nextBlock)
    {
        if (controller.Details.Link != Link.Bluetooth)
        {
            throw new InvalidOperationException("Haptic reports are Bluetooth only; over USB haptics are an audio stream.");
        }

        _controller = controller;
        _nextBlock = nextBlock;
        _thread = new Thread(Run) { IsBackground = true, Name = "dualsense-haptics", Priority = ThreadPriority.Highest };
        _controller.HapticsActive = true;
        _controller.WriteRaw(_report.BuildPrime(_controller.LastSent, _controller.CurrentOptions()));
        _thread.Start();
    }

    public int Sent { get; private set; }

    public void Dispose()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(1));
        _controller.HapticsActive = false;
    }

    private void Run()
    {
        TimerResolution.Begin();
        try
        {
            var clock = Stopwatch.StartNew();
            double intervalMs = HapticReport.Interval.TotalMilliseconds;
            var silence = new sbyte[HapticReport.PayloadLength];
            for (int block = -PrerollBlocks; _running && _controller.Connected; block++)
            {
                // A few silent blocks first so the controller's buffer is filled before real samples.
                if (!_controller.WriteRaw(_report.Build(block < 0 ? silence : _nextBlock(block))))
                {
                    break;
                }

                Sent++;
                double due = (block + PrerollBlocks + 1) * intervalMs;
                while (_running && clock.Elapsed.TotalMilliseconds < due - 1.5)
                {
                    Thread.Sleep(1);
                }

                while (clock.Elapsed.TotalMilliseconds < due)
                {
                    Thread.SpinWait(50);
                }
            }
        }
        finally
        {
            TimerResolution.End();
        }
    }

    /// <summary>Test signal: a sine burst on one or both actuators.</summary>
    public static Func<int, sbyte[]> Tone(float frequencyHz, float amplitude, bool left = true, bool right = true, int blocks = int.MaxValue)
    {
        amplitude = Math.Clamp(amplitude, 0f, 1f);
        return block =>
        {
            var samples = new sbyte[HapticReport.PayloadLength];
            if (block >= blocks)
            {
                return samples;
            }

            for (int f = 0; f < HapticReport.FramesPerReport; f++)
            {
                double t = (block * HapticReport.FramesPerReport + f) / (double)HapticReport.SampleRate;
                sbyte v = (sbyte)Math.Round(Math.Sin(2 * Math.PI * frequencyHz * t) * 127 * amplitude);
                samples[f * 2] = left ? v : (sbyte)0;
                samples[f * 2 + 1] = right ? v : (sbyte)0;
            }

            return samples;
        };
    }
}

public static partial class TimerResolution
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    public static void Begin() => TimeBeginPeriod(1);

    public static void End() => TimeEndPeriod(1);
}
