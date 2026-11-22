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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Flick_centers_the_virtual_stick_only_while_mapping_is_enabled_and_active(bool enabled, bool active)
    {
        var rig = new EmulationRig();
        using EmulationService service = rig.Create();
        Profile profile = Xbox with { Mapping = new() { Enabled = enabled, FlickStick = new() { Enabled = true } } };
        InputState input = Inputs.Make(right: new Stick(200, 40));
        service.Update(profile, EmulationRig.Settings(), EmulationRig.Bluetooth);
        service.OnInput(input, mappingActive: active);
        Assert.Equal(enabled && active ? new Stick(128, 128) : input.Right,
            Assert.Single(rig.Server.Last!.Submitted).Right);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DualShock4_mode_attaches_over_bluetooth_and_usb_and_overlays_rumble_and_lightbar(bool usb)
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        Profile profile = EmulationRig.ProfileWith(EmulationMode.DualShock4) with { Rumble = new RumbleConfig { Strength = 0.5 } };

        e.Update(profile, EmulationRig.Settings(), usb ? EmulationRig.Usb : EmulationRig.Bluetooth);

        FakePad pad = Assert.Single(rig.Server.Pads);
        Assert.Equal(VirtualKind.DualShock4, pad.Kind);
        Assert.Equal(EmulationMode.DualShock4, e.Status.Mode);
        Assert.True(e.Status.Attached);

        pad.Raise(new DualShock4Feedback(0, HasRumble: true, Large: 200, Small: 100, HasLightbar: true, new Rgb(1, 2, 3)));
        OutputState o = rig.Apply(OutputState.Default);
        Assert.Equal(100, o.RumbleLeft);
        Assert.Equal(50, o.RumbleRight);
        Assert.Equal(new Rgb(1, 2, 3), o.Lightbar);

        // A report that only sets the lightbar keeps the rumble the game set before.
        pad.Raise(new DualShock4Feedback(0, HasRumble: false, 0, 0, HasLightbar: true, new Rgb(9, 8, 7)));
        o = rig.Apply(OutputState.Default);
        Assert.Equal((100, 50, new Rgb(9, 8, 7)), (o.RumbleLeft, o.RumbleRight, o.Lightbar));
    }

    [Fact]
    public void Auto_mode_resolves_built_in_dualsense_games_user_overrides_and_xbox_fallback()
    {
        IReadOnlySet<string> builtIn = AutoEmulationResolver.BuiltInDualSenseGames;
        Assert.NotEmpty(builtIn);
        // The embedded list is in canonical form: lower-case file names with .exe, nothing else.
        Assert.All(builtIn, name => Assert.Equal(AutoEmulationResolver.NormalizeExe(name), name));
        Assert.Contains("ghostoftsushima.exe", builtIn);
        Assert.Contains("returnal-win64-shipping.exe", builtIn);

        Assert.Equal(EmulationMode.DualSense, AutoEmulationResolver.Resolve(EmulationMode.Auto, "ghostoftsushima.exe", null));
        Assert.Equal(EmulationMode.DualSense, AutoEmulationResolver.Resolve(EmulationMode.Auto, @"C:\Games\GhostOfTsushima.exe", null));
        Assert.Equal(EmulationMode.DualSense, AutoEmulationResolver.Resolve(EmulationMode.Auto, "returnal-win64-shipping.exe", null));
        Assert.Equal(EmulationMode.Xbox360, AutoEmulationResolver.Resolve(EmulationMode.Auto, "eldenring.exe", null));
        Assert.Equal(EmulationMode.Xbox360, AutoEmulationResolver.Resolve(EmulationMode.Auto, null, null));
        Assert.Equal(EmulationMode.DualShock4, AutoEmulationResolver.Resolve(EmulationMode.DualShock4, "ghostoftsushima.exe", null));

        var overrides = new Dictionary<string, EmulationMode>
        {
            ["eldenring.exe"] = EmulationMode.DualShock4,
            ["ghostoftsushima.exe"] = EmulationMode.Xbox360,
            [" Returnal-Win64-Shipping.exe "] = EmulationMode.Off,
        };
        Assert.Equal(EmulationMode.DualShock4, AutoEmulationResolver.Resolve(EmulationMode.Auto, "EldenRing.exe", overrides));
        Assert.Equal(EmulationMode.Xbox360, AutoEmulationResolver.Resolve(EmulationMode.Auto, "ghostoftsushima.exe", overrides));
        Assert.Equal(EmulationMode.Off, AutoEmulationResolver.Resolve(EmulationMode.Auto, "returnal-win64-shipping.exe", overrides));
        Assert.Equal(EmulationMode.Xbox360, AutoEmulationResolver.Resolve(EmulationMode.Auto, "discord.exe", overrides));

        Assert.True(AutoEmulationResolver.IsKnownGame("ghostoftsushima.exe", null));
        Assert.True(AutoEmulationResolver.IsKnownGame("eldenring.exe", overrides));
        Assert.False(AutoEmulationResolver.IsKnownGame("discord.exe", overrides));
    }

    [Fact]
    public void Auto_mode_reattaches_only_when_settled_game_changes_and_submits_neutral_input_first()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        Profile auto = EmulationRig.ProfileWith(EmulationMode.Auto);
        AppSettings settings = EmulationRig.Settings();

        // Nothing seen yet: Xbox 360.
        e.Update(auto, settings, EmulationRig.Bluetooth, null);
        Assert.Equal(VirtualKind.Xbox360, Assert.Single(rig.Server.Pads).Kind);

        e.Update(auto, settings, EmulationRig.Bluetooth, "ghostoftsushima.exe");
        Assert.Equal([VirtualKind.Xbox360, VirtualKind.DualSense], rig.Server.Pads.Select(p => p.Kind));
        FakePad xbox = rig.Server.Pads[0];
        InputState last = xbox.Submitted[^1];
        Assert.Equal((Buttons.None, (byte)128, (byte)128, (byte)128, (byte)128, (byte)0, (byte)0),
            (last.Buttons, last.Left.X, last.Left.Y, last.Right.X, last.Right.Y, last.L2, last.R2));
        Assert.True(rig.Log.IndexOf("remove 1-1") < rig.Log.IndexOf("create DualSense 1-2"));

        // Alt+Tab to chat, a browser, the shell or no window at all keeps the game's pad.
        rig.Clear();
        foreach (string? other in new[] { null, "explorer.exe", "discord.exe", "chrome.exe" })
        {
            e.Update(auto, settings, EmulationRig.Bluetooth, other);
        }

        Assert.Empty(rig.Log);
        Assert.Equal(EmulationMode.DualSense, e.Status.Mode);
        Assert.Equal("ghostoftsushima.exe", e.Status.AutoGame);

        // A game with a profile rule that is not on the list does start a new resolution.
        e.Update(auto, settings, EmulationRig.Bluetooth, "eldenring.exe", knownGame: true);
        Assert.Equal(VirtualKind.Xbox360, rig.Server.Pads[^1].Kind);
        Assert.Equal(3, rig.Server.Pads.Count);
        Assert.Equal("eldenring.exe", e.Status.AutoGame);

        // A user override makes an app known; only the file name is kept.
        AppSettings withOverride = new()
        {
            Emulation = settings.Emulation with
            {
                GameModes = new Dictionary<string, EmulationMode> { ["emu.exe"] = EmulationMode.DualShock4 },
            },
        };
        e.Update(auto, withOverride, EmulationRig.Bluetooth, @"D:\Tools\Emu.exe");
        Assert.Equal(VirtualKind.DualShock4, rig.Server.Pads[^1].Kind);
        Assert.Equal("Emu.exe", e.Status.AutoGame);
    }

    [Fact]
    public void Auto_on_a_wired_dualsense_reports_the_resolved_mode_without_attaching()
    {
        var rig = new EmulationRig();
        using EmulationService e = rig.Create();
        e.Update(EmulationRig.ProfileWith(EmulationMode.Auto), EmulationRig.Settings(), EmulationRig.Usb, "ghostoftsushima.exe");

        Assert.Empty(rig.Server.Pads);
        Assert.Equal((EmulationMode.DualSense, false), (e.Status.Mode, e.Status.Attached));
        Assert.Equal(EmulationMode.DualSense, e.Status.Slots[0].Mode);
    }
}
