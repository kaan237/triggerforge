using Tactile.Audio;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;
using Tactile.Device.Hid;
using Tactile.Drivers;
using Tactile.Emulation;
using Tactile.Tests.Drivers;
using Tactile.Tests.Multi;

namespace Tactile.Tests.EmulationCore;

internal sealed class FakeMic : IVirtualMic
{
    public bool MicActive { get; set; }

    public bool MicMuted { get; set; }

    public ushort MicVolume { get; set; } = 0x3000;

    public MicAudioReader? Reader { get; private set; }

    public event Action<bool>? MicActiveChanged;

    public Action? OnDetach { get; set; }

    public void SetMicReader(MicAudioReader? reader)
    {
        if (reader is null)
        {
            OnDetach?.Invoke();
        }

        Reader = reader;
    }

    public void Raise(bool active)
    {
        MicActive = active;
        MicActiveChanged?.Invoke(active);
    }

    public byte[] Read()
    {
        byte[] data = new byte[192];
        Reader?.Invoke(data);
        return data;
    }
}

internal sealed class FakeMicDecoder : IMicDecoder
{
    public bool Disposed;
    public bool Throw;

    public int Decode(ReadOnlySpan<byte> packet, Span<short> mono480)
    {
        if (Throw)
        {
            throw new InvalidOperationException();
        }

        mono480[..480].Fill(1000);
        return 480;
    }

