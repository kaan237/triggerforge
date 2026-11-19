using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Device.Hid;

namespace Tactile.Tests.Multi;

[Collection("Localization")]
public sealed class ControllerSlotsTests
{
    [Fact]
    public void Preferred_mac_wins_and_failed_or_virtual_paths_do_not_block_other_slots()
    {
        using var pads = new FakePads { Present = [1, 9, 2, 3, 4, 5], Failing = 2 };
        pads.Service.Configure(new AppSettings { PrimaryController = FakePads.Mac(4) });
        pads.Service.ScanIfIdle();

        Assert.Equal([4, 1, 3, 5], pads.Opened);
        Assert.Equal([0, 1, 2, 3], pads.Service.Slots.Select(s => s.Index));
        Assert.Same(pads[4], pads.Service.Controller);
        Assert.DoesNotContain(9, pads.Opened);

        pads[3].PublishDisconnect();
        pads.Failing = null;
        pads.Service.ScanIfIdle();
        Assert.Equal(2, pads.Attempts.Count(id => id == 2));
        Assert.Equal([4, 1, 2, 5], pads.Service.Slots.Select(s => int.Parse(s.Controller.DevicePath)));
        Assert.Equal(3, pads.Service.Slots.Single(s => s.Controller == pads[5]).Index);
    }

    [Fact]
    public void Primary_disconnect_does_not_promote_and_reconnection_recovers_slot_zero()
    {
        using var pads = new FakePads { Present = [1, 2, 3] };
        pads.Service.ScanIfIdle();
        pads.Present = [2, 3, 4];
        pads[1].PublishDisconnect();
        pads.Service.ScanIfIdle();

        Assert.Null(pads.Service.Controller);
        Assert.Equal([1, 2, 3], pads.Service.Slots.Select(s => s.Index));
        Assert.Same(pads[2], pads.Service.Slots[0].Controller);

        pads.Present = [2, 3, 4, 1];
        pads.Service.ScanIfIdle();
        Assert.Same(pads[1], pads.Service.Controller);
        Assert.Equal([0, 1, 2, 3], pads.Service.Slots.Select(s => s.Index));
    }

    [Fact]
    public void A_different_controller_becomes_primary_when_no_other_is_connected()
    {
        using var pads = new FakePads { Present = [1] };
        pads.Service.ScanIfIdle();
        Assert.Same(pads[1], pads.Service.Controller);

        pads.Present = [];
        pads[1].PublishDisconnect();
        pads.Service.ScanIfIdle();
        Assert.Empty(pads.Service.Slots);

        pads.Present = [2];
        pads.Service.ScanIfIdle();
        Assert.Same(pads[2], pads.Service.Controller);
        Assert.Equal([0], pads.Service.Slots.Select(s => s.Index));
    }

    [Fact]
    public void Single_controller_mode_opens_one_and_does_not_scan_while_connected()
    {
        using var dir = new TempDir();
        new SettingsStore(dir.Path).Save(new AppSettings { MaxControllers = 1 });
        using var pads = new FakePads { Present = [1, 2, 3, 4] };
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false);

        pads.Service.ScanIfIdle();
        pads.Service.ScanIfIdle();
        Assert.Null(core.SecondaryControllers);
        Assert.Equal([1], pads.Attempts);
        Assert.Equal(1, pads.Scans);
        Assert.Single(pads.Service.Slots);

        core.Settings.Save(core.Settings.Current with { MaxControllers = 4 });
        pads.Service.ScanIfIdle();
        Assert.NotNull(core.SecondaryControllers);
        Assert.Equal(4, pads.Service.Slots.Count);

        core.Settings.Save(core.Settings.Current with { MaxControllers = 1 });
        Assert.Null(core.SecondaryControllers);
        Assert.Single(pads.Service.Slots);
        Assert.Same(pads[1], pads.Service.Controller);
        pads.Service.ScanIfIdle();
        Assert.Equal(2, pads.Scans);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Preferred_controller_takes_over_only_after_it_opens(int maxControllers)
    {
        using var pads = new FakePads { Present = [1] };
        pads.Service.Configure(new AppSettings { MaxControllers = maxControllers, PrimaryController = FakePads.Mac(2) });
        pads.Service.ScanIfIdle();
        DualSenseController fallback = pads[1];

        pads.Present = [1, 2, 3];
        pads.Failing = 2;
        pads.Service.ScanIfIdle();
        Assert.Same(fallback, pads.Service.Controller);

        pads.Present = [1, 2];
        pads.Failing = null;
        if (maxControllers == 2)
        {
            pads[3].PublishDisconnect();
        }
        pads.Service.ScanIfIdle();
        Assert.Same(pads[2], pads.Service.Controller);
        Assert.Equal(maxControllers, pads.Service.Slots.Count);
        Assert.Equal(maxControllers > 1, fallback.Connected);
        if (maxControllers > 1)
        {
            Assert.Same(fallback, pads.Service.Slots[1].Controller);
        }
        else
        {
            pads.Service.ScanIfIdle();
            Assert.Equal(3, pads.Scans);
        }
    }

