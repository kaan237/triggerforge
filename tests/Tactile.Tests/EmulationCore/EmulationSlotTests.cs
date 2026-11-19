using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;
using Tactile.Tests.Drivers;
using Tactile.Tests.Multi;

namespace Tactile.Tests.EmulationCore;

[Collection("Localization")]
public sealed class EmulationSlotTests
{
    [Fact]
    public void Slots_share_the_server_and_journal_but_use_separate_ports_and_hide_entries()
    {
        using var rig = new SlotRig();
        rig.Connect();

        Assert.Equal([0, 1], rig.Core.Emulation!.Status.Slots.Select(s => s.Index));
        Assert.All(rig.Core.Emulation.Status.Slots, s => Assert.True(s.Attached && s.Hidden));
        AttachEntry[] attached = rig.Driver.Read().Attaches.ToArray();
        Assert.Equal(2, attached.Length);
        Assert.All(attached, entry => Assert.Equal(rig.Server.Port, entry.TcpPort));
        Assert.NotEqual(attached[0].BusId, attached[1].BusId);
        Assert.NotEqual(attached[0].HubPort, attached[1].HubPort);
        Assert.Equal([@"TEST\1", @"TEST\2"], rig.Driver.HidHide.Blacklist);
        Assert.Equal(DriverRig.Exe, Assert.Single(rig.Driver.HidHide.Whitelist));

        rig.Pads[2].PublishDisconnect();

        Assert.Same(rig.Server.Pads[0], rig.Server.Pads.Find(p => p.HasListeners));
        Assert.Equal(attached[0].HubPort, Assert.Single(rig.Driver.Read().Attaches).HubPort);
        Assert.Equal(@"TEST\1", Assert.Single(rig.Driver.HidHide.Blacklist));
        Assert.True(rig.Driver.HidHide.Active);
    }

    [Fact]
    public void Secondary_attach_failure_leaves_the_primary_attached_and_retry_only_recreates_the_secondary()
    {
        using var rig = new SlotRig();
        rig.Driver.VhciControl.DuringPlugIn = () =>
            rig.Driver.VhciControl.FailPlugIn = rig.Driver.VhciControl.Imported.Count == 1;
        rig.Connect();

        EmulationService emulation = rig.Core.Emulation!;
        Assert.True(emulation.Status.Attached && emulation.Status.Hidden);
        var failed = emulation.Status.Slots[1];
        Assert.False(failed.Attached);
        Assert.NotNull(failed.Error);
        Assert.Null(emulation.Status.Error);
        Assert.DoesNotContain(rig.Driver.Log, entry => entry.StartsWith("plugout ", StringComparison.Ordinal));

        rig.Driver.VhciControl.DuringPlugIn = null;
        rig.Driver.VhciControl.FailPlugIn = false;
        emulation.Retry();
        Assert.All(emulation.Status.Slots, s => Assert.True(s.Attached && s.Hidden));
        Assert.Equal(3, rig.Server.Pads.Count);
        Assert.True(rig.Server.Pads[0].HasListeners);
    }

    [Fact]
    public void Recovery_replays_all_slot_entries_and_preserves_foreign_devices()
    {
        using var rig = new SlotRig();
        rig.Connect();
        rig.Driver.HidHide.Blacklist.Add(@"USER\DEVICE");
        rig.Driver.VhciControl.Imported.Add(new ImportedDevice(20, "foreign", "3240", "127.0.0.1", 0, 0, 0));

        Recovery recovery = rig.Driver.NewRecovery();
        Assert.True(recovery.UncleanShutdown);
        recovery.RestoreAll();

        Assert.Equal(20, Assert.Single(rig.Driver.VhciControl.Imported).Port);
        Assert.Equal(@"USER\DEVICE", Assert.Single(rig.Driver.HidHide.Blacklist));
        Assert.Empty(rig.Driver.HidHide.Whitelist);
        Assert.False(File.Exists(rig.Driver.JournalPath));
    }

