using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    private readonly HidDevice? _device;
    private readonly Thread? _reader;
    private readonly Action<OutputState>? _observeOutput;
    private readonly Lock _writeLock = new();
    private readonly byte[] _writeBuffer;
    private volatile bool _running = true;
    private volatile OutputState? _lastSent;
    private volatile InputState? _latest;
    private int _sequence;
    private bool _sentFirst;
    private volatile bool _hapticsActive;
    private readonly bool _dongle;

    private DualSenseController(HidDevice device, ControllerDetails details, UsbFeatureReports features, bool dongle = false)
    {
        _device = device;
        DevicePath = device.Info.Path;
        Details = details;
        Features = features;
        _dongle = dongle;
        _writeBuffer = new byte[Math.Max(device.Info.OutputReportLength, HapticReport.SpeakerDoubleLength)];
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "TriggerForge: dualsense-reader", Priority = ThreadPriority.AboveNormal };
    }

    internal DualSenseController(string path, ControllerDetails details, Action<OutputState> observeOutput)
    {
        DevicePath = path;
        Details = details;
        Features = new(null, null, null);
        _observeOutput = observeOutput;
        _writeBuffer = [];
    }

    public ControllerDetails Details { get; private set; }

    /// <summary>Feature reports read at open, in USB form (what a virtual DualSense answers with).</summary>
    public UsbFeatureReports Features { get; }

    /// <summary>HID device path this controller was opened from.</summary>
    public string DevicePath { get; }

    public ControllerStats Stats { get; } = new();

    public InputState? Latest => _latest;

    public bool Connected => _running;

    public event Action<DualSenseController, InputState>? InputReceived;

    public event Action<DualSenseController>? Disconnected;

    public static DualSenseController Open(ControllerInfo info)
    {
        if (info.Model == ControllerModel.DualShock4)
        {
            return OpenDs4(info);
        }

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

            var features = new UsbFeatureReports(
                calibration is null ? null : FeatureReport.ToUsb(calibration, info.Link),
                pairing is null ? null : FeatureReport.ToUsb(pairing, info.Link),
                firmware is null ? null : FeatureReport.ToUsb(firmware, info.Link));
            var controller = new DualSenseController(device, details, features);
            controller._reader!.Start();
            return controller;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static DualSenseController OpenDs4(ControllerInfo info)
    {
        bool dongle = info.Hid.ProductId == DualSenseIds.DualShock4DongleProductId;
        bool grouped = info.Link == Link.Bluetooth || dongle;
        HidDevice device = HidDevice.Open(info.Hid);
        try
        {
            // On Bluetooth the 0x05 read also switches the controller from reduced to full reports.
            byte[]? calibration = ReadDs4Calibration(device, info.Link, dongle);
            bool ready = calibration is not null && !ImuCalibration.IsBlank(calibration);
            string? mac = info.Link == Link.Usb ? ReadDs4UsbMac(device) : ReadDs4BluetoothMac(device, info.Hid.Path);

            var details = new ControllerDetails(
                info.Model,
                info.Link,
                mac,
                null,
                ready ? ImuCalibration.ParseDualShock4(calibration, grouped) : ImuCalibration.DualShock4Nominal);
            var controller = new DualSenseController(device, details, UsbFeatureReports.None, dongle);
            if (info.Link == Link.Bluetooth)
            {
                controller.WriteRaw(Ds4OutputReport.BuildBluetooth(OutputState.Default));
            }

            controller._reader!.Start();
            return controller;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static byte[]? ReadDs4Calibration(HidDevice device, Link link, bool dongle)
    {
        byte id = link == Link.Bluetooth ? (byte)0x05 : (byte)0x02;
        byte[]? feature = null;
        for (int attempt = 0; attempt < (dongle ? 5 : 1); attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(2);
            }

            feature = ReadFeature(device, id);
            if (feature is not null && !ImuCalibration.IsBlank(feature))
            {
                break;
            }
        }

        return feature;
    }

    private static string? ReadDs4UsbMac(HidDevice device)
    {
        foreach (byte id in new byte[] { 0x12, 0x81 })
        {
            if (ReadFeature(device, id) is { } feature && ParseMac(feature, id) is { } mac)
            {
                return mac;
            }
        }

        return null;
    }

    private static string? MacFromPath(string path)
    {
        Match m = BluetoothAddressInPath.Match(path);
        return m.Success ? FormatMac(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// The address from a HID serial string: 12 hex digits, with or without ':' or '-' separators.
    /// All zero or anything else gives null.
    /// </summary>
    internal static string? MacFromSerial(string? serial)
    {
        if (serial is null)
        {
            return null;
        }

        string digits = serial.Trim().Replace(":", "").Replace("-", "");
        if (digits.Length != 12 || !digits.All(char.IsAsciiHexDigit) || digits.All(c => c == '0'))
        {
            return null;
        }

        return FormatMac(digits);
    }

    private static string FormatMac(string twelveHexDigits) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => twelveHexDigits.Substring(i * 2, 2).ToUpperInvariant()));

    /// <summary>On Bluetooth the HID child path has no address; the serial string does.</summary>
    private static string? ReadDs4BluetoothMac(HidDevice device, string path) =>
        MacFromSerial(device.TryGetSerial()) ?? MacFromPath(path);

    private static readonly Regex BluetoothAddressInPath = new("_([0-9a-fA-F]{12})(?=[&#_]|$)", RegexOptions.Compiled);

    public static string? ReadMac(ControllerInfo info)
    {
        if (info.Model == ControllerModel.DualShock4)
        {
            using HidDevice ds4 = HidDevice.Open(info.Hid);
            return info.Link == Link.Bluetooth ? ReadDs4BluetoothMac(ds4, info.Hid.Path) : ReadDs4UsbMac(ds4);
        }

        using HidDevice device = HidDevice.Open(info.Hid);
        return ReadFeature(device, 0x09) is { } pairing ? ParseMac(pairing) : null;
    }

    /// <summary>Sends the state if it differs from what was last sent.</summary>
    public bool Send(OutputState state, bool force = false) => Send(state, force, null);

    /// <summary>Checks that the caller still owns the output while holding this device's write lock.</summary>
    public bool SendIfCurrent(OutputState state, Func<bool> isCurrent) => Send(state, false, isCurrent);

    private bool Send(OutputState state, bool force, Func<bool>? isCurrent)
    {
        lock (_writeLock)
        {
            bool ds4 = Details.Model == ControllerModel.DualShock4;
            bool same = ds4 ? Ds4OutputReport.SameEffect(state, _lastSent) : state.Equals(_lastSent);
            if ((isCurrent is not null && !isCurrent()) || (!force && same))
            {
                return false;
            }

            if (_observeOutput is not null)
            {
                _observeOutput(state);
                _lastSent = state;
                return true;
            }

            OutputOptions options = CurrentOptions();

            byte[] report = ds4
                ? (Details.Link == Link.Usb ? Ds4OutputReport.BuildUsb(state) : Ds4OutputReport.BuildBluetooth(state))
                : Details.Link == Link.Usb
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
            lock (_writeLock)
            {
                if (_hapticsActive == value)
                {
                    return;
                }

                _hapticsActive = value;
                if (Connected)
                {
                    Send(LastSent, force: true);
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_running)
        {
            _device?.Dispose();
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

        _device?.Dispose();
        if (_reader != Thread.CurrentThread)
        {
            _reader?.Join(TimeSpan.FromSeconds(1));
        }
    }

    private bool Write(ReadOnlySpan<byte> report)
    {
        if (_device is null)
        {
            return false;
        }

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
        // A Bluetooth disconnect cancels pending writes; a closed handle throws ObjectDisposedException.
        // Both mean "write failed" here, never a crash of the writing thread.
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Interlocked.Increment(ref Stats.WriteErrors);
            return false;
        }
    }

    private void ReadLoop()
    {
        HidDevice device = _device!;
        byte[] buffer = new byte[device.Info.InputReportLength];
        bool ds4 = Details.Model == ControllerModel.DualShock4;
        var clock = default(Ds4Timestamp);
        var pairing = new DonglePairing();
        while (_running)
        {
            int read;
            try
            {
                read = device.Read(buffer);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                continue;
            }

            InputState? state;
            ParseResult result;
            if (ds4)
            {
                ReadOnlySpan<byte> report = buffer.AsSpan(0, read);
                if (_dongle)
                {
                    DongleStep step = pairing.Next(Ds4InputParser.DongleUnpaired(report));
                    if (step == DongleStep.DropAndRelease)
                    {
                        // Nothing may stay held while the controller is gone.
                        PublishInput(Ds4InputParser.Neutral(_latest));
                    }

                    if (step is DongleStep.Drop or DongleStep.DropAndRelease)
                    {
                        continue;
                    }

                    if (step == DongleStep.Resume)
                    {
                        // A different controller may have paired: restart the clock and read its calibration.
                        clock = default;
                        ReloadDongleCalibration(device);
                    }
                }

                result = Ds4InputParser.TryParse(report, Details.Link, ref clock, out state, _dongle);
            }
            else
            {
                result = InputParser.TryParse(buffer.AsSpan(0, read), Details.Link, out state);
            }

            switch (result)
            {
                case ParseResult.Ok:
                    Stats.Reports++;
                    PublishInput(state!);
                    break;
                case ParseResult.BadCrc:
                    Stats.BadCrc++;
                    break;
                case ParseResult.ReducedReport:
                    Stats.Reduced++;
                    break;
            }
        }

        PublishDisconnect();
    }

    private void ReloadDongleCalibration(HidDevice device)
    {
        try
        {
            byte[]? feature = ReadDs4Calibration(device, Link.Usb, dongle: true);
            if (feature is not null && !ImuCalibration.IsBlank(feature))
            {
                Details = Details with { Calibration = ImuCalibration.ParseDualShock4(feature, grouped: true) };
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    internal void PublishInput(InputState state)
    {
        _latest = state;
        InputReceived?.Invoke(this, state);
    }

    internal void PublishDisconnect()
    {
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

    private static string? ParseMac(byte[] pairing, byte id = 0x09)
    {
        if (pairing.Length < 7 || pairing[0] != id)
        {
            return null;
        }

        if (id != 0x09 && pairing.AsSpan(1, 6).IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        // Stored least significant byte first.
        return string.Join(':', Enumerable.Range(1, 6).Reverse().Select(i => pairing[i].ToString("X2")));
    }
}

public delegate void HapticBlockSource(int block, Span<sbyte> samples);
public delegate bool SpeakerFrameSource(int block, Span<byte> frame);

/// <summary>Streams haptic and optional speaker samples on a fixed 10.667 ms clock.</summary>
public sealed class HapticStreamer : IDisposable
{
    private readonly DualSenseController _controller;
    private readonly HapticBlockSource _nextBlock;
    private readonly SpeakerFrameSource? _nextSpeaker;
    private readonly HapticReport _report = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile SpeakerPacket _speakerPacket;
    private const int PrerollBlocks = 8;

    /// <param name="nextBlock">Called with the block index and a buffer for 64 interleaved s8 samples.</param>
    public byte BufferLength { set => _report.BufferLength = value; }
    public SpeakerPacket SpeakerPacket { set => _speakerPacket = value; }

    public HapticStreamer(DualSenseController controller, HapticBlockSource nextBlock, SpeakerFrameSource? nextSpeaker = null,
        byte bufferLength = 64, SpeakerPacket speakerPacket = SpeakerPacket.Single36)
    {
        if (controller.Details.Model == ControllerModel.DualShock4)
        {
            throw new InvalidOperationException("The DualShock 4 has no haptic stream.");
        }

        if (controller.Details.Link != Link.Bluetooth)
        {
            throw new InvalidOperationException("Haptic reports are Bluetooth only; over USB haptics are an audio stream.");
        }

        _controller = controller;
        _report.BufferLength = bufferLength;
        _speakerPacket = speakerPacket;
        _nextBlock = nextBlock;
        _nextSpeaker = nextSpeaker;
        _thread = new Thread(Run) { IsBackground = true, Name = "TriggerForge: dualsense-haptics", Priority = ThreadPriority.Highest };
        _controller.HapticsActive = true;
        _controller.WriteRaw(_report.BuildPrime(_controller.LastSent, _controller.CurrentOptions()));
        _thread.Start();
    }

    public int Sent { get; private set; }

    public bool Running => _thread.IsAlive;

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
            var samples = new sbyte[HapticReport.PayloadLength];
            var speaker = new byte[HapticReport.SpeakerFrameLength];
            var pairing = new SpeakerPairing();
            Func<byte[], bool> write = Write;
            for (int block = -PrerollBlocks; _running && _controller.Connected; block++)
            {
                // A few silent blocks first so the controller's buffer is filled before real samples.
                if (block >= 0)
                {
                    _nextBlock(block, samples);
                }

                bool hasSpeaker = _nextSpeaker?.Invoke(block, speaker) ?? false;
                if (!_running || !WriteTick(_report, pairing, _speakerPacket, hasSpeaker, samples, speaker, write))
                {
                    break;
                }

                double due = (block + PrerollBlocks + 1) * intervalMs;
                while (_running && clock.Elapsed.TotalMilliseconds < due - 1.5)
                {
                    Thread.Sleep(1);
                }

                while (_running && clock.Elapsed.TotalMilliseconds < due)
                {
                    Thread.SpinWait(50);
                }
            }

            if (!_running && _controller.Connected && pairing.HasPending)
            {
                Write(_report.Build(pairing.PendingHaptics));
            }
        }
        finally
        {
            TimerResolution.End();
        }
    }

    internal static bool WriteTick(HapticReport builder, SpeakerPairing pairing, SpeakerPacket mode, bool hasSpeaker,
        ReadOnlySpan<sbyte> samples, ReadOnlySpan<byte> speaker, Func<byte[], bool> write)
    {
        SpeakerTick action = pairing.Tick(mode, hasSpeaker, samples, speaker, out bool flushPending);
        if (flushPending && !write(builder.Build(pairing.PendingHaptics)))
        {
            return false;
        }

        return action switch
        {
            SpeakerTick.Hold => true,
            SpeakerTick.Double => write(builder.BuildSpeakerDouble(pairing.PendingHaptics, samples, pairing.PendingOpus, speaker)),
            SpeakerTick.Single => write(builder.BuildSpeaker(samples, speaker)),
            _ => write(builder.Build(samples)),
        };
    }

    private bool Write(byte[] report)
    {
        if (!_controller.WriteRaw(report))
        {
            return false;
        }

        Sent++;
        return true;
    }

    /// <summary>Test signal: a sine burst on one or both actuators.</summary>
    public static HapticBlockSource Tone(float frequencyHz, float amplitude, bool left = true, bool right = true, int blocks = int.MaxValue)
    {
        amplitude = Math.Clamp(amplitude, 0f, 1f);
        return (block, samples) =>
        {
            if (block >= blocks)
            {
                samples.Clear();
                return;
            }

            for (int f = 0; f < HapticReport.FramesPerReport; f++)
            {
                double t = ((double)block * HapticReport.FramesPerReport + f) / HapticReport.SampleRate;
                sbyte v = (sbyte)Math.Round(Math.Sin(2 * Math.PI * frequencyHz * t) * 127 * amplitude);
                samples[f * 2] = left ? v : (sbyte)0;
                samples[f * 2 + 1] = right ? v : (sbyte)0;
            }
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
