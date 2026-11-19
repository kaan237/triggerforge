using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests.Multi;

[Collection("Localization")]
public sealed class SecondaryControllersTests
{
    [Fact]
    public void Profile_choices_follow_saved_profiles_and_stay_local_to_the_session()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [1, 2, 3] };
        var clock = new ManualTime();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, clock, runTimer: false);
        Profile fallback = core.ActiveProfile;
        Profile defaults = core.Profiles.Save(new Profile { Name = "Controller default", Lightbar = new() { Color = "#123456" } });
        Profile session = core.Profiles.Save(new Profile { Name = "Session" });
        core.Settings.Save(core.Settings.Current with
        {
            DefaultProfileId = fallback.Id,
            ControllerDefaults = new Dictionary<string, string> { [FakePads.Mac(2)] = defaults.Id },
        });
        var connected = new List<int>();
        var disconnected = new List<int>();
        var changed = new List<string>();
        SecondaryControllers pipeline = core.SecondaryControllers!;
        pipeline.Connected += (slot, _) => connected.Add(slot.Index);
        pipeline.Disconnected += slot => disconnected.Add(slot.Index);
        pipeline.ProfileChanged += (_, profile) => changed.Add(profile.Name);

        pads.Service.ScanIfIdle();
        ControllerSlot slot = pads.Service.Slots[1];
        Assert.Equal([1, 2], connected);
        Assert.Equal(defaults.Id, core.ProfileForController(slot).Id);
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), pads[2].LastSent.Lightbar);
        Assert.Equal(fallback.Id, core.ProfileForController(pads.Service.Slots[2]).Id);

        core.SetSecondaryProfile(1, session.Id);
        Assert.Equal(session.Id, core.ProfileForController(slot).Id);
        Assert.Equal(defaults.Id, new SettingsStore(dir.Path).Current.ControllerDefaults[FakePads.Mac(2)]);
        pads[2].PublishDisconnect();
        pads.Service.ScanIfIdle();
        slot = pads.Service.Slots[1];
        Assert.Equal([1], disconnected);
        Assert.Equal([1, 2, 1], connected);
        Assert.Equal(session.Id, core.ProfileForController(slot).Id);

        core.Profiles.Delete(session.Id);
        Assert.Equal(defaults.Id, core.ProfileForController(slot).Id);
        core.Profiles.Save(defaults with { Name = "Updated" });
        Assert.Equal("Updated", core.ProfileForController(slot).Name);
        Assert.Equal(["Session", "Controller default", "Updated"], changed);

        pads.Present = [2, 3];
        pads[1].PublishDisconnect();
        core.ActivateProfile(defaults.Id);
        Assert.Equal(fallback.Id, core.ProfileForController(pads.Service.Slots[1]).Id);
        core.Settings.Save(core.Settings.Current with { ControllerDefaults = new Dictionary<string, string>() });
        Assert.Equal(fallback.Id, core.ProfileForController(slot).Id);
    }

    [Fact]
    public void Four_slots_use_console_leds_and_an_explicit_profile_overrides_automatic_assignment()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [1, 2, 3, 4] };
        var clock = new ManualTime();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, clock, runTimer: false);
        core.Settings.Save(core.Settings.Current with { DefaultProfileId = core.ActiveProfile.Id });
        pads.Service.ScanIfIdle();
        Assert.Equal(new byte[] { 0x04, 0x0A, 0x15, 0x1B }, pads.Service.Slots.Select(s => s.Controller.LastSent.PlayerLeds));

        Profile custom = core.Profiles.Save(new Profile { Name = "Custom", PlayerLeds = new() { Mode = PlayerLedMode.Custom, Mask = 3 } });
        clock.Advance(TimeSpan.FromMilliseconds(10));
        core.SetSecondaryProfile(2, custom.Id);
        Assert.Equal(3, pads[3].LastSent.PlayerLeds);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        core.Settings.Save(core.Settings.Current with { AutoPlayerLeds = false });
        Assert.Equal(new byte[] { 4, 4, 3, 4 }, pads.Service.Slots.Select(s => s.Controller.LastSent.PlayerLeds));

        clock.Advance(TimeSpan.FromMilliseconds(10));
        core.Settings.Save(core.Settings.Current with { AutoPlayerLeds = true });
        pads[1].PublishDisconnect();
        pads[3].PublishDisconnect();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        pads[4].PublishDisconnect();
        Assert.Equal(4, pads[2].LastSent.PlayerLeds);
    }

    [Theory]
    [InlineData("""{"mode":"number","number":1}""", 0x0A)]
    [InlineData("""{"mode":"number","number":1,"automatic":true}""", 0x0A)]
    [InlineData("""{"mode":"number","number":1,"automatic":false}""", 0x04)]
    public void Legacy_and_explicit_led_choices_survive_profile_serialization(string json, byte expected)
    {
        PlayerLedConfig choice = JsonSerializer.Deserialize<PlayerLedConfig>(json, TactileJson.Options)!;
        using var dir = new TempDir();
        Profile saved = new ProfileStore(dir.Path).Save(new Profile { Name = "LED choice", PlayerLeds = choice });
        Profile profile = new ProfileStore(dir.Path).Get(saved.Id)!;
        Assert.Equal(expected, ProfileOutput.Build(profile, ProfileOutput.TriggersFor(profile), TimeSpan.Zero, null, 2).PlayerLeds);
        Assert.Equal(0x04, ProfileOutput.Build(profile, ProfileOutput.TriggersFor(profile), TimeSpan.Zero, null, null).PlayerLeds);
    }

    [Fact]
    public void Primary_swap_cancels_calibration_and_discards_the_old_gyro_bias()
    {
        using var dir = new TempDir();
        using var pads = new FakePads();
        var time = new ManualTime();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, time, runTimer: false);
        pads.Service.ScanIfIdle();
        core.StartGyroCalibration();
        for (int i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            pads[1].PublishInput(Inputs.Make(gyro: new Vector3(512, 0, 0)));
        }
        Assert.Equal(0.5f, core.GyroBias.X, 0.001f);

        var notices = new List<Notice>();
        core.Notice += notices.Add;
        core.StartGyroCalibration();
        pads[1].PublishInput(Inputs.Make(gyro: new Vector3(512, 0, 0)));
        core.SetPrimaryController(FakePads.Mac(2));
        Assert.False(core.GyroCalibrating);
        Assert.Equal(default, core.GyroBias);
        Assert.Contains(notices, n => n.EnglishMessage.Contains("primary controller changed"));

        time.Advance(TimeSpan.FromSeconds(4));
        pads[2].PublishInput(Inputs.Make(gyro: new Vector3(1024, 0, 0)));
        Assert.Equal(default, core.GyroBias);
    }

    [Fact]
    public void Saved_primary_swaps_slots_and_only_primary_input_reaches_mapping()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [1, 2, 3] };
        var platform = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, platform.Platform);
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = new Dictionary<string, Binding>
                {
                    ["cross"] = new() { Press = ActionConfig.ForKeys(Key.A) },
                },
            },
        });
        pads.Service.ScanIfIdle();
        var secondaryInputs = new List<(int Index, Buttons Buttons)>();
        core.SecondaryControllers!.Input += (slot, input) => secondaryInputs.Add((slot.Index, input.Buttons));

        pads[2].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.Empty(platform.Injector.Take());
        Assert.Equal((1, Buttons.Cross), Assert.Single(secondaryInputs));
        pads[1].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], platform.Injector.Take());

        core.SetPrimaryController(FakePads.Mac(2).ToLowerInvariant());
        Assert.Equal(FakePads.Mac(2), new SettingsStore(dir.Path).Current.PrimaryController);
        Assert.Same(pads[2], pads.Service.Controller);
        Assert.Same(pads[1], pads.Service.Slots[1].Controller);
        Assert.Same(pads[3], pads.Service.Slots[2].Controller);
        Assert.Equal(["A up"], platform.Injector.Take());

        pads[1].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.Empty(platform.Injector.Take());
        pads[2].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], platform.Injector.Take());
    }

    [Fact]
    public async Task Blocked_secondary_write_does_not_block_primary_key_release_or_selection()
    {
        using var dir = new TempDir();
        using var pads = new FakePads { Present = [1, 3] };
        var platform = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false, platform.Platform);
        core.Profiles.Save(core.ActiveProfile with
        {
            Mapping = new MappingConfig
            {
                Enabled = true,
                Buttons = new Dictionary<string, Binding> { ["cross"] = new() { Press = ActionConfig.ForKeys(Key.A) } },
            },
        });
        pads.Service.ScanIfIdle();
        pads[1].PublishInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], platform.Injector.Take());

        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        pads.Writing = (id, _) =>
        {
            if (id == 2)
            {
                writing.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        pads.Present = [1, 3, 2];
        Task scan = Task.Run(pads.Service.ScanIfIdle);
        try
        {
            Assert.True(await Task.Run(() => writing.Wait(TimeSpan.FromSeconds(5))));
            await Task.Run(() => pads[1].PublishInput(Inputs.Make())).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["A up"], platform.Injector.Take());
            await Task.Run(() => core.SetPrimaryController(FakePads.Mac(3))).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Same(pads[3], pads.Service.Controller);
        }
        finally
        {
            release.Set();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            pads.Writing = null;
        }
    }

    [Fact]
    public void Profile_command_checks_the_requested_mac_after_slot_reuse()
    {
        using var dir = new TempDir();
        using var pads = new FakePads();
        using var core = new AppCore(dir.Path, new FakeForeground(), pads.Service, new ManualTime(), runTimer: false);
        Profile choice = core.Profiles.Save(new Profile { Name = "Session choice" });
        pads.Service.ScanIfIdle();
        pads.Present = [1, 3];
        pads[2].PublishDisconnect();
        Assert.False(core.TrySetSecondaryProfile(1, FakePads.Mac(2), choice.Id));
        pads.Service.ScanIfIdle();

        ControllerSlot replacement = pads.Service.Slots[1];
        Assert.False(core.TrySetSecondaryProfile(1, FakePads.Mac(2), choice.Id));
        Assert.NotEqual(choice.Id, core.ProfileForController(replacement).Id);
        Assert.True(core.TrySetSecondaryProfile(1, FakePads.Mac(3).ToLowerInvariant(), choice.Id));
        Assert.Equal(choice.Id, core.ProfileForController(replacement).Id);
    }

    [Fact]
    public void Secondary_udp_and_game_layers_are_ordered_and_output_is_coalesced_per_slot()
    {
        using var dir = new TempDir();
        using var pads = new FakePads();
        var profiles = new ProfileStore(dir.Path);
        Profile profile = profiles.Save(profiles.All[0] with { Rumble = new() { Strength = 0.5 } });
        var settings = new SettingsStore(dir.Path);
        UdpState udp = UdpState.Empty;
        using var pipeline = new SecondaryControllers(pads.Service, profiles, settings,
            _ => udp, () => TimeSpan.Zero, () => profile, () => { }, _ => { });
        pads.Service.ScanIfIdle();
        ControllerSlot slot = pads.Service.Slots[1];
        pipeline.Update(TimeSpan.Zero, profile, settings.Current);
        Assert.Equal(0x0A, slot.Controller.LastSent.PlayerLeds);

        udp = new UdpState(PlayerLeds: 7, RumbleLeft: 100);
        pipeline.Update(TimeSpan.FromMilliseconds(9), profile, settings.Current);
        Assert.Equal(0x0A, slot.Controller.LastSent.PlayerLeds);
        pipeline.Update(TimeSpan.FromMilliseconds(10), profile, settings.Current);
        Assert.Equal(7, slot.Controller.LastSent.PlayerLeds);
        Assert.Equal(100, slot.Controller.LastSent.RumbleLeft);

        pipeline.SetGameFeedback(slot, OutputState.Default with { RumbleLeft = 80 }, OutputFields.Rumble);
        pipeline.Update(TimeSpan.FromMilliseconds(20), profile, settings.Current);
        Assert.Equal(7, slot.Controller.LastSent.PlayerLeds);
        Assert.Equal(40, slot.Controller.LastSent.RumbleLeft);
        pipeline.Update(TimeSpan.FromMilliseconds(30), profile, settings.Current);
        Assert.Equal(3, pads.Sent.Count(s => s.Pad == 2));
    }
}