    [Fact]
    public void Preferred_controller_takes_the_primary_slot_even_when_all_slots_are_occupied()
    {
        using var pads = new FakePads { Present = [1, 3, 4, 5] };
        pads.Service.Configure(new AppSettings { PrimaryController = FakePads.Mac(2) });
        pads.Service.ScanIfIdle();
        ControllerSlot[] secondaries = pads.Service.Slots.Skip(1).ToArray();

        pads.Present = [1, 2, 3, 4, 5];
        pads.Service.ScanIfIdle();

        Assert.Same(pads[2], pads.Service.Controller);
        Assert.False(pads[1].Connected);
        Assert.Equal(secondaries, pads.Service.Slots.Skip(1));
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_open_and_closes_the_rejected_controller()
    {
        using var pads = new FakePads { Present = [1] };
        pads.Service.ScanIfIdle();
        pads.Present = [1, 2];
        using var opening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        pads.Opening = id =>
        {
            if (id == 2)
            {
                opening.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        };

        Task scan = Task.Run(pads.Service.ScanIfIdle);
        Task? shutdown = null;
        try
        {
            Assert.True(await Task.Run(() => opening.Wait(TimeSpan.FromSeconds(5))));
            shutdown = Task.Run(pads.Service.Dispose);
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => pads.Service.Slots.Count == 0, TimeSpan.FromSeconds(5))));
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            release.Set();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            if (shutdown is not null)
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.False(pads[2].Connected);
        Assert.Contains((2, OutputState.Default), pads.Sent);
    }

    [Fact]
    public void Settings_normalize_controller_macs_and_round_trip_the_switches()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        Assert.Equal(4, store.Current.MaxControllers);
        Assert.True(store.Current.AutoPlayerLeds);
        Assert.True(store.Current.EdgeShortcuts);
        Assert.Null(store.Current.PrimaryController);

        store.Save(store.Current with
        {
            PrimaryController = " aa-bb-cc-dd-ee-02 ",
            ControllerDefaults = new Dictionary<string, string> { [" aa-bb-cc-dd-ee-02 "] = new string('a', 32) },
            AutoPlayerLeds = false,
            EdgeShortcuts = false,
            MaxControllers = -3,
        });
        AppSettings loaded = new SettingsStore(dir.Path).Current;
        Assert.Equal(FakePads.Mac(2), loaded.PrimaryController);
        Assert.Equal(FakePads.Mac(2), Assert.Single(loaded.ControllerDefaults).Key);
        Assert.False(loaded.AutoPlayerLeds);
        Assert.False(loaded.EdgeShortcuts);
        Assert.Equal(1, loaded.MaxControllers);
        Assert.Equal(4, Validation.Normalize(loaded with { MaxControllers = 20 }).MaxControllers);
    }
}

internal sealed class FakePads : IDisposable
{
    private readonly Dictionary<int, DualSenseController> _pads = [];
    public int[] Present { get; set; } = [1, 2];
    public int? Failing { get; set; }
    public Action<int>? Opening { get; set; }
    public Action<int, OutputState>? Writing { get; set; }
    public int Scans { get; private set; }
    public List<int> Opened { get; } = [];
    public List<int> Attempts { get; } = [];
    public List<(int Pad, OutputState Output)> Sent { get; } = [];
    public ControllerService Service { get; }
    public DualSenseController this[int id] => _pads[id];

    public FakePads()
    {
        Service = new(Find, Open, info => Mac(int.Parse(info.Hid.Path)), isVirtual: path => path == "9");
    }

    public static string Mac(int id) => $"AA:BB:CC:DD:EE:{id:X2}";

    public void Dispose() => Service.Dispose();

    private ControllerInfo[] Find()
    {
        Scans++;
        return Present.Select(id => new ControllerInfo(
            new HidDeviceInfo(id.ToString(), DualSenseIds.SonyVendorId,
                id == 2 ? DualSenseIds.DualSenseEdgeProductId : DualSenseIds.DualSenseProductId, 64, 64, 64),
            id == 2 ? ControllerModel.DualSenseEdge : ControllerModel.DualSense, Link.Usb)).ToArray();
    }

    private DualSenseController Open(ControllerInfo info)
    {
        int id = int.Parse(info.Hid.Path);
        Attempts.Add(id);
        Opening?.Invoke(id);
        if (id == Failing)
        {
            throw new IOException("Device unavailable.");
        }

        Opened.Add(id);
        return _pads[id] = new DualSenseController(info.Hid.Path,
            new ControllerDetails(info.Model, info.Link, Mac(id), null, ImuCalibration.Identity),
            output =>
            {
                Writing?.Invoke(id, output);
                lock (Sent)
                {
                    Sent.Add((id, output));
                }
            });
    }
}
