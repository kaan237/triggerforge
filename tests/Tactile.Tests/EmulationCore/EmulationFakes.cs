using System.Diagnostics;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;

namespace Tactile.Tests.EmulationCore;

/// <summary>Every fake writes what it was asked to do into one shared log, so tests can check the order.</summary>
internal sealed class EmulationRig
{
    public const string RealPath = @"\\?\hid#real-pad";
    public const string RealId = @"BTHENUM\REAL";

    public List<string> Log { get; } = [];
    public List<Notice> Notices { get; } = [];
    public Dictionary<string, Func<OutputState, OutputState>> Layers { get; } = [];
    public FakeServer Server { get; }
    public FakeDrivers Drivers { get; } = new();
    public FakeVhci Vhci { get; }
    public FakeHider Hider { get; }
    public FakeRecovery Recovery { get; }
    public FakeTree Tree { get; } = new();
    public FakeEmulationHost Host { get; }
    public bool SteamRunning { get; set; }
    public long Now { get; set; }
    public TimeSpan Clock { get; set; }

    public EmulationRig()
    {
        Server = new FakeServer(Log);
        Vhci = new FakeVhci(Log);
        Hider = new FakeHider(Log);
        Recovery = new FakeRecovery(Log);
        Host = new FakeEmulationHost(this);
    }

    public DriverPlatform Platform => new(Drivers, Vhci, Hider, Recovery, Tree);

    public EmulationService Create(bool withServer = true) =>
        new(Platform, withServer ? Server : null, Host, () => Clock, threaded: false, () => SteamRunning, () => Now);

    public static RealController Bluetooth { get; } = new(RealPath, Link.Bluetooth, null);

    public static RealController Usb { get; } = new(RealPath, Link.Usb, null);

    public static Profile ProfileWith(EmulationMode mode, string id = "11111111111111111111111111111111") =>
        new() { Id = id, Name = "p", Emulation = new EmulationConfig { Mode = mode } };

    public static AppSettings Settings(bool enabled = true, bool hide = true) =>
        new() { Emulation = new EmulationSettings { Enabled = enabled, HideController = hide } };

    /// <summary>Applies the current layers to a state, in insertion order.</summary>
    public OutputState Apply(OutputState o)
    {
        foreach (Func<OutputState, OutputState> layer in Layers.Values)
        {
            o = layer(o);
        }

        return o;
    }

    public void Clear() => Log.Clear();
}

internal sealed class FakeEmulationHost(EmulationRig rig) : IEmulationHost
{
    public bool Sends { get; set; } = true;

    public bool SetOutputLayer(string key, Func<OutputState, OutputState>? layer)
    {
        if (layer is null)
        {
            rig.Layers.Remove(key);
        }
        else
        {
            rig.Layers[key] = layer;
        }

        return Sends;
    }

    public void Report(Notice notice) => rig.Notices.Add(notice);
}

internal sealed class FakePad(VirtualKind kind, string busId, DualSenseIdentity? identity) : IVirtualPad
{
    public VirtualKind Kind => kind;

    public string BusId => busId;

    public DualSenseIdentity? Identity => identity;

    public bool Imported => true;

    public List<InputState> Submitted { get; } = [];

    public event Action<PadFeedback>? Feedback;

    public void Submit(InputState state) => Submitted.Add(state);

    public void Raise(PadFeedback f) => Feedback?.Invoke(f);

    public bool HasListeners => Feedback is not null;

    public void Dispose()
    {
    }
}

internal sealed class FakeServer(List<string> log) : IUsbIpServer
{
    private int _next = 1;

    public int Port => 40123;

    public bool FailCreate { get; set; }

    public List<FakePad> Pads { get; } = [];

    public FakePad? Last => Pads.Count == 0 ? null : Pads[^1];

    public bool Disposed { get; private set; }

    public IVirtualPad Create(VirtualKind kind, DualSenseIdentity? identity)
    {
        if (FailCreate)
        {
            throw new InvalidOperationException("create failed");
        }

        var pad = new FakePad(kind, $"1-{_next++}", identity);
        Pads.Add(pad);
        log.Add($"create {kind} {pad.BusId}");
        return pad;
    }

    public void Remove(IVirtualPad pad) => log.Add($"remove {pad.BusId}");

    public void Dispose() => Disposed = true;
}

internal sealed class FakeDrivers : IDriverManager
{
    public DriverState UsbIp { get; set; } = DriverState.Ready;

    public DriverState HidHide { get; set; } = DriverState.Ready;

    public DriverStatus Status(DriverKind kind) =>
        new(kind, kind == DriverKind.UsbIp ? UsbIp : HidHide, kind == DriverKind.UsbIp ? "0.9.8.1" : "1.5", null);

    public Task<InstallResult> InstallAsync(DriverKind kind, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("indiriliyor");
        return Task.FromResult(new InstallResult(true, true, "Kuruldu."));
    }
}

internal sealed class FakeVhci(List<string> log) : IVhci
{
    public bool Fail { get; set; }

    public int Attach(int tcpPort, string busId)
    {
        if (Fail)
        {
            log.Add($"attach failed {busId}");
            throw new IOException("attach failed");
        }

        log.Add($"attach {tcpPort} {busId}");
        return 3;
    }

    public void Detach(int hubPort) => log.Add($"detach {hubPort}");
}

internal sealed class FakeHider(List<string> log) : IDeviceHider
{
    public bool Fail { get; set; }

    public bool Hide(string deviceInstanceId)
    {
        log.Add($"hide {deviceInstanceId}");
        return !Fail;
    }

    public bool FailUnhide { get; set; }

    public void Unhide(string deviceInstanceId)
    {
        log.Add($"unhide {deviceInstanceId}");
        if (FailUnhide)
        {
            throw new DriverException("busy");
        }
    }
}

internal sealed class FakeRecovery(List<string> log) : IRecovery
{
    public bool UncleanShutdown { get; set; }

    public void RestoreAll() => log.Add("restoreAll");

    public void MarkCleanExit() => log.Add("cleanExit");
}

internal sealed class FakeTree : IDeviceTree
{
    public HashSet<string> Virtual { get; } = [];

    public bool IsVirtual(string hidDevicePath) => Virtual.Contains(hidDevicePath);

    public string? InstanceId(string hidDevicePath) => hidDevicePath == EmulationRig.RealPath ? EmulationRig.RealId : null;
}

internal static class Ticks
{
    public static long FromMs(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);
}
