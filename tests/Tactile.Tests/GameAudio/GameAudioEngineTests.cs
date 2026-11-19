using Tactile.Audio;
using Tactile.Core;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Tests;

public sealed class GameAudioEngineTests
{
    [Fact]
    public void Game_haptics_replace_local_audio_only_during_the_live_window()
    {
        using var rig = new Rig();
        var config = new HapticsConfig { Audio = AudioHapticSource.System, GameAudio = GameAudioMode.Haptics };
        rig.Pad.SpeakerActive = true;
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        rig.Engine.PollAudio();
        var local = new float[1024];
        for (int i = 0; i < 512; i++)
        {
            local[i * 2] = MathF.Sin(2f * MathF.PI * 120 * i / 48000);
        }

        rig.Capture.Push(local);
        rig.Capture.Push(local);
        rig.Pad.Push(GameAudioTests.Block(8192, 8192, 0, 0));
        sbyte[] payload = rig.Fill();
        Assert.Contains(payload, value => value != 0);

        rig.Capture.Push(local);
        rig.Pad.Push(GameAudioTests.Block(0, 0, 0, 4096));
        Assert.All(rig.Fill(), value => Assert.Equal(0, value));
        rig.Capture.Push(local);
        rig.Pad.Push(GameAudioTests.Block(0, 0, 0, 4096));
        payload = rig.Fill();
        for (int i = 0; i < payload.Length; i += 2)
        {
            Assert.Equal(0, payload[i]);
            Assert.Equal(16, payload[i + 1]);
        }

        rig.Time.Milliseconds = 99;
        rig.Capture.Push(local);
        Assert.Equal(16, rig.Fill()[1]);
        rig.Capture.Push(local);
        Assert.All(rig.Fill(), value => Assert.Equal(0, value));
        rig.Time.Milliseconds = 100;
        rig.Capture.Push(local);
        Assert.Contains(rig.Fill(), value => value != 0);
    }

    [Fact]
    public void Active_game_speaker_wins_and_encodes_silence_until_game_data_arrives()
    {
        using var rig = new Rig();
        var config = new HapticsConfig { Speaker = AudioHapticSource.System, GameAudio = GameAudioMode.Speaker };
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        rig.Engine.PollAudio();
        float[] local = Enumerable.Repeat(-0.5f, 1024).ToArray();
        rig.Capture.Push(local);
        rig.Capture.Push(local);

        byte[] actual = new byte[200], expected = new byte[200];
        using var game = new SpeakerFeed();
        rig.Pad.SpeakerActive = true;
        game.EncodeSilence(expected);
        Assert.True(rig.Engine.ReadSpeakerFrame(0, actual));
        Assert.Equal(expected, actual);

        // The speaker feed starts once two blocks are queued.
        rig.Pad.Push(GameAudioTests.Block(8192, 8192, 16384, 16384));
        rig.Pad.Push(GameAudioTests.Block(8192, 8192, 16384, 16384));
        game.Push(Enumerable.Repeat(0.25f, 1024).ToArray());
        game.Push(Enumerable.Repeat(0.25f, 1024).ToArray());
        Assert.True(game.Read(expected));
        Assert.True(rig.Engine.ReadSpeakerFrame(1, actual));
        Assert.Equal(expected, actual);
        Assert.All(rig.Fill(), value => Assert.Equal(0, value));

        rig.Pad.SpeakerActive = false;
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        using var fallback = new SpeakerFeed();
        fallback.Push(local);
        fallback.Push(local);
        Assert.True(fallback.Read(expected));
        Assert.True(rig.Engine.ReadSpeakerFrame(2, actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Game_only_stream_follows_interface_activity_mode_link_and_pad_lifetime()
    {
        using var rig = new Rig();
        var config = new HapticsConfig();
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        Assert.Null(rig.Source);
        Assert.True(rig.Pad.Subscribed);

        rig.Pad.SpeakerActive = true;
        rig.Pad.Push(GameAudioTests.Block(0, 0, 4096, 4096));
        rig.Pad.Push(GameAudioTests.Block(0, 0, 4096, 4096));
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        Assert.All(rig.Fill(), value => Assert.Equal(16, value));
        rig.Pad.Push(GameAudioTests.Block(0, 0, 8192, 8192));

        rig.Pad.SpeakerActive = false;
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        Assert.Null(rig.Source);
        rig.Pad.SpeakerActive = true;
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        Assert.All(rig.Fill(), value => Assert.Equal(0, value));

        rig.Engine.Configure(config with { GameAudio = GameAudioMode.Off }, Link.Bluetooth, rig.Pad);
        Assert.Null(rig.Source);
        Assert.False(rig.Pad.Subscribed);
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        rig.Engine.Configure(config, Link.Usb);
        Assert.False(rig.Pad.Subscribed);
        Assert.Null(rig.Source);
    }

    [Fact]
    public void Silent_game_audio_does_not_open_the_stream()
    {
        using var rig = new Rig();
        var config = new HapticsConfig();
        rig.Pad.SpeakerActive = true;
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        rig.Pad.Push(GameAudioTests.Block(0, 0, 0, 0));
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        Assert.Null(rig.Source);

        rig.Pad.Push(GameAudioTests.Block(0, 0, 4096, 4096));
        rig.Engine.Configure(config, Link.Bluetooth, rig.Pad);
        Assert.NotNull(rig.Source);
    }

    private sealed class Rig : IDisposable
    {
        public FakeCapture Capture { get; } = new();
        public FakePad Pad { get; } = new();
        public GameAudioClock Time { get; } = new();
        public HapticEngine Engine { get; }
        public HapticBlockSource? Source { get; private set; }

        public Rig() => Engine = new HapticEngine(new FakeAudio(Capture), source => (Source = source) is not null,
            () => OutputState.Default, notice => Assert.Fail(notice.Message), Time);

        public sbyte[] Fill()
        {
            var payload = new sbyte[64];
            Assert.NotNull(Source);
            Source(0, payload);
            return payload;
        }

        public void Dispose() => Engine.Dispose();
    }

    private sealed class FakePad : IVirtualAudio
    {
        public bool SpeakerActive { get; set; }
        public event GameAudioHandler? Audio;
        public bool Subscribed => Audio is not null;
        public void Push(ReadOnlySpan<short> frames) => Audio?.Invoke(frames);
    }

    private sealed class FakeAudio(FakeCapture capture) : IAudioSystem
    {
        public IReadOnlyList<AudioApp> ListApps() => [];
        public IAudioCapture Start(AudioTarget target) => capture;
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public AudioTarget Target { get; } = new SystemAudio();
        public bool Running { get; private set; } = true;
        public event AudioDataHandler? Data;
        public event Action<string>? Failed { add { } remove { } }
        public void Push(ReadOnlySpan<float> samples) => Data?.Invoke(samples);
        public void Dispose() => Running = false;
    }
}
