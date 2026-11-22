using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Drivers;
using Tactile.Emulation;

namespace Tactile.Tests.EmulationCore;

[Collection("Localization")]
public class EmulationServiceTests
{
    private static readonly Profile Xbox = EmulationRig.ProfileWith(EmulationMode.Xbox360);
    private static readonly Profile DualSense = EmulationRig.ProfileWith(EmulationMode.DualSense);

    [Fact]
    public void Starts_in_order_create_attach_hide_and_stops_in_reverse()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();

        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.Equal(["create Xbox360 1-1", "attach 40123 1-1", $"hide {EmulationRig.RealId}"], rig.Log);
        EmulationStatus status = e.Status;
        Assert.True(status.Attached);
        Assert.True(status.Hidden);
        Assert.Equal(EmulationMode.Xbox360, status.Mode);

        rig.Clear();
        e.Update(Xbox, EmulationRig.Settings(), null);
        Assert.Equal([$"unhide {EmulationRig.RealId}", "detach 3", "remove 1-1"], rig.Log);
        Assert.False(e.Status.Attached);
        Assert.Equal(EmulationMode.Off, e.Status.Mode);
    }

    [Fact]
    public void Nothing_happens_while_off_disabled_or_drivers_missing()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();

        e.Update(EmulationRig.ProfileWith(EmulationMode.Off), EmulationRig.Settings(), EmulationRig.Bluetooth);
        e.Update(Xbox, EmulationRig.Settings(enabled: false), EmulationRig.Bluetooth);
        Assert.Empty(rig.Log);

        rig.Drivers.UsbIp = DriverState.Unsupported;
        e.RecheckDrivers();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.Empty(rig.Log);
        Assert.Contains(e.Status.Drivers, d => d is { Kind: DriverKind.UsbIp, State: DriverState.Unsupported, Version: "0.9.8.1" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gyro_stick_preserves_physical_axes_and_only_adds_its_active_offset(bool swap)
    {
        var rig = new EmulationRig();
        using EmulationService service = rig.Create();
        var shaping = new StickConfig { InvertX = true, InvertY = true, InnerDeadzone = 0.4 };
        Profile profile = Xbox with
        {
            Sticks = new StickPair { Left = shaping, Right = shaping },
            Emulation = Xbox.Emulation with { SwapSticks = swap },
        };
        AppSettings settings = EmulationRig.Settings();
        var input = Inputs.Make(left: new Stick(70, 90), right: new Stick(140, 180));
        Stick physical = swap ? input.Left : input.Right;
        service.Update(profile, settings, EmulationRig.Bluetooth);
        service.OnInput(input);
        var pad = rig.Server.Last!;
        Assert.Equal(physical, Assert.Single(pad.Submitted).Right);

        service.Update(profile with
        {
            Emulation = profile.Emulation with { GyroStick = new GyroStickConfig { Mode = GyroMouseMode.WhileHeld, Smoothing = 0 } },
        }, settings, EmulationRig.Bluetooth);
        var rate = new Vector3(0, -24, 0);
        service.OnInput(input, gyroDegPerSec: rate);
        Assert.Equal(physical, pad.Submitted[^1].Right);
        service.OnInput(input with { Buttons = Buttons.L2 }, gyroDegPerSec: rate);
        Assert.Equal(new Stick((byte)(physical.X + 26), physical.Y), pad.Submitted[^1].Right);
    }

    [Fact]
    public void Hide_switch_off_attaches_without_hiding_and_without_needing_hidhide()
    {
        var rig = new EmulationRig();
        rig.Drivers.HidHide = DriverState.Missing;
        using EmulationService e = rig.Create();

        e.Update(Xbox, EmulationRig.Settings(hide: false), EmulationRig.Bluetooth);
        Assert.Equal(["create Xbox360 1-1", "attach 40123 1-1"], rig.Log);
        Assert.False(e.Status.Hidden);

        // Missing HidHide with hiding wanted: emulation stays off rather than showing two pads.
        rig.Clear();
        e.Update(Xbox, EmulationRig.Settings(hide: true), EmulationRig.Bluetooth);
        Assert.Equal(["detach 3", "remove 1-1"], rig.Log);
    }

    [Fact]
    public void Turning_the_hide_switch_on_and_off_while_attached()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(hide: false), EmulationRig.Bluetooth);
        rig.Clear();

        e.Update(Xbox, EmulationRig.Settings(hide: true), EmulationRig.Bluetooth);
        Assert.Equal([$"hide {EmulationRig.RealId}"], rig.Log);
        rig.Clear();
        e.Update(Xbox, EmulationRig.Settings(hide: false), EmulationRig.Bluetooth);
        Assert.Equal([$"unhide {EmulationRig.RealId}"], rig.Log);
        Assert.True(e.Status.Attached);
    }

    [Fact]
    public void Mode_switch_between_profiles_reattaches()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        rig.Clear();

        e.Update(EmulationRig.ProfileWith(EmulationMode.DualSense, "22222222222222222222222222222222"), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.Equal([$"unhide {EmulationRig.RealId}", "detach 3", "remove 1-1", "create DualSense 1-2", "attach 40123 1-2", $"hide {EmulationRig.RealId}"], rig.Log);
        Assert.Equal(EmulationMode.DualSense, e.Status.Mode);
    }

    [Fact]
    public void Same_mode_on_another_profile_keeps_the_pad()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        rig.Clear();

        e.Update(EmulationRig.ProfileWith(EmulationMode.Xbox360, "22222222222222222222222222222222"), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void Dualsense_mode_is_not_offered_over_usb_but_xbox_is()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();

        e.Update(DualSense, EmulationRig.Settings(), EmulationRig.Usb);
        Assert.Empty(rig.Log);
        Assert.False(e.Status.Attached);

        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Usb);
        Assert.Equal("create Xbox360 1-1", rig.Log[0]);
    }

    [Fact]
    public void Virtual_dualsense_gets_the_real_identity()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        var identity = new DualSenseIdentity([0x05], [0x09], [0x20]);

        e.Update(DualSense, EmulationRig.Settings(), EmulationRig.Bluetooth with { Identity = identity });
        Assert.Same(identity, rig.Server.Last!.Identity);
    }

    [Fact]
    public void Attach_failure_is_a_notice_and_stays_off_until_retry_or_profile_change()
    {
        using var language = new TestLanguage(Language.Tr);
        var rig = new EmulationRig();
        rig.Vhci.Fail = true;
        using EmulationService e = rig.Create();

        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.Equal(["create Xbox360 1-1", "attach failed 1-1", "remove 1-1"], rig.Log);
        Assert.Single(rig.Notices, n => n.Kind == NoticeKinds.Emulation && n.Message.Contains("takılamadı"));
        Assert.NotNull(e.Status.Error);
        Assert.False(rig.Server.Last!.HasListeners);

        // Ticks and settings changes do not retry.
        rig.Clear();
        e.Update(Xbox, EmulationRig.Settings(hide: false), EmulationRig.Bluetooth);
        e.Reconcile();
        Assert.Empty(rig.Log);

        rig.Vhci.Fail = false;
        e.Retry();
        Assert.Equal(["create Xbox360 1-2", "attach 40123 1-2"], rig.Log);
        Assert.Null(e.Status.Error);
    }

    [Fact]
    public void Failure_clears_on_profile_change()
    {
        var rig = new EmulationRig();
        rig.Server.FailCreate = true;
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.False(e.Status.Attached);
        Assert.Single(rig.Notices);

        rig.Server.FailCreate = false;
        e.Update(EmulationRig.ProfileWith(EmulationMode.Xbox360, "22222222222222222222222222222222"), EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.True(e.Status.Attached);
    }

    [Fact]
    public void Hide_failure_keeps_the_pad_and_says_so()
    {
        using var language = new TestLanguage(Language.Tr);
        var rig = new EmulationRig();
        rig.Hider.Fail = true;
        using EmulationService e = rig.Create();

        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        Assert.True(e.Status.Attached);
        Assert.False(e.Status.Hidden);
        Assert.Single(rig.Notices, n => n.Message.Contains("gizlenemedi"));

        // Tried once per attach, not every tick; nothing to unhide on stop.
        rig.Clear();
        e.Reconcile();
        e.Update(Xbox, EmulationRig.Settings(), null);
        Assert.Equal(["detach 3", "remove 1-1"], rig.Log);
    }

    [Fact]
    public void Unclean_start_restores_first_with_a_notice()
    {
        var rig = new EmulationRig();
        rig.Recovery.UncleanShutdown = true;
        using EmulationService e = rig.Create();

        Assert.Equal(["restoreAll"], rig.Log);
        Assert.Single(rig.Notices, n => n.Kind == NoticeKinds.Emulation);
    }

    [Fact]
    public void Dispose_stops_then_marks_a_clean_exit()
    {
        var rig = new EmulationRig();
        EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        rig.Clear();

        e.Dispose();
        Assert.Equal([$"unhide {EmulationRig.RealId}", "detach 3", "remove 1-1", "cleanExit"], rig.Log);
        Assert.True(rig.Server.Disposed);
    }

    [Fact]
    public void Restore_removes_our_pad_then_replays_the_journal()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        rig.Clear();

        e.RestoreAll();
        Assert.Equal([$"unhide {EmulationRig.RealId}", "detach 3", "remove 1-1", "restoreAll"], rig.Log);
    }

    [Fact]
    public void Without_a_server_drivers_still_show_and_nothing_attaches()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create(withServer: false);
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);

        Assert.Empty(rig.Log);
        Assert.False(e.Status.Available);
        Assert.Equal(2, e.Status.Drivers.Count);
        Assert.NotNull(e.Status.Error);
    }

    [Fact]
    public void Input_is_transformed_and_submitted_while_attached()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        Profile p = Xbox with
        {
            Emulation = new EmulationConfig { Mode = EmulationMode.Xbox360, Remap = new Dictionary<string, string> { ["cross"] = "circle" }, SwapSticks = true },
            Mapping = new MappingConfig { Enabled = true, Buttons = new Dictionary<string, Binding> { ["square"] = new() { Press = ActionConfig.ForKeys(Tactile.Input.Key.A) } } },
        };

        e.OnInput(Inputs.Make(Buttons.Cross));
        e.Update(p, EmulationRig.Settings(), EmulationRig.Bluetooth);
        e.OnInput(Inputs.Make(Buttons.Cross | Buttons.Square, new Stick(1, 1), new Stick(9, 9)));

        InputState sent = Assert.Single(rig.Server.Last!.Submitted);
        Assert.Equal(Buttons.Circle, sent.Buttons);
        Assert.Equal(new Stick(9, 9), sent.Left);

        // Hiding mapped buttons can be turned off.
        e.Update(p with { Emulation = p.Emulation with { HideMappedButtons = false } }, EmulationRig.Settings(), EmulationRig.Bluetooth);
        e.OnInput(Inputs.Make(Buttons.Square));
        Assert.Equal(Buttons.Square, rig.Server.Last!.Submitted[^1].Buttons);
    }

    [Fact]
    public void Steam_notice_once_per_session()
    {
        var rig = new EmulationRig { SteamRunning = true };
        using EmulationService e = rig.Create();
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        e.Update(Xbox, EmulationRig.Settings(), null);
        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);

        Assert.Single(rig.Notices, n => n.Message.Contains("Steam"));
    }

    [Fact]
    public void Changed_fires_on_attach_and_detach()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        int changes = 0;
        e.Changed += () => changes++;

        e.Update(Xbox, EmulationRig.Settings(), EmulationRig.Bluetooth);
        e.Reconcile();
        e.Update(Xbox, EmulationRig.Settings(), null);
        Assert.Equal(2, changes);
    }
}
