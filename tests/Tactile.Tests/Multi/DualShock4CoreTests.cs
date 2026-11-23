using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Device.Hid;
using Tactile.Drivers;
using Tactile.Emulation;
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

        public CoreRig(string path, Link link = Link.Usb, EmulationMode mode = EmulationMode.DualSense, bool confirmPulses = false)
        {
            _path = path;
            _link = link;
            Server = new FakeServer(_driver.Log);
            var tree = new FakeTree { InstanceIds = { [path] = $@"TEST\{path}" } };
            Service = new ControllerService(Find, Open, _ => null);
            Core = new AppCore(_driver.Dir.Path, new FakeForeground(), Service, Time, runTimer: false,
                drivers: new DriverPlatform(new FakeDrivers(), _driver.Vhci, _driver.Hider, _driver.NewRecovery(), tree),
                server: Server, steamRunning: () => false);
            Profile profile = Core.Profiles.Save(Core.ActiveProfile with
            {
                Emulation = new() { Mode = mode },
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
