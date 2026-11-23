using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Integration;
using Tactile.Device.DualSense;

namespace Tactile.Tests.Integration;

[Collection("Localization")]
public sealed class IntegrationCoreTests
{
    [Fact]
    public void Layer_priority_is_independent_of_registration_order_and_stable_within_a_group()
    {
        using var dir = new TempDir();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            new ManualTime(), runTimer: false);
        core.Profiles.Save(core.ActiveProfile with { Lightbar = new LightbarConfig { Color = "#010000" } });

        void Set(string key, byte red) => core.SetOutputLayerNow(key, o => o with { Lightbar = new Rgb(red, 0, 0) });
        void Remove(string key) => core.SetOutputLayerNow(key, null);
        byte Red() => core.Controllers.Output.Lightbar.R;

        Assert.Equal(1, Red());
        Set("preview.lightbar", 6);
        Set("game.first", 4);
        Set("game.second", 5);
        Set(IntegrationLayers.Telemetry, 3);
        Set(IntegrationLayers.Udp, 2);
        Set("game.first", 7);

        Assert.Equal(6, Red());
        Remove("preview.lightbar");
        Assert.Equal(5, Red());
        Remove("game.second");
        Assert.Equal(7, Red());
        Remove("game.first");
        Assert.Equal(3, Red());
        Remove(IntegrationLayers.Telemetry);
        Assert.Equal(2, Red());
        Remove(IntegrationLayers.Udp);
        Assert.Equal(1, Red());
    }

    [Fact]
    public void Integration_settings_round_trip_clamp_and_keep_defaults_for_old_files()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        var settings = new IntegrationSettings
        {
            UdpEnabled = true,
            UdpPort = -1,
            Dsx = false,
            DsxPortFile = false,
            ReleaseAfterSeconds = 4000,
            ForzaEnabled = true,
            ForzaPort = 80000,
        };
        store.Save(store.Current with { Integration = settings });
        Assert.Equal(settings with { UdpPort = 1024, ForzaPort = 65535, ReleaseAfterSeconds = 3600 },
            new SettingsStore(dir.Path).Current.Integration);
        Assert.Equal(new IntegrationSettings(),
            JsonSerializer.Deserialize<AppSettings>("{}", TactileJson.Options)!.Integration);
        Assert.Equal(new IntegrationSettings(), Validation.Normalize(new AppSettings { Integration = null! }).Integration);

        var samePort = settings with { UdpPort = 5555, ForzaPort = 5555, ReleaseAfterSeconds = -1 };
        Assert.Equal(samePort with { ReleaseAfterSeconds = 0 },
            Validation.Normalize(new AppSettings { Integration = samePort }).Integration);
    }

    [Fact]
    public void Status_serializes_with_camel_case_names()
    {
        var status = new IntegrationStatus(new UdpStatus(UdpListenerState.PortBusy, 6969, 1, 2, 3, null, false, UdpFormat.Dsx),
            new ForzaStatus(ForzaListenerState.PortConflict, 6969, null, 0, 0, null));
        JsonElement json = JsonSerializer.SerializeToElement(status, TactileJson.Compact);
        JsonElement udp = json.GetProperty("udp");
        Assert.Equal("portBusy", udp.GetProperty("state").GetString());
        Assert.Equal("dsx", udp.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Null, udp.GetProperty("lastPacketAgoMs").ValueKind);
        JsonElement forza = json.GetProperty("forza");
        Assert.Equal("portConflict", forza.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, forza.GetProperty("values").ValueKind);
    }

    [Fact]
    public async Task Forza_uses_current_profile_and_hands_a_conflicting_port_to_udp()
    {
        using var dir = new TempDir();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            TimeProvider.System, runTimer: false);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int forzaPort;
        int udpPort;
        using (var forzaReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var udpReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            forzaPort = ((IPEndPoint)forzaReservation.Client.LocalEndPoint!).Port;
            udpPort = ((IPEndPoint)udpReservation.Client.LocalEndPoint!).Port;
        }

        var settings = new IntegrationSettings
        {
            UdpEnabled = true, UdpPort = udpPort, DsxPortFile = false,
            ForzaEnabled = true, ForzaPort = forzaPort,
        };
        Profile withoutRules = core.ActiveProfile;
        Profile withRules = core.Profiles.Save(new Profile { Name = "Forza", Telemetry = TelemetryTemplates.Forza });
        core.ActivateProfile(withRules.Id);
        core.Settings.Save(core.Settings.Current with { Integration = settings });
        Assert.Equal(ForzaListenerState.Listening, core.Integration.Forza.State);
        Assert.Equal(UdpListenerState.Listening, core.Integration.Udp.State);

        await sender.SendAsync(ForzaPacketTests.Packet().AsMemory(), new IPEndPoint(IPAddress.Loopback, forzaPort));
        await Until(() => core.Controllers.Output.RumbleRight == 120);
        core.ActivateProfile(withoutRules.Id);
        await Until(() => core.Controllers.Output.RumbleRight == 0);
        core.ActivateProfile(withRules.Id);
        await Until(() => core.Controllers.Output.RumbleRight == 120);

        core.Settings.Save(core.Settings.Current with { Integration = settings with { UdpPort = forzaPort } });
        Assert.Equal(ForzaListenerState.PortConflict, core.Integration.Forza.State);
        Assert.Equal(UdpListenerState.Listening, core.Integration.Udp.State);
        Assert.Equal(forzaPort, core.Integration.Udp.Port);
        await Until(() => core.Controllers.Output.RumbleRight == 0);
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
    public void Phase_13_settings_default_clamp_and_status_serialize_with_exact_names()
    {
        IntegrationSettings defaults = JsonSerializer.Deserialize<AppSettings>("""{"integration":{"forzaPort":5555}}""", TactileJson.Options)!.Integration;
        Assert.False(defaults.F1Enabled);
        Assert.Equal(20777, defaults.F1Port);
        Assert.False(defaults.AcEnabled);
        Assert.False(defaults.BeamNgEnabled);
        Assert.Equal(4444, defaults.BeamNgPort);
        IntegrationSettings clamped = Validation.Normalize(new AppSettings
        {
            Integration = new IntegrationSettings { F1Port = 10, BeamNgPort = 70000 },
        }).Integration;
        Assert.Equal(1024, clamped.F1Port);
        Assert.Equal(65535, clamped.BeamNgPort);

        var values = new TelemetryValues(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.5, 1);
        var status = new IntegrationStatus(
            new UdpStatus(UdpListenerState.Off, 6969, 0, 0, 0, null, false, null),
            new ForzaStatus(ForzaListenerState.Off, 5555, null, 0, 0, null), null,
            new F1Status(ForzaListenerState.Receiving, 20777, 2025, 4, 1, 2, values),
            new AcStatus(AcListenerState.Receiving, AcGame.Acc, 9, values),
            new BeamNgStatus(ForzaListenerState.PortConflict, 4444, 3, 0, null),
            TelemetrySource.BeamNg);
        JsonElement json = JsonSerializer.SerializeToElement(status, TactileJson.Compact);
        Assert.Equal("beamNg", json.GetProperty("activeSource").GetString());
        JsonElement f1 = json.GetProperty("f1");
        Assert.Equal("receiving", f1.GetProperty("state").GetString());
        Assert.Equal(2025, f1.GetProperty("format").GetInt32());
        Assert.Equal(2, f1.GetProperty("unsupported").GetInt32());
        Assert.Equal(0.5, f1.GetProperty("values").GetProperty("revLights").GetDouble());
        Assert.Equal(1, f1.GetProperty("values").GetProperty("drs").GetInt32());
        JsonElement ac = json.GetProperty("ac");
        Assert.Equal("receiving", ac.GetProperty("state").GetString());
        Assert.Equal("acc", ac.GetProperty("game").GetString());
        Assert.Equal(9, ac.GetProperty("packets").GetInt32());
        Assert.Equal("portConflict", json.GetProperty("beamNg").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("beamNg").GetProperty("values").ValueKind);
        foreach (TelemetrySource source in Enum.GetValues<TelemetrySource>())
        {
            Assert.Contains(JsonSerializer.Serialize(source, TactileJson.Compact), new[] { "\"forza\"", "\"f1\"", "\"acc\"", "\"ac\"", "\"beamNg\"" });
        }
    }

    [Fact]
    public void Core_status_carries_new_sources_and_dsu_yields_to_every_telemetry_port()
    {
        using var dir = new TempDir();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            TimeProvider.System, runTimer: false);
        int f1Port, beamPort, dsuPort;
        using (var a = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var c = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            f1Port = ((IPEndPoint)a.Client.LocalEndPoint!).Port;
            beamPort = ((IPEndPoint)b.Client.LocalEndPoint!).Port;
            dsuPort = ((IPEndPoint)c.Client.LocalEndPoint!).Port;
        }

        var settings = new IntegrationSettings
        {
            DsxPortFile = false, F1Enabled = true, F1Port = f1Port, BeamNgEnabled = true, BeamNgPort = beamPort,
            DsuEnabled = true, DsuPort = dsuPort,
        };
        core.Settings.Save(core.Settings.Current with { Integration = settings });
        Assert.Equal(ForzaListenerState.Listening, core.Integration.F1!.State);
        Assert.Equal(ForzaListenerState.Listening, core.Integration.BeamNg!.State);
        Assert.Equal(AcListenerState.Off, core.Integration.Ac!.State);
        Assert.Null(core.Integration.ActiveSource);
        Assert.Equal(DsuListenerState.Listening, core.Integration.Dsu!.State);

        core.Settings.Save(core.Settings.Current with { Integration = settings with { DsuPort = f1Port } });
        Assert.Equal(DsuListenerState.PortConflict, core.Integration.Dsu!.State);
        Assert.Equal(ForzaListenerState.Listening, core.Integration.F1!.State);
        core.Settings.Save(core.Settings.Current with { Integration = settings with { DsuPort = beamPort } });
        Assert.Equal(DsuListenerState.PortConflict, core.Integration.Dsu!.State);
        Assert.Equal(ForzaListenerState.Listening, core.Integration.BeamNg!.State);

        // Telemetry gives way to the UDP API on the same port.
        core.Settings.Save(core.Settings.Current with
        {
            Integration = settings with { DsuEnabled = false, UdpEnabled = true, UdpPort = f1Port },
        });
        Assert.Equal(ForzaListenerState.PortConflict, core.Integration.F1!.State);
        Assert.Equal(UdpListenerState.Listening, core.Integration.Udp.State);
        Assert.Equal(ForzaListenerState.Listening, core.Integration.BeamNg!.State);
    }
}
