using System.Net;
using System.Net.Sockets;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;
using Tactile.Input;

namespace Tactile.Tests.EmulationCore;

[Collection("Localization")]
public sealed class AutoEmulationFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dsu_releases_its_port_before_the_new_listener_binds(bool useForza)
    {
        using var dir = new TempDir();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            new ManualTime(), runTimer: false);
        IntegrationSettings integration = core.Settings.Current.Integration;
        int port;
        do
        {
            using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        }
        while (port == integration.UdpPort || port == integration.ForzaPort);

        integration = integration with { DsuEnabled = true, DsuPort = port, DsxPortFile = false };
        core.Settings.Save(core.Settings.Current with { Integration = integration });
        Assert.Equal(DsuListenerState.Listening, core.Integration.Dsu?.State);

        integration = useForza
            ? integration with { ForzaEnabled = true, ForzaPort = port }
            : integration with { UdpEnabled = true, UdpPort = port };
        core.Settings.Save(core.Settings.Current with { Integration = integration });

        Assert.Equal(DsuListenerState.PortConflict, core.Integration.Dsu?.State);
        if (useForza)
        {
            Assert.Equal(ForzaListenerState.Listening, core.Integration.Forza.State);
        }
        else
        {
            Assert.Equal(UdpListenerState.Listening, core.Integration.Udp.State);
        }
    }

    [Fact]
    public void Configured_mode_and_game_changes_notify_without_replacing_the_pad()
    {
        var rig = new EmulationRig();
        using EmulationService service = rig.Create();
        Profile xbox = EmulationRig.ProfileWith(EmulationMode.Xbox360);
        Profile auto = EmulationRig.ProfileWith(EmulationMode.Auto);
        AppSettings settings = EmulationRig.Settings();
        service.Update(xbox, settings, EmulationRig.Bluetooth, "first-game.exe");
        var changes = new List<EmulationStatus>();
        service.Changed += () => changes.Add(service.Status);

        service.Update(auto, settings, EmulationRig.Bluetooth, "first-game.exe", knownGame: true);
        service.Update(auto, settings, EmulationRig.Bluetooth, "second-game.exe", knownGame: true);
        service.Update(auto, settings, EmulationRig.Bluetooth, "second-game.exe", knownGame: true);

        Assert.Collection(changes,
            status => Assert.Equal((EmulationMode.Auto, EmulationMode.Xbox360, "first-game.exe"),
                (status.ConfiguredMode, status.Mode, status.AutoGame)),
            status => Assert.Equal((EmulationMode.Auto, EmulationMode.Xbox360, "second-game.exe"),
                (status.ConfiguredMode, status.Mode, status.AutoGame)));
        Assert.Single(rig.Server.Pads);
        Assert.DoesNotContain(rig.Log, entry => entry.StartsWith("remove ", StringComparison.Ordinal));
    }

    [Fact]
    public void First_tick_keeps_mapped_keys_held_and_disabling_emulation_releases_them()
    {
        using var dir = new TempDir();
        var fake = new FakePlatform();
        var rig = new EmulationRig();
        Profile profile = new ProfileStore(dir.Path).Save(EmulationRig.ProfileWith(EmulationMode.Xbox360) with
        {
            Mapping = new() { Enabled = true, Buttons = Bind.Map(("cross", Bind.Key(Key.A))) },
        });
        var settings = new SettingsStore(dir.Path);
        settings.Save(settings.Current with { DefaultProfileId = profile.Id });
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            new ManualTime(), runTimer: false, platform: fake.Platform, drivers: rig.Platform, server: rig.Server,
            steamRunning: () => false);

        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Equal(["A down"], fake.Injector.Take());
        core.Tick(TimeSpan.Zero);
        Assert.Empty(fake.Injector.Take());

        core.Settings.Save(core.Settings.Current with { Emulation = core.Settings.Current.Emulation with { Enabled = false } });

        Assert.Equal(["A up"], fake.Injector.Take());

        // The key is still held on the controller: that is not a new press for the mapping.
        core.OnInput(Inputs.Make(Buttons.Cross));
        Assert.Empty(fake.Injector.Take());
    }

    [Fact]
    public void Ambient_paths_use_the_file_name_and_the_last_game_is_trimmed()
    {
        var rig = new EmulationRig();
        using EmulationService service = rig.Create();
        Profile auto = EmulationRig.ProfileWith(EmulationMode.Auto);
        AppSettings settings = EmulationRig.Settings();
        service.Update(auto, settings, EmulationRig.Bluetooth, "  first-game.exe  ", knownGame: true);
        Assert.Equal("first-game.exe", service.Status.AutoGame);

        service.Update(auto, settings, EmulationRig.Bluetooth, Path.Combine("Windows", "EXPLORER.EXE"));
        Assert.Equal("first-game.exe", service.Status.AutoGame);

        service.Update(auto, settings, EmulationRig.Bluetooth, " explorer ");
        Assert.Equal("first-game.exe", service.Status.AutoGame);
    }

    [Theory]
    [InlineData(true, EmulationMode.Xbox360)]
    [InlineData(false, EmulationMode.DualSense)]
    public void Unknown_foreground_exe_counts_as_a_game_only_when_full_screen(bool fullscreen, EmulationMode expected)
    {
        using var dir = new TempDir();
        var rig = new EmulationRig();
        Profile profile = new ProfileStore(dir.Path).Save(EmulationRig.ProfileWith(EmulationMode.Auto));
        var settings = new SettingsStore(dir.Path);
        settings.Save(settings.Current with { DefaultProfileId = profile.Id });
        var fg = new FakeForeground();
        using var core = new AppCore(dir.Path, fg, new ControllerService(autoConnect: false),
            new ManualTime(), runTimer: false, drivers: rig.Platform, server: rig.Server, steamRunning: () => false);

        fg.Set("ghostoftsushima.exe");
        core.Tick(TimeSpan.Zero);
        core.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal(EmulationMode.DualSense, core.Emulation!.ResolvedMode);

        fg.IsFullscreen = fullscreen;
        fg.Set("eldenring.exe");
        core.Tick(TimeSpan.FromSeconds(11));
        core.Tick(TimeSpan.FromSeconds(20));
        Assert.Equal(expected, core.Emulation.ResolvedMode);
    }
}
