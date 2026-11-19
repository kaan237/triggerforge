using Tactile.Audio;
using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public sealed class SpeakerEngineTests
{
    [Fact]
    public void Speaker_only_streams_on_bluetooth_and_encodes_silence_through_preroll_and_underrun()
    {
        var audio = new FakeAudio();
        audio.Error = "Player.exe çalışmıyor.";
        var notices = new List<Notice>();
        HapticBlockSource? source = null;
        using var engine = new HapticEngine(audio, next => (source = next) is not null,
            () => OutputState.Default, notices.Add, TimeProvider.System);
        var config = new HapticsConfig { Speaker = AudioHapticSource.App, SpeakerApp = "Player.exe" };
        var packet = new byte[200];

        engine.Configure(config, Link.Usb);
        engine.PollAudio();
        Assert.False(engine.ReadSpeakerFrame(0, packet));
        Assert.Empty(audio.Captures);

        engine.Configure(config, Link.Bluetooth);
        engine.PollAudio();
        Assert.NotNull(source);
        Assert.Equal(new HapticStatus(true, "app:Player.exe", audio.Error), engine.Status);
        Assert.Equal(audio.Error, Assert.Single(notices).Message);
        using var reference = new SpeakerFeed();
        var expected = new byte[200];
        foreach (int tick in new[] { -8, -1, 0, 1 })
        {
            reference.EncodeSilence(expected);
            Assert.True(engine.ReadSpeakerFrame(tick, packet));
            Assert.Equal(expected, packet);
        }

        engine.Configure(new HapticsConfig { Clicks = true }, Link.Bluetooth);
        engine.PollAudio();
        Assert.True(engine.Status.Streaming);
        Assert.False(engine.ReadSpeakerFrame(2, packet));
    }

    [Fact]
    public void Same_target_fans_out_to_both_feeds_and_split_reuses_the_unchanged_capture()
    {
        var audio = new FakeAudio();
        HapticBlockSource? source = null;
        using var engine = new HapticEngine(audio, next => (source = next) is not null,
            () => OutputState.Default, _ => { }, TimeProvider.System);
        var config = new HapticsConfig
        {
            Audio = AudioHapticSource.App, App = "PLAYER.exe",
            Speaker = AudioHapticSource.App, SpeakerApp = "player.exe",
        };
        engine.Configure(config, Link.Bluetooth);
        engine.PollAudio();
        FakeCapture shared = Assert.Single(audio.Captures);
        float[] samples = new float[1024];
        for (int i = 0; i < samples.Length; i += 2)
        {
            samples[i] = MathF.Sin(2f * MathF.PI * 60 * (i / 2) / 48000);
        }

        shared.Push(samples);
        var haptic = new sbyte[64];
        Assert.NotNull(source);
        source(0, haptic);
        Assert.Contains(haptic, sample => sample != 0);

        using var reference = new SpeakerFeed();
        var frame = new byte[200];
        var expected = new byte[200];
        reference.Push(samples);
        Assert.True(reference.Read(expected));
        Assert.True(engine.ReadSpeakerFrame(0, frame));
        Assert.Equal(expected, frame);

        shared.Push(samples);
        engine.Configure(config with { Speaker = AudioHapticSource.System }, Link.Bluetooth);
        engine.PollAudio();
        Assert.Equal(2, audio.Captures.Count);
        Assert.True(shared.Running);
        FakeCapture speakerOnly = audio.Captures[1];
        Assert.IsType<SystemAudio>(speakerOnly.Target);
        using var newFeed = new SpeakerFeed();
        newFeed.EncodeSilence(expected);
        Assert.True(engine.ReadSpeakerFrame(1, frame));
        Assert.Equal(expected, frame);

        engine.Configure(config, Link.Bluetooth);
        engine.PollAudio();
        Assert.False(speakerOnly.Running);
        Assert.True(shared.Running);
        Assert.Equal(2, audio.Captures.Count);
        engine.Configure(config, null);
        engine.PollAudio();
        Assert.False(shared.Running);
        Assert.False(engine.ReadSpeakerFrame(2, frame));
    }

    private sealed class FakeAudio : IAudioSystem
    {
        public List<FakeCapture> Captures { get; } = [];
        public string? Error { get; set; }
        public IReadOnlyList<AudioApp> ListApps() => [];

        public IAudioCapture Start(AudioTarget target)
        {
            if (Error is { } problem)
            {
                throw new AudioUnavailableException(problem);
            }

            var capture = new FakeCapture(target);
            Captures.Add(capture);
            return capture;
        }
    }

    private sealed class FakeCapture(AudioTarget target) : IAudioCapture
    {
        public AudioTarget Target { get; } = target;
        public bool Running { get; private set; } = true;
        public event AudioDataHandler? Data;
        public event Action<string>? Failed { add { } remove { } }

        public void Push(ReadOnlySpan<float> samples) => Data?.Invoke(samples);
        public void Dispose() => Running = false;
    }
}
