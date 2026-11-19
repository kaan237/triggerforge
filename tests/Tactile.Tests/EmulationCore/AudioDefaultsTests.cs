using Tactile.Audio;
using Tactile.Core;

namespace Tactile.Tests.EmulationCore;

[Collection("Localization")]
public sealed class AudioDefaultsTests
{
    private static readonly Profile DualSense = EmulationRig.ProfileWith(EmulationMode.DualSense);
    private static readonly AudioDefaultSlot Render = new(AudioFlow.Render, AudioRole.Console);
    private static readonly AudioDefaultSlot Capture = new(AudioFlow.Capture, AudioRole.Communications);
    private static readonly AudioDefaultEndpoint DualSenseEndpoint = new("virtual", "Wireless Controller (DualSense)");

    [Fact]
    public void Snapshots_before_attach_and_restores_each_changed_role_only_once()
    {
        var rig = new EmulationRig();
        var audio = new FakeAudioDefaults(rig.Log);
        foreach (AudioFlow flow in Enum.GetValues<AudioFlow>())
        foreach (AudioRole role in Enum.GetValues<AudioRole>())
            audio.Endpoints[new(flow, role)] = new($"{flow}-{role}", "Headset");

        using var service = rig.Create(audioDefaults: audio);
        service.Update(DualSense, EmulationRig.Settings(hide: false), EmulationRig.Bluetooth);
        Assert.Equal(["create DualSense 1-1", "snapshot", "attach 40123 1-1"], rig.Log);

        audio.Endpoints[Render] = DualSenseEndpoint with { FriendlyName = "Speakers (dualsense)" };
        audio.Endpoints[Capture] = new("different-mic", "USB microphone");
        Step(rig, service, 499);
        Assert.Equal(0, audio.Polls);
        Step(rig, service, 500);
        Assert.Equal([("Render-Console", AudioRole.Console)], audio.Restores);

        foreach (AudioDefaultSlot slot in audio.Endpoints.Keys.ToArray())
            audio.Endpoints[slot] = DualSenseEndpoint with { Id = $"virtual-{slot.Flow}" };

        Step(rig, service, 999);
        Assert.Equal(1, audio.Polls);
        Step(rig, service, 1000);
        Assert.Equal(6, audio.Restores.Count);
        foreach (AudioDefaultSlot slot in audio.Endpoints.Keys)
            Assert.Contains(($"{slot.Flow}-{slot.Role}", slot.Role), audio.Restores);

        Step(rig, service, 1500);
        Assert.Equal(2, audio.Polls);
        Assert.Equal(6, audio.Restores.Count);
    }

    [Fact]
    public void Unchanged_and_missing_original_endpoints_are_left_alone_and_the_window_expires()
    {
        var rig = new EmulationRig { Clock = TimeSpan.FromSeconds(10) };
        var audio = new FakeAudioDefaults(rig.Log);
        audio.Endpoints[Render] = DualSenseEndpoint;
        audio.Endpoints[Capture] = new("mic", "Microphone");
        var noOriginal = new AudioDefaultSlot(AudioFlow.Capture, AudioRole.Multimedia);
        using var service = rig.Create(audioDefaults: audio);
        service.Update(DualSense, EmulationRig.Settings(), EmulationRig.Bluetooth);

        audio.Endpoints.Remove(Capture);
        audio.Endpoints[noOriginal] = DualSenseEndpoint;
        Step(rig, service, 24_500);
        Assert.Equal(1, audio.Polls);
        Assert.Empty(audio.Restores);

        audio.Endpoints[Capture] = DualSenseEndpoint;
        Step(rig, service, 25_000);
        Assert.Equal(1, audio.Polls);
        Assert.Empty(audio.Restores);
    }

