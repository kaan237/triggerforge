using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Emulation;
using Tactile.Tests.EmulationCore;

namespace Tactile.Tests;

public sealed class GameAudioSettingsTests
{
    [Fact]
    public void Settings_and_profile_round_trip_validate_and_load_defaults()
    {
        AppSettings oldSettings = Load<AppSettings>("""{"emulation":{"enabled":false}}""");
        Assert.True(oldSettings.Emulation.Audio);
        Assert.True(oldSettings.Emulation.KeepAudioDefaults);
        Assert.True(Validation.Normalize(Load<AppSettings>("""{"emulation":null}""")).Emulation.KeepAudioDefaults);
        Assert.Equal(64, oldSettings.Emulation.HapticBuffer);
        Assert.Equal(GameAudioMode.Both, Load<Profile>("""{"haptics":{}}""").Haptics.GameAudio);
        Assert.Equal(1, Load<Profile>("{}").Haptics.GameStrength);
        Assert.False(Load<Profile>("{}").Haptics.GameLowPass);

        var settings = new AppSettings { Emulation = new EmulationSettings { Audio = false, KeepAudioDefaults = false, HapticBuffer = 100 } };
        Assert.Equal(settings.Emulation, RoundTrip(settings).Emulation);
        Assert.False(Validation.Normalize(settings).Emulation.KeepAudioDefaults);
        var invalidSettings = settings with { Emulation = settings.Emulation with { HapticBuffer = -1 } };
        Assert.Equal(16, Validation.Normalize(invalidSettings).Emulation.HapticBuffer);
        Assert.Equal(128, Validation.Normalize(Load<AppSettings>("""{"emulation":{"hapticBuffer":129}}""")).Emulation.HapticBuffer);

        var profile = new Profile
        {
            Haptics = new HapticsConfig { GameAudio = GameAudioMode.Haptics, GameStrength = 1.5, GameLowPass = true },
        };
        Assert.Equal(profile.Haptics, RoundTrip(profile).Haptics);
        HapticsConfig invalid = Load<Profile>("""{"haptics":{"gameAudio":"unknown","gameStrength":3}}""").Haptics;
        HapticsConfig normalized = Validation.Normalize(profile with { Haptics = invalid }).Haptics;
        Assert.Equal(GameAudioMode.Both, normalized.GameAudio);
        Assert.Equal(2, normalized.GameStrength);
        normalized = Validation.Normalize(profile with { Haptics = invalid with { GameStrength = double.NaN } }).Haptics;
        Assert.Equal(1, normalized.GameStrength);
    }

    [Fact]
    public void Buffer_length_updates_haptics_report_and_preserves_combined_report_layout()
    {
        var report = new HapticReport { BufferLength = 96 };
        Assert.Equal(new byte[] { 96, 96 }, report.Build(new sbyte[64])[5..7]);
        Assert.Equal(new byte[] { 64, 64, 64, 64, 64 }, report.BuildSpeaker(new sbyte[64], new byte[200])[5..10]);
    }

    [Fact]
    public void Audio_setting_recreates_only_the_dualsense_and_status_tracks_the_interface()
    {
        var rig = new EmulationRig();
        using var server = new UsbIpServer(0);
        using var emulation = new EmulationService(rig.Platform, server, rig.Host, () => TimeSpan.Zero,
            threaded: false, steamRunning: () => false);
        var profile = EmulationRig.ProfileWith(EmulationMode.DualSense);
        var settings = EmulationRig.Settings(hide: false);

        emulation.Update(profile, settings, EmulationRig.Bluetooth);
        var pad = Assert.IsType<VirtualDualSense.WithAudio>(emulation.VirtualAudio);
        Assert.Equal(EmulationAudioStatus.Ready, emulation.Status.Audio);
        int changes = 0;
        emulation.Changed += () => changes++;
        Assert.Equal(0, pad.Control(new SetupPacket(1, 0x0B, 1, 1, 0), [], 0).Status);
        emulation.Reconcile();
        Assert.Equal(1, changes);
        Assert.Equal(EmulationAudioStatus.Playing, emulation.Status.Audio);

        rig.Clear();
        settings = settings with { Emulation = settings.Emulation with { HapticBuffer = 32 } };
        emulation.Update(profile, settings, EmulationRig.Bluetooth);
        Assert.Same(pad, emulation.VirtualAudio);
        Assert.Empty(rig.Log);
        settings = settings with { Emulation = settings.Emulation with { Audio = false } };
        emulation.Update(profile, settings, EmulationRig.Bluetooth);
        Assert.True(emulation.Status.Attached);
        Assert.Null(emulation.VirtualAudio);
        Assert.Equal(EmulationAudioStatus.Off, emulation.Status.Audio);
        Assert.Equal("detach 3", rig.Log[0]);
        Assert.StartsWith("attach ", rig.Log[1]);

        var xbox = EmulationRig.ProfileWith(EmulationMode.Xbox360);
        emulation.Update(xbox, settings, EmulationRig.Bluetooth);
        rig.Clear();
        emulation.Update(xbox, EmulationRig.Settings(hide: false), EmulationRig.Bluetooth);
        Assert.Empty(rig.Log);
    }

    private static T Load<T>(string json) => JsonSerializer.Deserialize<T>(json, TactileJson.Options)!;
    private static T RoundTrip<T>(T value) => Load<T>(JsonSerializer.Serialize(value, TactileJson.Options));
}
