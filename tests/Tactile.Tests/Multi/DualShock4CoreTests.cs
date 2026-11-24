using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;
using Tactile.Device.Hid;
using Tactile.Drivers;
using Tactile.Emulation;
using Tactile.Input;
using Tactile.Tests.Drivers;
using Tactile.Tests.EmulationCore;

namespace Tactile.Tests.Multi;

[Collection("Localization")]
public sealed class DualShock4CoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Virtual_ds4_09cc_path_is_never_opened_as_physical_controller(bool emulationActive)
    {
        using var pads = new Ds4Pads { Present = ["virtual", "probe", "10"] };
        pads.Service.VirtualPadMayBeActive = () => emulationActive;
        pads.Service.ScanIfIdle();

        Assert.DoesNotContain("virtual", pads.Opened);
        Assert.Contains("10", pads.Opened);
        // With emulation off a failing probe keeps the old behaviour: the pad is treated as real.
        Assert.Equal(!emulationActive, pads.Opened.Contains("probe"));

    }

    [Fact]
    public void A_throwing_virtual_pad_check_fails_closed()
    {
        using var pads = new Ds4Pads { Present = ["probe"] };
        pads.Service.VirtualPadMayBeActive = () => throw new InvalidOperationException("state unavailable");
        pads.Service.ScanIfIdle();
        Assert.Empty(pads.Opened);
    }

    [Fact]
    public void Physical_ds4_drives_virtual_dualsense_and_hides_without_haptics()
    {
        using var rig = new CoreRig("10");
        rig.Connect();

        Assert.Equal(VirtualKind.DualSense, Assert.Single(rig.Server.Pads).Kind);
        Assert.Equal([@"TEST\10"], rig.Driver.HidHide.Blacklist);
        Assert.True(rig.Core.Emulation!.Status is { Attached: true, Hidden: true, Mode: EmulationMode.DualSense });

        rig.Opened[0].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.NotEmpty(rig.Server.Pads[0].Submitted);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Core.Tick(rig.Core.Now());
        Assert.False(rig.Core.Haptics.Streaming);
        Assert.Null(rig.Core.Haptics.Problem);
        Assert.Single(rig.Server.Pads);
    }

    [Fact]
    public void Wired_dualsense_keeps_being_its_own_virtual_pad()
    {
        using var rig = new CoreRig("1");
        rig.Connect();

        Assert.Empty(rig.Server.Pads);
        Assert.Equal(EmulationMode.DualSense, rig.Core.Emulation!.Status.Mode);
    }

    [Fact]
    public void Audio_flag_does_not_recreate_the_virtual_pad_of_a_ds4()
    {
        using var rig = new CoreRig("10", Link.Bluetooth);
        rig.Connect();
        Assert.Single(rig.Server.Pads);

        rig.Core.Settings.Save(rig.Core.Settings.Current with
        {
            Emulation = rig.Core.Settings.Current.Emulation with { Audio = true },
        });
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Core.Tick(rig.Core.Now());

        Assert.Single(rig.Server.Pads);
        Assert.True(rig.Core.Emulation!.Status.Attached);
        Assert.Null(rig.Core.Haptics.Problem);
    }

    [Fact]
    public void PlayHapticTone_and_PulseConfirm_behave_safely_on_ds4()
    {
        using var rig = new CoreRig("10", Link.Bluetooth, mode: EmulationMode.Off, confirmPulses: true);
        rig.Connect();

        Assert.NotNull(rig.Service.PlayHapticTone(200, 0.5f, true, true, 100));
        Assert.False(rig.Core.Haptics.Streaming);

        Profile other = rig.Core.Profiles.Save(new Profile { Name = "Other", Haptics = new() { ConfirmPulses = true } });
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Core.ActivateProfile(other.Id);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Core.Tick(rig.Core.Now());

        Assert.Contains(rig.Sent, o => o is { RumbleLeft: 64, RumbleRight: 160 });
    }

    [Fact]
    public void Secondary_ds4_receives_the_profile_lightbar_and_ignores_edge_shortcuts()
    {
        using var dir = new TempDir();
        using var pads = new Ds4Pads { Present = ["1", "10"] };
        var clock = new ManualTime();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, clock, runTimer: false);
        Profile fallback = core.ActiveProfile;
        Profile lit = core.Profiles.Save(new Profile { Name = "Lit", Lightbar = new() { Color = "#123456" } });
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = fallback.Id });
        pads.Service.ScanIfIdle();

        ControllerSlot slot = pads.Service.Slots.Single(s => s.Index == 1);
        Assert.Equal(ControllerModel.DualShock4, slot.Controller.Details.Model);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        core.SetSecondaryProfile(1, lit.Id);
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), slot.Controller.LastSent.Lightbar);

        bool before = core.Settings.Current.EdgeShortcuts;
        slot.Controller.PublishInput(Inputs.Make(Buttons.Fn1));
        slot.Controller.PublishInput(Inputs.Make(Buttons.Fn1 | Buttons.R1));
        Assert.Equal(before, core.Settings.Current.EdgeShortcuts);
        Assert.Equal(fallback.Id, core.ActiveProfile.Id);
        Assert.Null(EdgeShortcuts.Read(ControllerModel.DualShock4, Buttons.Fn1, Buttons.Fn1 | Buttons.R1));
    }

    [Fact]
    public async Task Physical_ds4_input_reaches_the_mapper_and_the_dsu_server()
    {
        int port;
        using (var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        }

        var fake = new FakePlatform();
        using var rig = new CoreRig("10", fake: fake, mapping: Bind.Buttons(("cross", Bind.Key(Key.A))));
        rig.Core.Settings.Save(rig.Core.Settings.Current with
        {
            Integration = new IntegrationSettings { DsuEnabled = true, DsuPort = port, DsxPortFile = false },
        });
        Assert.Equal(DsuListenerState.Listening, rig.Core.Integration.Dsu!.State);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, port);
        byte[] request = new byte[28];
        "DSUC"u8.CopyTo(request);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), 1001);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)(request.Length - 16));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0x100002);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), Crc32.Compute(request));
        await client.SendAsync(request.AsMemory());
        await Until(() => rig.Core.Integration.Dsu!.Clients == 1);

        rig.Connect();
        rig.Time.Advance(TimeSpan.FromMilliseconds(20));
        rig.Opened[0].PublishInput(Inputs.Make(Buttons.Cross));
        rig.Time.Advance(TimeSpan.FromMilliseconds(20));
        rig.Core.Tick(rig.Core.Now());

        Assert.Contains("A down", fake.Injector.Take());
        await Until(() => rig.Core.Integration.Dsu!.Packets >= 1);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public void Wired_ds4_in_dualsense_mode_asks_for_a_hid_only_pad()
    {
        using var rig = new CoreRig("10");
        rig.Core.Settings.Save(rig.Core.Settings.Current with
        {
            Emulation = rig.Core.Settings.Current.Emulation with { Audio = true },
        });
        rig.Connect();

        // The fake server cannot see the audio flag (the extension only forwards it to the real server),
        // so this checks what the service derives from it: no audio pad for a DualShock 4.
        Assert.Equal(VirtualKind.DualSense, Assert.Single(rig.Server.Pads).Kind);
        Assert.Null(rig.Core.Emulation!.VirtualAudio);
        Assert.Equal(EmulationAudioStatus.Off, rig.Core.Emulation.Status.Audio);
    }

    [Fact]
    public void Secondary_ds4_gets_profile_rumble_game_feedback_and_lightbar()
    {
        using var rig = new SecondaryRig();
        rig.Connect();
        Profile profile = rig.Core.Profiles.Save(rig.Core.ActiveProfile with
        {
            Id = "",
            Name = "Secondary",
            Rumble = new() { Strength = 0.5 },
            Lightbar = new() { Color = "#123456" },
            Emulation = new() { Mode = EmulationMode.DualSense },
        });
        rig.Core.SetSecondaryProfile(1, profile.Id);
        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Core.Refresh();
        FakePad secondary = rig.Server.Pads[^1];
        Assert.Equal(VirtualKind.DualSense, secondary.Kind);
        Assert.Equal(ControllerModel.DualShock4, rig.Pads.Service.Slots.Single(s => s.Index == 1).Controller.Details.Model);
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), rig.Pads["10"].LastSent.Lightbar);

        byte[] game = new byte[OutputReport.StateLength];
        game[0] = 0x01;
        game[3] = 100;
        secondary.Raise(new DualSenseOutputFeedback(0, game));
        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Core.Refresh();
        Assert.Equal(50, rig.Pads["10"].LastSent.RumbleLeft);
        Assert.Equal(0, rig.Pads["1"].LastSent.RumbleLeft);
    }

    [Fact]
    public void MayHavePad_covers_wanted_attached_attaching_detaching_and_shortly_after()
    {
        var rig = new EmulationRig();
        using EmulationService service = rig.Create();
        Assert.False(service.MayHavePad);

        // Attaching: the wish is already gone, the pad is not published yet.
        bool duringAttach = false;
        rig.Vhci.DuringAttach = () =>
        {
            rig.Vhci.DuringAttach = null;
            service.Update(EmulationRig.ProfileWith(EmulationMode.Off), EmulationRig.Settings(), EmulationRig.Bluetooth);
            duringAttach = service.MayHavePad;
        };
        service.Update(EmulationRig.ProfileWith(EmulationMode.DualSense), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.True(duringAttach);

        service.Update(EmulationRig.ProfileWith(EmulationMode.DualSense), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.True(service.Status.Attached);
        Assert.True(service.MayHavePad);

        // Detaching: pad and hidden path are already cleared while Detach runs.
        bool duringDetach = false;
        rig.Vhci.DuringDetach = () => duringDetach = service.MayHavePad;
        service.Update(EmulationRig.ProfileWith(EmulationMode.Off), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.True(duringDetach);
        Assert.False(service.Status.Attached);

        // Shortly after the last detach, then not any more.
        Assert.True(service.MayHavePad);
        rig.Clock = TimeSpan.FromSeconds(2.9);
        Assert.True(service.MayHavePad);
        rig.Clock = TimeSpan.FromSeconds(3.1);
        Assert.False(service.MayHavePad);
    }

    [Fact]
    public void MayHavePad_is_true_while_a_pad_is_only_wanted()
    {
        var rig = new EmulationRig();
        using EmulationService service = rig.Create(withServer: false);
        service.Update(EmulationRig.ProfileWith(EmulationMode.DualSense), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.False(service.Status.Attached);
        Assert.True(service.MayHavePad);
    }

    private sealed class SecondaryRig : IDisposable
    {
        public DriverRig Driver { get; } = new();
        public Ds4Pads Pads { get; } = new() { Present = ["1", "10"] };
        public FakeServer Server { get; }
        public ManualTime Time { get; } = new();
        public AppCore Core { get; }

        public SecondaryRig()
        {
            Server = new FakeServer(Driver.Log);
            var tree = new FakeTree { InstanceIds = { ["1"] = @"TEST\1", ["10"] = @"TEST\10" } };
            Core = new AppCore(Driver.Dir.Path, new FakeForeground(), Pads.Service, Time, runTimer: false,
                drivers: new DriverPlatform(new FakeDrivers(), Driver.Vhci, Driver.Hider, Driver.NewRecovery(), tree),
                server: Server, steamRunning: () => false);
            Profile profile = Core.Profiles.Save(Core.ActiveProfile with { Emulation = new() { Mode = EmulationMode.Xbox360 } });
            Core.Settings.Save(Core.Settings.Current with { DefaultProfileId = profile.Id });
        }

        public void Connect() => Pads.Service.ScanIfIdle();

        public void Dispose()
        {
            Core.Dispose();
            Pads.Dispose();
            Driver.Dispose();
        }
    }

    private sealed class Ds4Pads : IDisposable
    {
        private readonly Dictionary<string, DualSenseController> _pads = [];
        public string[] Present { get; set; } = [];
        public List<string> Opened { get; } = [];
        public ControllerService Service { get; }
        public DualSenseController this[string path] => _pads[path];

        public Ds4Pads() => Service = new(Find, Open, _ => null, IsVirtual);

        public void Dispose() => Service.Dispose();

        private static bool IsVirtual(string path) => path switch
        {
            "virtual" => true,
            "probe" => throw new InvalidOperationException("device tree unavailable"),
            _ => false,
        };

        private ControllerInfo[] Find() => Present.Select(path =>
        {
            bool ds4 = path != "1";
            return new ControllerInfo(
                new HidDeviceInfo(path, DualSenseIds.SonyVendorId,
                    ds4 ? DualSenseIds.DualShock4V2ProductId : DualSenseIds.DualSenseProductId, 64, 64, 64),
                ds4 ? ControllerModel.DualShock4 : ControllerModel.DualSense, Link.Usb);
        }).ToArray();

        private DualSenseController Open(ControllerInfo info)
        {
            Opened.Add(info.Hid.Path);
            return _pads[info.Hid.Path] = new DualSenseController(info.Hid.Path,
                new ControllerDetails(info.Model, info.Link, null, null, ImuCalibration.Identity), _ => { });
        }
    }

    private sealed class CoreRig : IDisposable
    {
        private readonly DriverRig _driver = new();
        private readonly string _path;
        private readonly Link _link;

        public CoreRig(string path, Link link = Link.Usb, EmulationMode mode = EmulationMode.DualSense, bool confirmPulses = false,
            FakePlatform? fake = null, MappingConfig? mapping = null)
        {
            _path = path;
            _link = link;
            Server = new FakeServer(_driver.Log);
            var tree = new FakeTree { InstanceIds = { [path] = $@"TEST\{path}" } };
            Service = new ControllerService(Find, Open, _ => null);
            Core = new AppCore(_driver.Dir.Path, new FakeForeground(), Service, Time, runTimer: false,
                platform: fake?.Platform, drivers: new DriverPlatform(new FakeDrivers(), _driver.Vhci, _driver.Hider, _driver.NewRecovery(), tree),
                server: Server, steamRunning: () => false);
            Profile profile = Core.Profiles.Save(Core.ActiveProfile with
            {
                Emulation = new() { Mode = mode },
                Mapping = mapping is null ? Core.ActiveProfile.Mapping : mapping with { Enabled = true },
                Haptics = new() { Audio = AudioHapticSource.System, Clicks = true, ConfirmPulses = confirmPulses },
            });
            Core.Settings.Save(Core.Settings.Current with { DefaultProfileId = profile.Id });
        }

        public List<DualSenseController> Opened { get; } = [];
        public List<OutputState> Sent { get; } = [];
        public ControllerService Service { get; }
        public FakeServer Server { get; }
        public DriverRig Driver => _driver;
        public ManualTime Time { get; } = new();
        public AppCore Core { get; }

        public void Connect() => Service.ScanIfIdle();

        public void Dispose()
        {
            Core.Dispose();
            Service.Dispose();
            _driver.Dispose();
        }

        private ControllerInfo[] Find()
        {
            bool ds4 = _path != "1";
            return
            [
                new ControllerInfo(
                    new HidDeviceInfo(_path, DualSenseIds.SonyVendorId,
                        ds4 ? DualSenseIds.DualShock4V2ProductId : DualSenseIds.DualSenseProductId, 64, 64, 64),
                    ds4 ? ControllerModel.DualShock4 : ControllerModel.DualSense, _link),
            ];
        }

        private DualSenseController Open(ControllerInfo info)
        {
            var controller = new DualSenseController(info.Hid.Path,
                new ControllerDetails(info.Model, info.Link, null, null, ImuCalibration.Identity),
                output =>
                {
                    lock (Sent)
                    {
                        Sent.Add(output);
                    }
                });
            Opened.Add(controller);
            return controller;
        }
    }
}