    [Theory]
    [InlineData(EmulationMode.Xbox360, true, true)]
    [InlineData(EmulationMode.DualSense, false, true)]
    [InlineData(EmulationMode.DualSense, true, false)]
    public void Snapshot_requires_an_audio_dualsense_and_opt_in(EmulationMode mode, bool withAudio, bool keep)
    {
        var rig = new EmulationRig();
        var audio = new FakeAudioDefaults(rig.Log);
        using var service = rig.Create(audioDefaults: audio);
        var settings = new AppSettings { Emulation = new EmulationSettings { Audio = withAudio, KeepAudioDefaults = keep } };

        service.Update(EmulationRig.ProfileWith(mode), settings, EmulationRig.Bluetooth);
        Step(rig, service, 500);

        Assert.True(service.Status.Attached);
        Assert.DoesNotContain("snapshot", rig.Log);
        Assert.Equal(0, audio.Polls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Turning_protection_off_or_detaching_stops_the_watch(bool detach)
    {
        var rig = new EmulationRig();
        var audio = new FakeAudioDefaults(rig.Log);
        audio.Endpoints[Render] = new("headset", "Headset");
        using var service = rig.Create(audioDefaults: audio);
        var settings = EmulationRig.Settings(hide: false);
        service.Update(DualSense, settings, EmulationRig.Bluetooth);
        var pad = rig.Server.Last;

        var off = settings with { Emulation = settings.Emulation with { KeepAudioDefaults = false } };
        service.Update(DualSense, detach ? settings : off, detach ? null : EmulationRig.Bluetooth);
        if (!detach)
            service.Update(DualSense, settings, EmulationRig.Bluetooth);

        audio.Endpoints[Render] = DualSenseEndpoint;
        Step(rig, service, 500);
        Assert.Same(pad, rig.Server.Last);
        Assert.Equal(!detach, service.Status.Attached);
        Assert.Equal(0, audio.Polls);
    }

    [Fact]
    public void Failed_attach_does_not_start_the_watch()
    {
        var rig = new EmulationRig();
        rig.Vhci.Fail = true;
        var audio = new FakeAudioDefaults(rig.Log);
        audio.Endpoints[Render] = new("headset", "Headset");
        using var service = rig.Create(audioDefaults: audio);
        service.Update(DualSense, EmulationRig.Settings(), EmulationRig.Bluetooth);

        audio.Endpoints[Render] = DualSenseEndpoint;
        Step(rig, service, 500);
        Assert.False(service.Status.Attached);
        Assert.Equal(0, audio.Polls);
    }

    [Theory]
    [InlineData("snapshot", "kaydedilemedi")]
    [InlineData("poll", "denetlenemedi")]
    [InlineData("restore", "geri yüklenemedi")]
    public void Audio_failure_reports_a_notice_and_keeps_the_pad(string failure, string message)
    {
        using var language = new TestLanguage(Language.Tr);
        var rig = new EmulationRig();
        var audio = new FakeAudioDefaults(rig.Log) { Failure = failure };
        audio.Endpoints[Render] = new("headset", "Headset");
        using var service = rig.Create(audioDefaults: audio);
        service.Update(DualSense, EmulationRig.Settings(), EmulationRig.Bluetooth);

        audio.Endpoints[Render] = DualSenseEndpoint;
        Step(rig, service, 500);
        Step(rig, service, 1000);

        Assert.True(service.Status.Attached);
        Assert.Null(service.Status.Error);
        Assert.Contains(message, Assert.Single(rig.Notices).Message);
        Assert.Equal(failure == "restore" ? 1 : 0, audio.Restores.Count);
    }

    private static void Step(EmulationRig rig, EmulationService service, int milliseconds)
    {
        rig.Clock = TimeSpan.FromMilliseconds(milliseconds);
        service.Reconcile();
    }

    private sealed class FakeAudioDefaults(List<string> log) : IAudioDefaults
    {
        public Dictionary<AudioDefaultSlot, AudioDefaultEndpoint> Endpoints { get; } = [];
        public List<(string Id, AudioRole Role)> Restores { get; } = [];
        public string? Failure { get; init; }
        public int Polls { get; private set; }

        public IReadOnlyDictionary<AudioDefaultSlot, AudioDefaultEndpoint> ReadDefaults(bool includeNames)
        {
            if (includeNames)
                Polls++;
            else
                log.Add("snapshot");

            if (Failure == (includeNames ? "poll" : "snapshot"))
                throw new AudioUnavailableException("Ses cihazları okunamadı.");

            return Endpoints;
        }

        public void Restore(string deviceId, AudioRole role)
        {
            Restores.Add((deviceId, role));
            if (Failure == "restore")
                throw new AudioUnavailableException("Ses cihazı geri yüklenemedi.");
        }
    }
}