    public int DecodePlc(Span<short> mono480)
    {
        mono480[..480].Clear();
        return 480;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Bluetooth (or other) pads without hardware; every raw write and main report is kept.</summary>
internal sealed class MicPads : IDisposable
{
    private readonly Dictionary<int, DualSenseController> _pads = [];
    public int[] Present { get; set; } = [1];
    public Link Link { get; set; } = Link.Bluetooth;
    public ControllerModel Model { get; set; } = ControllerModel.DualSense;
    public List<(int Pad, byte[] Report, string? Thread)> Raw { get; } = [];
    public List<OutputState> Sent { get; } = [];
    public ControllerService Service { get; }
    public Action<int, byte[]>? OnRaw { get; set; }
    public DualSenseController this[int id] => _pads[id];

    public MicPads()
    {
        Service = new(Find, Open, info => Mac(int.Parse(info.Hid.Path)));
    }

    public static string Mac(int id) => $"AA:BB:CC:DD:EE:{id:X2}";

    public static bool IsMicOn(byte[] r) => r.Length == HapticReport.Length && r[0] == 0x32 && r[2] == 0x91 && r[3] == 7 && r[4] == 0xFF;

    public static bool IsMicOff(byte[] r) => r.Length == HapticReport.Length && r[0] == 0x32 && r[2] == 0x91 && r[3] == 7 && r[4] == 0xFE;

    public byte[][] Controls(int pad = 1)
    {
        lock (Raw)
        {
            return Raw.Where(r => r.Pad == pad && (IsMicOn(r.Report) || IsMicOff(r.Report))).Select(r => r.Report).ToArray();
        }
    }

    public int Writes
    {
        get
        {
            lock (Raw)
            {
                return Raw.Count;
            }
        }
    }

    public void Dispose() => Service.Dispose();

    public static byte[] MicReport(byte sequence)
    {
        byte[] r = new byte[78];
        r[0] = 0x31;
        r[1] = 0x02;
        r[2] = sequence;
        r.AsSpan(3, 71).Fill(1);
        return r;
    }

    public void Frames(int pad, int count, byte first = 1)
    {
        for (int i = 0; i < count; i++)
        {
            this[pad].PublishMic(MicReport((byte)(first + i)));
        }
    }

    private ControllerInfo[] Find() => Present.Select(id => new ControllerInfo(
        new HidDeviceInfo(id.ToString(), DualSenseIds.SonyVendorId, DualSenseIds.DualSenseProductId, 78, 78, 64), Model, Link)).ToArray();

    private DualSenseController Open(ControllerInfo info)
    {
        int id = int.Parse(info.Hid.Path);
        return _pads[id] = new DualSenseController(info.Hid.Path, new ControllerDetails(info.Model, info.Link, Mac(id), null, ImuCalibration.Identity),
            output =>
            {
                lock (Raw)
                {
                    Sent.Add(output);
                }
            },
            report =>
            {
                lock (Raw)
                {
                    Raw.Add((id, report, Thread.CurrentThread.Name));
                }

                OnRaw?.Invoke(id, report);
            });
    }
}

[Collection("Localization")]
public sealed class BluetoothMicBridgeTests
{
    private sealed class Rig : IDisposable
    {
        public MicPads Pads { get; } = new();
        public FakeMic Mic { get; } = new();
        public EmulationSettings Settings { get; set; }
        public Dictionary<string, Func<OutputState, OutputState>> Layers { get; } = [];
        public List<Notice> Notices { get; } = [];
        public List<FakeMicDecoder> Decoders { get; } = [];
        public int Changes;
        public bool PadAttached { get; set; } = true;
        public BluetoothMicBridge Bridge { get; }

        public Rig(bool threaded = false, Func<IMicDecoder>? decoder = null, bool enabled = true, Func<long>? tick = null)
        {
            Settings = new EmulationSettings { BluetoothMic = enabled };
            Bridge = new BluetoothMicBridge(Pads.Service, () => Settings, () => PadAttached ? Mic : null,
                (key, layer) =>
                {
                    lock (Layers)
                    {
                        if (layer is null)
                        {
                            Layers.Remove(key);
                        }
                        else
                        {
                            Layers[key] = layer;
                        }
                    }

                    return true;
                },
                Notices.Add, () => Interlocked.Increment(ref Changes), decoder ?? (() =>
                {
                    var made = new FakeMicDecoder();
                    Decoders.Add(made);
                    return made;
                }), threaded, tick);
            Pads.Service.ScanIfIdle();
        }

        public bool LedOn
        {
            get
            {
                lock (Layers)
                {
                    return Layers.TryGetValue(IntegrationLayers.MicLed, out var layer) && layer(OutputState.Default).MicLed == MicLed.On;
                }
            }
        }

        /// <summary>The light the layer forces on a state that asks for a pulse; null without a layer.</summary>
        public MicLed? Forced
        {
            get
            {
                lock (Layers)
                {
                    return Layers.TryGetValue(IntegrationLayers.MicLed, out var layer) ? layer(OutputState.Default with { MicLed = MicLed.Pulse }).MicLed : null;
                }
            }
        }

        public void Dispose()
        {
            Bridge.Dispose();
            Pads.Dispose();
        }
    }

    private static bool CarriesMicOn(byte[] report) => report.Length > 4 && report[2] == 0x91 && (report[4] & 1) != 0;

    private static bool IsMain(byte[] report) => report.Length == OutputReport.BluetoothLength && report[0] == 0x31;

    private static bool MainCarriesMic(byte[] report) => IsMain(report) && (report[3] & 0x40) != 0;

    private static MicParseDecision Decision(DualSenseController pad) =>
        (MicParseDecision)typeof(DualSenseController).GetMethod("MicDecision", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(pad, null)!;

    private static byte[][] PadReports(MicPads pads, int pad)
    {
        lock (pads.Raw)
        {
            return pads.Raw.Where(r => r.Pad == pad).Select(r => r.Report).ToArray();
        }
    }

    // Nothing the pad wrote after its last mic-off control may carry mic-on, and there must be such a control.
    private static void AssertOffIsLastMicReport(byte[][] reports)
    {
        int off = Array.FindLastIndex(reports, MicPads.IsMicOff);
        Assert.True(off >= 0, "no mic-off control was sent");
        Assert.DoesNotContain(reports[(off + 1)..], CarriesMicOn);
    }

    private static void WaitForStreamedMicOn(MicPads pads, int pad)
    {
        Assert.True(SpinWait.SpinUntil(() => PadReports(pads, pad).Any(CarriesMicOn), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Opens_only_when_enabled_bt_primary_and_mic_active_and_closes_on_idle_or_disconnect()
    {
        using var rig = new Rig();
        DualSenseController pad = rig.Pads[1];

        // Off by default: the setting is the gate.
        rig.Settings = new EmulationSettings();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.False(pad.MicStreamActive);
        Assert.Empty(rig.Pads.Controls());
        Assert.Null(rig.Mic.Reader);
        Assert.Equal(EmulationMicStatus.Off, rig.Bridge.Status);

        // Setting on, no app recording: ready, nothing sent.
        rig.Settings = new EmulationSettings { BluetoothMic = true };
        rig.Mic.MicActive = false;
        rig.Bridge.Pump();
        Assert.Equal(EmulationMicStatus.Ready, rig.Bridge.Status);
        Assert.False(pad.MicStreamActive);
        Assert.Empty(rig.Pads.Controls());

        // An app records: stream opens, reader is installed.
        rig.Mic.Raise(true);
        rig.Bridge.Pump();
        Assert.True(pad.MicStreamActive);
        Assert.True(MicPads.IsMicOn(rig.Pads.Controls()[^1]));
        Assert.NotNull(rig.Mic.Reader);
        Assert.Equal(EmulationMicStatus.Active, rig.Bridge.Status);
        Assert.Single(rig.Decoders);

        // Recording stops: closed at once, buffer wiped, decoder released.
        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        Assert.False(pad.MicStreamActive);
        Assert.True(MicPads.IsMicOff(rig.Pads.Controls()[^1]));
        Assert.Null(rig.Mic.Reader);
        Assert.True(rig.Decoders[0].Disposed);
        Assert.Equal(EmulationMicStatus.Ready, rig.Bridge.Status);

        // Reopened with a new decoder; the setting turning off closes it.
        rig.Mic.Raise(true);
        rig.Bridge.Pump();
        Assert.Equal(2, rig.Decoders.Count);
        Assert.True(pad.MicStreamActive);
        rig.Settings = new EmulationSettings { BluetoothMic = true, Audio = false };
        rig.Bridge.Pump();
        Assert.False(pad.MicStreamActive);
        Assert.Null(rig.Mic.Reader);
        Assert.Equal(EmulationMicStatus.Off, rig.Bridge.Status);

        // The pad being stopped closes it as well.
        rig.Settings = new EmulationSettings { BluetoothMic = true };
        rig.Bridge.Pump();
        Assert.True(pad.MicStreamActive);
        rig.PadAttached = false;
        rig.Bridge.Pump();
        Assert.False(pad.MicStreamActive);
        rig.PadAttached = true;
        rig.Bridge.Pump();
        Assert.True(pad.MicStreamActive);

        // Disconnect closes the session and a new controller does not inherit it.
        pad.PublishDisconnect();
        rig.Bridge.Pump();
        Assert.Null(rig.Mic.Reader);
        Assert.Equal(EmulationMicStatus.Off, rig.Bridge.Status);
        Assert.False(rig.Bridge.SessionOpen);
        Assert.True(rig.Changes > 0);
    }

    [Fact]
    public void Primary_swap_closes_the_old_controller_and_reopens_on_the_new_one()
    {
        using var rig = new Rig();
        rig.Pads.Present = [1, 2];
        rig.Pads.Service.Configure(new AppSettings { MaxControllers = 2 });
        rig.Pads.Service.ScanIfIdle();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.True(rig.Pads[1].MicStreamActive);

        rig.Pads.Service.SelectPrimary(MicPads.Mac(2));
        // The swap only clears the flags; the bridge worker writes the off and then opens the new primary.
        Assert.False(rig.Pads[1].MicStreamActive);
        Assert.True(rig.Pads.Service.MicOffPending);
        Assert.True(MicPads.IsMicOn(rig.Pads.Controls(1)[^1]));
        rig.Bridge.Pump();
        Assert.True(MicPads.IsMicOff(rig.Pads.Controls(1)[^1]));
        Assert.False(rig.Pads.Service.MicOffPending);
        Assert.True(rig.Pads[2].MicStreamActive);
        Assert.False(rig.Pads[1].MicStreamActive);
        Assert.True(MicPads.IsMicOn(rig.Pads.Controls(2)[^1]));
    }

    [Theory]
    [InlineData(Link.Usb, ControllerModel.DualSense)]
    [InlineData(Link.Bluetooth, ControllerModel.DualShock4)]
    [InlineData(Link.Usb, ControllerModel.DualShock4)]
    public void Usb_and_dualshock4_primaries_are_refused(Link link, ControllerModel model)
    {
        using var pads = new MicPads { Link = link, Model = model };
        pads.Service.ScanIfIdle();
        using var bridge = new BluetoothMicBridge(pads.Service, () => new EmulationSettings { BluetoothMic = true }, () => new FakeMic { MicActive = true },
            (_, _) => true, _ => { }, () => { }, () => new FakeMicDecoder(), threaded: false);
        bridge.Pump();

        Assert.False(pads.Service.SetMicStream(true));
        Assert.False(pads[1].MicStreamActive);
        Assert.False(pads[1].WriteMicControl(true));
        pads[1].SetMicStream(true);
        Assert.False(pads[1].MicStreamActive);
        Assert.Equal(EmulationMicStatus.Off, bridge.Status);
        Assert.Empty(pads.Controls());
    }

    [Fact]
    public void Every_close_path_sends_mic_off_and_never_throws()
    {
        // Controller.Dispose: the stream is cleared and closed before the neutral report.
        using (var pads = new MicPads())
        {
            pads.Service.ScanIfIdle();
            Assert.True(pads.Service.SetMicStream(true));
            DualSenseController pad = pads[1];
            pad.Dispose();
            Assert.False(pad.MicStreamActive);
            Assert.True(MicPads.IsMicOff(pads.Controls()[^1]));
            Assert.Equal(OutputState.Default, pads.Sent[^1]);
        }

        // ControllerService.Dispose closes the stream it opened.
        using (var pads = new MicPads())
        {
            pads.Service.ScanIfIdle();
            pads.Service.SetMicStream(true);
            DualSenseController pad = pads[1];
            pads.Service.Dispose();
            Assert.False(pad.MicStreamActive);
            Assert.True(MicPads.IsMicOff(pads.Controls()[^1]));
        }

        // Bridge.Dispose (app exit) closes the session and the light.
        using (var rig = new Rig())
        {
            rig.Mic.MicActive = true;
            rig.Bridge.Pump();
            rig.Bridge.OnMicButton();
            rig.Bridge.Pump();
            Assert.True(rig.LedOn);
            rig.Bridge.Dispose();
            Assert.False(rig.Pads[1].MicStreamActive);
            Assert.True(MicPads.IsMicOff(rig.Pads.Controls()[^1]));
            Assert.Null(rig.Mic.Reader);
            Assert.False(rig.LedOn);
        }

        // A closed pad's failed write does not throw out of the closing path.
        using (var pads = new MicPads())
        {
            pads.Service.ScanIfIdle();
            pads.Service.SetMicStream(true);
            pads[1].PublishDisconnect();
            Assert.True(pads.Service.SetMicStream(false));
        }
    }

    [Fact]
    public void Active_event_only_signals_and_hid_writes_run_on_the_bridge_worker()
    {
        using var rig = new Rig(threaded: true);
        rig.Mic.MicActiveChanged += _ => throw new InvalidOperationException("faulty listener");

        // The raising thread only signals; the worker does every write.
        lock (rig.Pads.Raw)
        {
            rig.Pads.Raw.Clear();
        }

        rig.Mic.MicActive = true;
        try
        {
            rig.Mic.Raise(true);
        }
        catch (InvalidOperationException)
        {
            // The fake pad does not guard its listeners; the real one does (VirtualDualSenseMicTests).
        }

        SpinWait.SpinUntil(() => rig.Pads[1].MicStreamActive, TimeSpan.FromSeconds(3));
        Assert.True(rig.Pads[1].MicStreamActive);
        lock (rig.Pads.Raw)
        {
            Assert.NotEmpty(rig.Pads.Raw);
            Assert.All(rig.Pads.Raw, r => Assert.Equal("TriggerForge: mic-bridge", r.Thread));
        }
    }

    [Fact]
    public void Mic_button_only_records_the_toggle_on_the_calling_thread()
    {
        using var rig = new Rig(threaded: true);
        rig.Mic.MicActive = true;
        // The off-to-on sequence ends with the standalone control; only then is the count stable.
        SpinWait.SpinUntil(() => rig.Pads.Controls().Length == 1, TimeSpan.FromSeconds(3));
        int before = rig.Pads.Writes;
        rig.Bridge.OnMicButton();
        SpinWait.SpinUntil(() => rig.Bridge.Muted, TimeSpan.FromSeconds(3));
        Assert.True(rig.Bridge.Muted);
        Assert.Equal(before, rig.Pads.Writes);
    }

    [Fact]
    public void A_throwing_frame_handler_decoder_or_unavailable_codec_never_reaches_the_reader_thread()
    {
        using var rig = new Rig(decoder: () => new FakeMicDecoder { Throw = true });
        rig.Pads[1].MicFrameReceived += (_, _, _) => throw new InvalidOperationException();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        rig.Pads.Frames(1, 4);
        Assert.Equal(0, rig.Mic.Read().Count(b => b != 0));

        using var missing = new Rig(decoder: () => throw new AudioUnavailableException("opus.dll missing"));
        missing.Mic.MicActive = true;
        missing.Bridge.Pump();
        missing.Bridge.Pump();
        Assert.False(missing.Pads[1].MicStreamActive);
        Assert.Equal(EmulationMicStatus.Off, missing.Bridge.Status);
        Assert.Equal(NoticeKinds.Emulation, Assert.Single(missing.Notices).Kind);
        Assert.Null(missing.Mic.Reader);
    }

    [Fact]
    public void Frames_from_a_non_primary_are_dropped()
    {
        using var rig = new Rig();
        rig.Pads.Present = [1, 2];
        rig.Pads.Service.Configure(new AppSettings { MaxControllers = 2 });
        rig.Pads.Service.ScanIfIdle();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();

        rig.Pads.Frames(2, 5);
        Assert.Equal(0, rig.Bridge.BufferedSamples);
        rig.Pads.Frames(1, 3);
        Assert.Equal(1440, rig.Bridge.BufferedSamples);
    }

    [Fact]
    public void Mic_on_is_sent_again_after_the_haptic_stream_stops()
    {
        using var rig = new Rig();
        ControllerService service = rig.Pads.Service;
        Assert.True(service.SetHapticSource((_, samples) => samples.Clear(), 64, SpeakerPacket.Single36));
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.True(rig.Pads[1].MicStreamActive);

        // The running stream carries the flag in its own reports.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (rig.Pads.Raw)
            {
                return rig.Pads.Raw.Any(r => r.Report.Length == HapticReport.Length && r.Report[0] == 0x32 && r.Report[2] == 0x91 && r.Report[3] == 4 && r.Report[4] == 0x67);
            }
        }, TimeSpan.FromSeconds(3)));

        int controls = rig.Pads.Controls().Length;
        service.SetHapticSource(null, 64, SpeakerPacket.Single36);
        byte[][] after = rig.Pads.Controls();
        Assert.Equal(controls + 1, after.Length);
        Assert.True(MicPads.IsMicOn(after[^1]));
        lock (rig.Pads.Raw)
        {
            Assert.True(MicPads.IsMicOn(rig.Pads.Raw[^1].Report));
        }

        // Closing while a stream runs clears its flag instead of sending the standalone report.
        Assert.True(service.SetHapticSource((_, samples) => samples.Clear(), 64, SpeakerPacket.Single36));
        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        Assert.False(rig.Pads[1].MicStreamActive);
        service.SetHapticSource(null, 64, SpeakerPacket.Single36);
        lock (rig.Pads.Raw)
        {
            Assert.False(MicPads.IsMicOn(rig.Pads.Raw[^1].Report));
        }
    }

    [Fact]
    public void Mute_button_hardware_mute_and_windows_mute_zero_samples_drain_the_buffer_and_drive_the_led()
    {
        using var rig = new Rig();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        rig.Pads.Frames(1, 4);
        Assert.Contains(rig.Mic.Read(), b => b != 0);
        Assert.False(rig.Bridge.Muted);
        Assert.False(rig.LedOn);

        // The button mutes: zeros, the light, and the buffer keeps draining.
        rig.Bridge.OnMicButton();
        rig.Bridge.Pump();
        Assert.True(rig.LedOn);
        Assert.Equal(EmulationMicStatus.Muted, rig.Bridge.Status);
        int buffered = rig.Bridge.BufferedSamples;
        Assert.All(rig.Mic.Read(), b => Assert.Equal(0, b));
        Assert.Equal(buffered - 48, rig.Bridge.BufferedSamples);
        for (int i = 0; i < 40; i++)
        {
            rig.Mic.Read();
        }

        Assert.Equal(0, rig.Bridge.BufferedSamples);

        // Unmuted: light off, and only audio pushed after the unmute comes out.
        rig.Bridge.OnMicButton();
        rig.Bridge.Pump();
        Assert.False(rig.LedOn);
        Assert.Equal(EmulationMicStatus.Active, rig.Bridge.Status);
        Assert.All(rig.Mic.Read(), b => Assert.Equal(0, b));
        rig.Pads.Frames(1, 4, first: 20);
        Assert.Contains(rig.Mic.Read(), b => b != 0);

        // The report's own mute bit and the Windows mute silence it too.
        rig.Pads[1].PublishInput(Inputs.Make() with { MicMuted = true });
        rig.Bridge.Pump();
        Assert.True(rig.LedOn);
        Assert.All(rig.Mic.Read(), b => Assert.Equal(0, b));
        rig.Pads[1].PublishInput(Inputs.Make());
        rig.Bridge.Pump();
        Assert.False(rig.LedOn);
        rig.Mic.MicMuted = true;
        rig.Bridge.Pump();
        Assert.True(rig.LedOn);
        Assert.Equal(EmulationMicStatus.Muted, rig.Bridge.Status);
        rig.Mic.MicMuted = false;

        // The toggle ends with the session; the next recording starts unmuted.
        rig.Bridge.OnMicButton();
        rig.Bridge.Pump();
        Assert.True(rig.Bridge.Muted);
        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        Assert.False(rig.LedOn);
        rig.Mic.Raise(true);
        rig.Bridge.Pump();
        Assert.False(rig.Bridge.Muted);
        Assert.False(rig.LedOn);
    }

    [Fact]
    public void The_button_does_nothing_while_the_bridge_is_off_and_toggles_while_only_ready()
    {
        using var rig = new Rig();
        rig.Settings = new EmulationSettings();
        rig.Bridge.OnMicButton();
        rig.Settings = new EmulationSettings { BluetoothMic = true };
        rig.Bridge.Pump();
        rig.Mic.Raise(true);
        rig.Bridge.Pump();
        Assert.False(rig.Bridge.Muted);

        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        rig.Bridge.OnMicButton();
        rig.Mic.Raise(true);
        rig.Bridge.Pump();
        Assert.True(rig.Bridge.Muted);
    }

    [Fact]
    public void Mic_off_is_sent_even_when_the_haptic_stream_stops_right_after()
    {
        using var rig = new Rig();
        ControllerService service = rig.Pads.Service;
        Assert.True(service.SetHapticSource((_, samples) => samples.Clear(), 64, SpeakerPacket.Single36));
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.True(rig.Pads[1].MicStreamActive);
        WaitForStreamedMicOn(rig.Pads, 1);

        Assert.True(service.SetMicStream(false));
        service.SetHapticSource(null, 64, SpeakerPacket.Single36);

        AssertOffIsLastMicReport(PadReports(rig.Pads, 1));
        Assert.True(MicPads.IsMicOff(rig.Pads.Controls()[^1]));
    }

    [Fact]
    public void Primary_swap_and_dispose_stop_the_old_stream_carrying_mic_on_before_the_off_control()
    {
        using var rig = new Rig();
        rig.Pads.Present = [1, 2];
        rig.Pads.Service.Configure(new AppSettings { MaxControllers = 2 });
        rig.Pads.Service.ScanIfIdle();
        Assert.True(rig.Pads.Service.SetHapticSource((_, samples) => samples.Clear(), 64, SpeakerPacket.Single36));
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        WaitForStreamedMicOn(rig.Pads, 1);

        rig.Pads.Service.SelectPrimary(MicPads.Mac(2));
        Assert.False(rig.Pads[1].MicStreamActive);
        rig.Bridge.Pump();
        AssertOffIsLastMicReport(PadReports(rig.Pads, 1));

        // The same on shutdown: the service closes the mic before it stops the stream.
        using var pads = new MicPads();
        pads.Service.ScanIfIdle();
        Assert.True(pads.Service.SetHapticSource((_, samples) => samples.Clear(), 64, SpeakerPacket.Single36));
        Assert.True(pads.Service.SetMicStream(true));
        WaitForStreamedMicOn(pads, 1);
        pads.Service.Dispose();
        AssertOffIsLastMicReport(PadReports(pads, 1));
    }

    [Fact]
    public void Controller_dispose_sends_the_off_control_when_it_was_never_confirmed()
    {
        using var pads = new MicPads();
        pads.Service.ScanIfIdle();
        DualSenseController pad = pads[1];
        Assert.True(pads.Service.SetMicStream(true));
        pad.FailWrites = true;
        // The off is not confirmed, so the service says so and keeps it pending.
        Assert.False(pads.Service.SetMicStream(false));
        Assert.True(pads.Service.MicOffPending);
        Assert.True(MicPads.IsMicOn(pads.Controls()[^1]));
        pad.FailWrites = false;
        pad.Dispose();
        Assert.True(MicPads.IsMicOff(pads.Controls()[^1]));

        // Opened by the standalone control alone, never through the main report: still closed on dispose.
        using var other = new MicPads();
        other.Service.ScanIfIdle();
        Assert.True(other[1].WriteMicControl(true));
        other[1].Dispose();
        Assert.True(MicPads.IsMicOff(other.Controls()[^1]));
    }

    [Fact]
    public void Failed_open_closes_cleanly_waits_a_second_and_stays_ready()
    {
        long now = 5000;
        using var rig = new Rig(tick: () => now);
        rig.Pads[1].FailWrites = true;
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.False(rig.Bridge.SessionOpen);
        Assert.False(rig.Pads[1].MicStreamActive);
        Assert.Null(rig.Mic.Reader);
        Assert.Equal(EmulationMicStatus.Ready, rig.Bridge.Status);
        Assert.Null(rig.Forced);

        rig.Pads[1].FailWrites = false;
        now += 900;
        rig.Bridge.Pump();
        Assert.False(rig.Bridge.SessionOpen);
        Assert.Equal(EmulationMicStatus.Ready, rig.Bridge.Status);

        now += 101;
        rig.Bridge.Pump();
        Assert.True(rig.Bridge.SessionOpen);
        Assert.Equal(EmulationMicStatus.Active, rig.Bridge.Status);
    }

    [Fact]
    public void The_light_is_forced_off_while_a_session_runs_on_while_muted_and_released_afterwards()
    {
        using var rig = new Rig();
        Assert.Null(rig.Forced);
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.Equal(MicLed.Off, rig.Forced);

        rig.Bridge.OnMicButton();
        rig.Bridge.Pump();
        Assert.Equal(MicLed.On, rig.Forced);
        rig.Bridge.OnMicButton();
        rig.Bridge.Pump();
        Assert.Equal(MicLed.Off, rig.Forced);

        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        Assert.Null(rig.Forced);
    }

    [Fact]
    public void Mute_reaches_the_iso_reader_without_the_worker()
    {
        using var rig = new Rig();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        rig.Pads.Frames(1, 20);

        // No Pump after the changes: the reader works the mute out itself.
        rig.Mic.MicMuted = true;
        Assert.All(rig.Mic.Read(), b => Assert.Equal(0, b));
        rig.Mic.MicMuted = false;
        Assert.Contains(rig.Mic.Read(), b => b != 0);
        rig.Pads[1].PublishInput(Inputs.Make() with { MicMuted = true });
        Assert.All(rig.Mic.Read(), b => Assert.Equal(0, b));
        rig.Pads[1].PublishInput(Inputs.Make());
        Assert.Contains(rig.Mic.Read(), b => b != 0);
        rig.Bridge.OnMicButton();
        Assert.All(rig.Mic.Read(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void The_worker_costs_nothing_when_off_and_polls_slowly_when_nobody_records()
    {
        using var off = new Rig(threaded: true, enabled: false);
        off.Mic.MicActive = true;
        Thread.Sleep(400);
        Assert.False(off.Bridge.Listening);
        Assert.InRange(off.Bridge.Evaluations, 1, 2);
        Assert.False(off.Pads[1].MicStreamActive);

        // Turning it on is a signal, not a poll.
        off.Settings = new EmulationSettings { BluetoothMic = true };
        off.Bridge.Poke();
        Assert.True(SpinWait.SpinUntil(() => off.Pads[1].MicStreamActive, TimeSpan.FromSeconds(3)));
        Assert.True(off.Bridge.Listening);

        using var idle = new Rig(threaded: true);
        Thread.Sleep(400);
        Assert.True(idle.Bridge.Listening);
        Assert.InRange(idle.Bridge.Evaluations, 1, 3);
        idle.Mic.Raise(true);
        Assert.True(SpinWait.SpinUntil(() => idle.Pads[1].MicStreamActive, TimeSpan.FromSeconds(3)));

        // Turning it off again stops listening and closes the session.
        idle.Settings = new EmulationSettings();
        idle.Bridge.Poke();
        Assert.True(SpinWait.SpinUntil(() => !idle.Pads[1].MicStreamActive && !idle.Bridge.Listening, TimeSpan.FromSeconds(3)));
        int settled = idle.Bridge.Evaluations;
        Thread.Sleep(300);
        Assert.Equal(settled, idle.Bridge.Evaluations);
    }

    [Fact]
    public void Status_mic_goes_through_the_emulation_service()
    {
        var emulationRig = new EmulationRig();
        using var server = new UsbIpServer(0);
        using var emulation = new EmulationService(emulationRig.Platform, server, emulationRig.Host, () => TimeSpan.Zero,
            threaded: false, steamRunning: () => false);
        using var pads = new MicPads();
        pads.Service.ScanIfIdle();
        EmulationSettings settings = new() { Enabled = true, HideController = false, BluetoothMic = true };
        using var bridge = new BluetoothMicBridge(pads.Service, () => settings, () => emulation.VirtualMic,
            (_, _) => true, _ => { }, () => emulation.NotifyMicChanged(), () => new FakeMicDecoder(), threaded: false);
        emulation.MicStatusSource = () => bridge.Status;
        int changes = 0;
        emulation.Changed += () => changes++;

        Assert.Equal(EmulationMicStatus.Off, emulation.Status.Mic);
        emulation.Update(EmulationRig.ProfileWith(EmulationMode.DualSense), new AppSettings { Emulation = settings }, EmulationRig.Bluetooth);
        var pad = Assert.IsType<VirtualDualSense.WithAudio>(emulation.VirtualMic);
        bridge.Pump();
        Assert.Equal(EmulationMicStatus.Ready, emulation.Status.Mic);

        Assert.Equal(0, pad.Control(new SetupPacket(1, 0x0B, 1, 2, 0), [], 0).Status);
        bridge.Pump();
        Assert.Equal(EmulationMicStatus.Active, emulation.Status.Mic);
        Assert.True(changes > 0);

        Assert.Equal(0, pad.Control(new SetupPacket(0x21, 0x01, 0x0100, 0x0500, 1), [1], 0).Status);
        bridge.Pump();
        Assert.Equal(EmulationMicStatus.Muted, emulation.Status.Mic);
    }

    [Fact]
    public void Mic_off_timestamp_is_written_before_the_flag_and_forgotten_after_the_grace_period()
    {
        using var pads = new MicPads();
        pads.Service.ScanIfIdle();
        DualSenseController pad = pads[1];
        var stamp = typeof(DualSenseController).GetField("_micOffAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var decide = typeof(DualSenseController).GetMethod("MicDecision", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        MicParseDecision Decision() => (MicParseDecision)decide.Invoke(pad, null)!;

        Assert.Equal(MicParseDecision.Normal, Decision());
        Assert.Equal(0L, (long)stamp.GetValue(pad)!);
        pad.SetMicStream(true);
        Assert.Equal(MicParseDecision.Publish, Decision());
        pad.SetMicStream(false);
        Assert.NotEqual(0L, (long)stamp.GetValue(pad)!);
        Assert.Equal(MicParseDecision.DropGrace, Decision());

        // Still dropped until the off control is confirmed; the grace period starts then.
        stamp.SetValue(pad, System.Diagnostics.Stopwatch.GetTimestamp() - 3 * System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal(MicParseDecision.DropGrace, Decision());
        Assert.True(pad.WriteMicControl(false));
        Assert.Equal(MicParseDecision.DropGrace, Decision());

        stamp.SetValue(pad, System.Diagnostics.Stopwatch.GetTimestamp() - 3 * System.Diagnostics.Stopwatch.Frequency);
        Assert.Equal(MicParseDecision.Normal, Decision());
        Assert.Equal(0L, (long)stamp.GetValue(pad)!);
    }

    [Fact]
    public void A_failed_off_is_retried_by_the_bridge_until_confirmed_and_the_parse_gate_keeps_dropping()
    {
        long now = 5000;
        using var rig = new Rig(tick: () => now);
        DualSenseController pad = rig.Pads[1];
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.True(pad.MicStreamActive);
        Assert.Contains(rig.Pads.Raw, r => MainCarriesMic(r.Report));

        pad.FailWrites = true;
        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        Assert.False(rig.Bridge.SessionOpen);
        Assert.False(pad.MicStreamActive);
        Assert.True(pad.MicOffPending);
        Assert.True(rig.Pads.Service.MicOffPending);
        Assert.False(rig.Pads.Service.SetMicStream(false));
        // The pad does not take an unconfirmed off for "nothing to change".
        Assert.False(pad.SetMicStream(false));
        Assert.Equal(MicParseDecision.DropGrace, Decision(pad));

        // Failing again keeps it pending; the next try is a second later.
        now += 1000;
        rig.Bridge.Pump();
        Assert.True(pad.MicOffPending);
        pad.FailWrites = false;
        now += 999;
        rig.Bridge.Pump();
        Assert.True(pad.MicOffPending);
        Assert.Equal(MicParseDecision.DropGrace, Decision(pad));

        now += 1;
        rig.Bridge.Pump();
        Assert.False(pad.MicOffPending);
        Assert.False(rig.Pads.Service.MicOffPending);
        byte[][] reports = PadReports(rig.Pads, 1);
        AssertOffIsLastMicReport(reports);
        // The main report without mic bits went first, the off control is the last mic report.
        int off = Array.FindLastIndex(reports, MicPads.IsMicOff);
        Assert.False(MainCarriesMic(reports[Array.FindLastIndex(reports, off, IsMain)]));
        Assert.DoesNotContain(reports[off..], MainCarriesMic);
        Assert.Equal(MicParseDecision.DropGrace, Decision(pad));
    }

    [Fact]
    public void The_off_retry_runs_on_the_worker_even_with_the_setting_off()
    {
        using var rig = new Rig(threaded: true);
        rig.Mic.Raise(true);
        Assert.True(SpinWait.SpinUntil(() => rig.Pads[1].MicStreamActive, TimeSpan.FromSeconds(3)));
        rig.Pads[1].FailWrites = true;
        rig.Settings = new EmulationSettings();
        rig.Bridge.Poke();
        Assert.True(SpinWait.SpinUntil(() => rig.Pads.Service.MicOffPending && !rig.Bridge.SessionOpen, TimeSpan.FromSeconds(3)));

        rig.Pads[1].FailWrites = false;
        Assert.True(SpinWait.SpinUntil(() => !rig.Pads.Service.MicOffPending, TimeSpan.FromSeconds(4)));
        Assert.False(rig.Pads[1].MicOffPending);
        AssertOffIsLastMicReport(PadReports(rig.Pads, 1));
    }

    [Fact]
    public void A_disconnected_pad_needs_no_write_and_no_wait()
    {
        using var rig = new Rig();
        Assert.True(rig.Pads.Service.SetHapticSource((_, samples) => samples.Clear(), 64, SpeakerPacket.Single36));
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        WaitForStreamedMicOn(rig.Pads, 1);

        int writes = rig.Pads.Writes;
        rig.Pads[1].PublishDisconnect();
        Assert.Equal(writes, rig.Pads.Writes);
        Assert.False(rig.Pads.Service.MicOffPending);
        Assert.False(rig.Pads[1].MicStreamActive);
        rig.Bridge.Pump();
        Assert.Equal(writes, rig.Pads.Writes);
        Assert.False(rig.Bridge.SessionOpen);
    }

    [Fact]
    public void Release_closes_only_the_controller_asked_to_sleep_and_holds_it_for_ten_seconds()
    {
        long now = 5000;
        using var rig = new Rig(tick: () => now);
        rig.Pads.Present = [1, 2];
        rig.Pads.Service.Configure(new AppSettings { MaxControllers = 2 });
        rig.Pads.Service.ScanIfIdle();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        Assert.True(rig.Bridge.SessionOpen);
        int changes = Volatile.Read(ref rig.Changes);

        // Another controller going to sleep leaves the session alone.
        rig.Bridge.Release(MicPads.Mac(2));
        Assert.True(rig.Bridge.SessionOpen);
        Assert.Equal(changes, Volatile.Read(ref rig.Changes));

        rig.Bridge.Release(MicPads.Mac(1));
        Assert.False(rig.Bridge.SessionOpen);
        Assert.False(rig.Pads[1].MicStreamActive);
        Assert.True(MicPads.IsMicOff(rig.Pads.Controls(1)[^1]));
        Assert.True(Volatile.Read(ref rig.Changes) > changes);
        Assert.Equal(EmulationMicStatus.Ready, rig.Bridge.Status);

        now += 9999;
        rig.Bridge.Pump();
        Assert.False(rig.Bridge.SessionOpen);

        // The hold is the released controller's: a new primary opens at once.
        rig.Pads.Service.SelectPrimary(MicPads.Mac(2));
        rig.Bridge.Pump();
        Assert.True(rig.Bridge.SessionOpen);
        Assert.True(rig.Pads[2].MicStreamActive);
        rig.Pads.Service.SelectPrimary(MicPads.Mac(1));
        rig.Bridge.Pump();
        Assert.False(rig.Pads[2].MicStreamActive);
        Assert.False(rig.Bridge.SessionOpen);

        now += 1;
        rig.Bridge.Pump();
        Assert.True(rig.Bridge.SessionOpen);
        Assert.True(rig.Pads[1].MicStreamActive);
    }

    [Fact]
    public void The_iso_reader_gets_silence_while_the_session_closes()
    {
        using var rig = new Rig();
        rig.Mic.MicActive = true;
        rig.Bridge.Pump();
        rig.Pads.Frames(1, 20);
        Assert.Contains(rig.Mic.Read(), b => b != 0);

        // The read lands while the controller side is being closed, with samples still buffered.
        byte[]? during = null;
        MicAudioReader reader = rig.Mic.Reader!;
        rig.Mic.OnDetach = () =>
        {
            during = new byte[192];
            reader(during);
        };
        rig.Mic.Raise(false);
        rig.Bridge.Pump();
        Assert.NotNull(during);
        Assert.All(during, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_press_during_the_close_is_kept_and_a_press_on_another_controller_or_while_unavailable_is_not()
    {
        using (var rig = new Rig())
        {
            rig.Mic.MicActive = true;
            rig.Bridge.Pump();
            rig.Pads.OnRaw = (_, report) =>
            {
                if (MicPads.IsMicOff(report))
                {
                    rig.Bridge.OnMicButton();
                }
            };
            rig.Mic.Raise(false);
            rig.Bridge.Pump();
            rig.Pads.OnRaw = null;
            rig.Mic.Raise(true);
            rig.Bridge.Pump();
            Assert.True(rig.Bridge.Muted);
        }

        using (var rig = new Rig())
        {
            rig.Pads.Present = [1, 2];
            rig.Pads.Service.Configure(new AppSettings { MaxControllers = 2 });
            rig.Pads.Service.ScanIfIdle();
            rig.Mic.MicActive = true;
            rig.Bridge.Pump();
            rig.Bridge.OnMicButton();
            rig.Bridge.Pump();
            Assert.True(rig.Bridge.Muted);
            rig.Pads.Service.SelectPrimary(MicPads.Mac(2));
            rig.Bridge.Pump();
            Assert.True(rig.Pads[2].MicStreamActive);
            Assert.False(rig.Bridge.Muted);
        }

        bool broken = true;
        using (var rig = new Rig(decoder: () => broken ? throw new AudioUnavailableException("opus.dll missing") : new FakeMicDecoder()))
        {
            rig.Mic.MicActive = true;
            rig.Bridge.Pump();
            Assert.False(rig.Bridge.SessionOpen);
            rig.Bridge.OnMicButton();
            rig.Mic.Raise(false);
            rig.Bridge.Pump();
            broken = false;
            rig.Mic.Raise(true);
            rig.Bridge.Pump();
            Assert.True(rig.Bridge.SessionOpen);
            Assert.False(rig.Bridge.Muted);
        }
    }

    private sealed class CoreRig : IDisposable
    {
        private readonly TempDir _dir = new();
        public MicPads Pads { get; } = new();
        public FakeMic Mic { get; } = new() { MicActive = true };
        public FakePlatform Platform { get; } = new();
        public ManualTime Time { get; } = new();
        public AppCore Core { get; }

        public CoreRig()
        {
            Core = new AppCore(_dir.Path, new FakeForeground(), Pads.Service, Time, runTimer: false, Platform.Platform,
                virtualMic: () => Mic, micDecoder: () => new FakeMicDecoder());
            Core.Settings.Save(Core.Settings.Current with { Emulation = Core.Settings.Current.Emulation with { BluetoothMic = true } });
            Pads.Service.ScanIfIdle();
            Core.Refresh();
        }

        public void Press()
        {
            Core.OnInput(Inputs.Make());
            Core.OnInput(Inputs.Make(Buttons.Mic));
            Core.OnInput(Inputs.Make());
            Core.Refresh();
        }

        public OutputState Last
        {
            get
            {
                lock (Pads.Raw)
                {
                    return Pads.Sent[^1];
                }
            }
        }

        public void Dispose()
        {
            Core.Dispose();
            Pads.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public void Mic_button_toggles_the_mute_whatever_mapping_pause_or_keyboard_do()
    {
        using var rig = new CoreRig();
        Assert.True(rig.Pads[1].MicStreamActive);
        Assert.Equal(EmulationMicStatus.Active, StatusOf(rig));

        // Plain.
        rig.Press();
        Assert.Equal(EmulationMicStatus.Muted, StatusOf(rig));
        Assert.Equal(MicLed.On, rig.Last.MicLed);
        rig.Press();
        Assert.Equal(EmulationMicStatus.Active, StatusOf(rig));

        // Mapped to a mic mute of its own: both run.
        rig.Core.Profiles.Save(rig.Core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = new Dictionary<string, Binding> { ["mic"] = new() { Press = new ActionConfig { Kind = ActionKind.MicMute } } },
            },
        });
        rig.Press();
        Assert.Equal(EmulationMicStatus.Muted, StatusOf(rig));
        Assert.True(rig.Platform.Microphone.IsMuted);
        rig.Press();
        Assert.Equal(EmulationMicStatus.Active, StatusOf(rig));

        // Mapping paused by the user interface.
        rig.Core.UiMappingPaused = true;
        rig.Press();
        Assert.Equal(EmulationMicStatus.Muted, StatusOf(rig));
        rig.Press();
        rig.Core.UiMappingPaused = false;
        Assert.Equal(EmulationMicStatus.Active, StatusOf(rig));

        // Paused by the shortcut.
        rig.Core.ToggleUserPause();
        Assert.True(rig.Core.UserMappingPaused);
        rig.Press();
        Assert.Equal(EmulationMicStatus.Muted, StatusOf(rig));
        rig.Press();
        rig.Core.ToggleUserPause();

        // On-screen keyboard open.
        rig.Core.Profiles.Save(rig.Core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = new Dictionary<string, Binding>
                {
                    ["cross"] = new() { Press = new ActionConfig { Kind = ActionKind.Special, Special = SpecialAction.OnScreenKeyboard } },
                },
            },
        });
        rig.Core.OnInput(Inputs.Make(Buttons.Cross));
        rig.Core.OnInput(Inputs.Make());
        Assert.True(rig.Core.OnScreenKeyboardOpen);
        rig.Press();
        Assert.True(rig.Core.OnScreenKeyboardOpen);
        Assert.Equal(EmulationMicStatus.Muted, StatusOf(rig));
    }

    private static EmulationMicStatus StatusOf(CoreRig rig) => rig.Core.MicBridgeStatus;

    [Fact]
    public void Mic_led_outranks_other_layers_and_survives_clear_overrides()
    {
        using var rig = new CoreRig();
        rig.Core.SetOverride("live.micLed", o => o with { MicLed = MicLed.Off });
        rig.Core.SetOverride(EmulationService.GameLayer, o => o with { MicLed = MicLed.Pulse });
        rig.Core.SetOverride("mapping.mic", o => o with { MicLed = MicLed.Pulse });
        Assert.NotEqual(MicLed.On, rig.Last.MicLed);

        rig.Press();
        Assert.Equal(MicLed.On, rig.Last.MicLed);
        rig.Core.ClearOverrides();
        Assert.Equal(MicLed.On, rig.Last.MicLed);
        rig.Core.SetOverride("live.micLed", o => o with { MicLed = MicLed.Off });
        Assert.Equal(MicLed.On, rig.Last.MicLed);

        // Unmuting removes the layer, and the others show again.
        rig.Press();
        Assert.Equal(MicLed.Off, rig.Last.MicLed);
        Assert.Equal(4, IntegrationLayersOrder(IntegrationLayers.MicLed));
        Assert.True(IntegrationLayersOrder(IntegrationLayers.MicLed) > IntegrationLayersOrder(EmulationService.GameLayer));
        Assert.True(IntegrationLayersOrder(IntegrationLayers.MicLed) > IntegrationLayersOrder("live.micLed"));
        Assert.True(IntegrationLayersOrder(IntegrationLayers.MicLed) > IntegrationLayersOrder("mapping.mic"));
    }

    private static int IntegrationLayersOrder(string key) => IntegrationLayers.Order(key);

    [Fact]
    public void Status_state_compares_the_mic_and_old_statuses_stay_off()
    {
        EmulationStatus off = EmulationStatus.Unavailable;
        Assert.Equal(EmulationMicStatus.Off, off.Mic);
        Assert.True(off.SameState(off with { }));
        foreach (EmulationMicStatus mic in new[] { EmulationMicStatus.Ready, EmulationMicStatus.Active, EmulationMicStatus.Muted })
        {
            Assert.False(off.SameState(off with { Mic = mic }));
        }

        Assert.True((off with { Mic = EmulationMicStatus.Muted }).SameState(off with { Mic = EmulationMicStatus.Muted }));
    }

    [Fact]
    public void Secondary_slots_always_run_with_the_bluetooth_mic_off()
    {
        using var driver = new DriverRig();
        using var pads = new FakePads();
        var server = new FakeServer(driver.Log);
        var tree = new FakeTree { InstanceIds = { ["1"] = @"TEST\1", ["2"] = @"TEST\2" } };
        using var core = new AppCore(driver.Dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false,
            drivers: new DriverPlatform(new FakeDrivers(), driver.Vhci, driver.Hider, driver.NewRecovery(), tree),
            server: server, steamRunning: () => false);
        Profile profile = core.Profiles.Save(core.ActiveProfile with { Emulation = new() { Mode = EmulationMode.Xbox360 } });
        core.Settings.Save(core.Settings.Current with
        {
            DefaultProfileId = profile.Id,
            Emulation = core.Settings.Current.Emulation with { BluetoothMic = true },
        });
        pads.Service.ScanIfIdle();

        EmulationService emulation = core.Emulation!;
        var slots = (EmulationService?[])typeof(EmulationService).GetField("_secondarySlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(emulation)!;
        EmulationService secondary = Assert.Single(slots, s => s is not null)!;
        object wanted = typeof(EmulationService).GetField("_wanted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(secondary)!;
        var settings = (EmulationSettings)wanted.GetType().GetProperty("Settings")!.GetValue(wanted)!;
        Assert.False(settings.BluetoothMic);
        Assert.True(core.Settings.Current.Emulation.BluetoothMic);
    }
}