    [Fact]
    public void Secondary_input_and_feedback_stay_with_the_profile_and_physical_slot()
    {
        using var rig = new SlotRig();
        rig.Connect();
        Profile profile = rig.Core.Profiles.Save(rig.Core.ActiveProfile with
        {
            Id = "",
            Name = "Secondary",
            Rumble = new() { Strength = 0.5 },
            Sticks = new() { Left = new() { InvertX = true } },
            Emulation = new() { Mode = EmulationMode.DualSense },
        });
        rig.Core.SetSecondaryProfile(1, profile.Id);
        rig.Core.Refresh();
        FakePad primary = rig.Server.Pads[0];
        FakePad secondary = rig.Server.Pads[^1];
        Assert.Equal(VirtualKind.DualSense, secondary.Kind);

        rig.Pads[2].PublishInput(Inputs.Make(Buttons.Cross) with { Left = new Stick(255, 128) });
        Assert.Empty(primary.Submitted);
        InputState submitted = Assert.Single(secondary.Submitted);
        Assert.Equal(0, submitted.Left.X);
        Assert.Equal(Buttons.Cross, submitted.Buttons);

        byte[] game = new byte[OutputReport.StateLength];
        game[0] = 0x01;
        game[3] = 100;
        secondary.Raise(new DualSenseOutputFeedback(0, game));
        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Core.Refresh();
        Assert.Equal(50, rig.Pads[2].LastSent.RumbleLeft);
        Assert.Equal(0, rig.Pads[1].LastSent.RumbleLeft);

        primary.Raise(new RumbleFeedback(0, 170, 20));
        Assert.Equal(170, rig.Pads[1].LastSent.RumbleLeft);
        int beforeSwap = rig.Pads.Sent.Count;
        rig.Core.SetPrimaryController(FakePads.Mac(2));
        Assert.DoesNotContain(rig.Pads.Sent.Skip(beforeSwap), write => write.Pad == 2 && write.Output.RumbleLeft == 170);
        Assert.False(secondary.HasListeners);
        Assert.All(rig.Core.Emulation!.Status.Slots, s => Assert.True(s.Attached && s.Hidden));
        Assert.Equal([@"TEST\1", @"TEST\2"], rig.Driver.HidHide.Blacklist.Order());
        FakePad moved = rig.Server.Pads[^1];
        moved.Raise(new RumbleFeedback(0, 80, 20));
        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Core.Refresh();
        Assert.Equal(80, rig.Pads[1].LastSent.RumbleLeft);
        Assert.Equal(0, rig.Pads[2].LastSent.RumbleLeft);

        Assert.True(rig.Core.RestoreEmulation());
        Assert.All(rig.Core.Emulation.Status.Slots, s => Assert.False(s.Attached || s.Hidden));
        Assert.Empty(rig.Driver.VhciControl.Imported);
        Assert.Empty(rig.Driver.HidHide.Blacklist);
    }

    [Fact]
    public void Primary_disconnect_keeps_secondary_attached_until_the_slot_is_disabled()
    {
        using var rig = new SlotRig();
        rig.Connect();
        FakePad secondary = rig.Server.Pads[1];

        rig.Pads[1].PublishDisconnect();

        Assert.False(rig.Core.Emulation!.Status.Attached);
        Assert.True(rig.Core.Emulation.Status.Slots[1].Attached);
        Assert.Equal(secondary.BusId, Assert.Single(rig.Driver.Read().Attaches).BusId);
        Assert.True(secondary.HasListeners);

        rig.Core.Settings.Save(rig.Core.Settings.Current with { MaxControllers = 1 });

        Assert.Single(rig.Core.Emulation.Status.Slots);
        Assert.False(secondary.HasListeners);
        Assert.Empty(rig.Driver.Read().Attaches);
        Assert.Empty(rig.Driver.HidHide.Blacklist);
    }

    private sealed class SlotRig : IDisposable
    {
        public DriverRig Driver { get; } = new();
        public FakePads Pads { get; } = new();
        public FakeServer Server { get; }
        public ManualTime Time { get; } = new();
        public AppCore Core { get; }

        public SlotRig()
        {
            Server = new FakeServer(Driver.Log);
            var tree = new FakeTree { InstanceIds = { ["1"] = @"TEST\1", ["2"] = @"TEST\2" } };
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
}
