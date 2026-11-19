using Tactile.Audio;
using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public sealed class HapticEngineTests
{
    [Theory]
    [InlineData(AudioHapticSource.Off, false, false, false)]
    [InlineData(AudioHapticSource.System, false, false, true)]
    [InlineData(AudioHapticSource.App, false, false, true)]
    [InlineData(AudioHapticSource.Off, true, false, true)]
    [InlineData(AudioHapticSource.Off, false, true, true)]
    [InlineData(AudioHapticSource.System, true, true, true)]
    public void Profile_features_keep_one_stream_only_on_bluetooth(AudioHapticSource source, bool clicks, bool rumble, bool expected)
    {
        using var rig = new Rig();
        var config = new HapticsConfig { Audio = source, App = "Player.exe", Clicks = clicks, RumbleToHaptics = rumble };
        rig.Configure(config, Link.Usb);
        Assert.False(rig.Engine.Status.Streaming);
        Assert.Equal(expected ? "Bluetooth gerekli." : null, rig.Engine.Status.Problem);
        Assert.Empty(rig.Audio.Starts);

        rig.Configure(config);
        rig.Configure(config with { ClickStrength = 0.2, Mode = HapticMode.Film });
        Assert.Equal(expected, rig.Engine.Status.Streaming);
        Assert.Equal(expected ? 1 : 0, rig.Opened);
        Assert.Equal(source == AudioHapticSource.Off ? 0 : 1, rig.Audio.Starts.Count);

        rig.Configure(config, null);
        Assert.False(rig.Engine.Status.Streaming);
        Assert.Equal(expected, rig.Source is not null);
        if (rig.Audio.Latest is { } previous)
        {
            Assert.True(previous.Disposed);
        }

        rig.Configure(config);
        Assert.Equal(expected ? 2 : 0, rig.Opened);
        rig.Configure(new HapticsConfig());
        Assert.Equal(new HapticStatus(false, "off", null), rig.Engine.Status);
        Assert.Null(rig.Source);
    }

    [Fact]
    public void App_retries_at_three_seconds_and_recovers_without_reopening_the_stream()
    {
        const string missing = "Player.exe çalışmıyor.";
        using var rig = new Rig();
        var config = new HapticsConfig { Audio = AudioHapticSource.App, App = "Player.exe" };
        rig.Audio.Error = missing;
        rig.Configure(config);
        Assert.Equal(new HapticStatus(true, "app:Player.exe", missing), rig.Engine.Status);

        rig.Time.Advance(TimeSpan.FromMilliseconds(2999));
        rig.Engine.PollAudio();
        Assert.Single(rig.Audio.Starts);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        rig.Engine.PollAudio();
        Assert.Equal(2, rig.Audio.Starts.Count);
        Assert.Equal(new Notice(NoticeKinds.Info, missing), Assert.Single(rig.Notices));

        rig.Audio.Error = null;
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        rig.Engine.PollAudio();
        Assert.Null(rig.Engine.Status.Problem);
        FakeCapture capture = Assert.IsType<FakeCapture>(rig.Audio.Latest);
        Assert.Equal(new AppAudio("Player.exe"), capture.Target);
        Assert.Equal(1, rig.Opened);

        const string stopped = "Uygulama kapandı. Ses yakalama durdu.";
        capture.Fail(stopped);
        rig.Engine.PollAudio();
        Assert.True(capture.Disposed);
        Assert.Equal(stopped, rig.Engine.Status.Problem);
        Assert.Equal(3, rig.Audio.Starts.Count);
        rig.Audio.Error = missing;
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        rig.Engine.PollAudio();
        Assert.Equal(4, rig.Audio.Starts.Count);
        Assert.Collection(rig.Notices, n => Assert.Equal(missing, n.Message), n => Assert.Equal(stopped, n.Message));
    }

    [Fact]
    public void Switching_target_disposes_old_capture_and_discards_queued_audio()
    {
        using var rig = new Rig();
        rig.Configure(new HapticsConfig { Audio = AudioHapticSource.System });
        FakeCapture previous = Assert.IsType<FakeCapture>(rig.Audio.Latest);
        previous.Push(SineBlock());

        rig.Configure(new HapticsConfig { Audio = AudioHapticSource.App, App = "Player.exe" });
        Assert.True(previous.Disposed);
        Assert.Equal(new AppAudio("Player.exe"), rig.Audio.Latest?.Target);
        Assert.All(rig.Block(), sample => Assert.Equal(0, sample));
        Assert.Equal(1, rig.Opened);
    }

    [Fact]
    public void Capture_finished_starting_after_turn_off_is_disposed()
    {
        using var rig = new Rig();
        rig.Audio.OnStart = () => rig.Engine.Configure(new HapticsConfig(), Link.Bluetooth);

        rig.Configure(new HapticsConfig { Audio = AudioHapticSource.System });

        Assert.True(rig.Audio.Latest?.Disposed);
        Assert.Null(rig.Source);
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public void Audio_strength_scales_dsp_and_rumble_uses_the_current_composited_output()
    {
        using var rig = new Rig();
        var config = new HapticsConfig { Audio = AudioHapticSource.System, Strength = 0 };
        rig.Configure(config);
        FakeCapture capture = Assert.IsType<FakeCapture>(rig.Audio.Latest);
        float[] data = SineBlock();
        capture.Push(data);
        Assert.All(rig.Block(), sample => Assert.Equal(0, sample));

        rig.Configure(config with { Strength = 1 });
        capture.Push(data);
        sbyte[] audio = rig.Block();
        Assert.Contains(audio.Where((_, i) => i % 2 == 0), sample => sample != 0);
        Assert.All(audio.Where((_, i) => i % 2 != 0), sample => Assert.Equal(0, sample));

        rig.Output = rig.Output with { RumbleRight = 255 };
        sbyte[] rumble = rig.Block();
        Assert.Contains(rumble.Where((_, i) => i % 2 != 0), sample => sample != 0);
        Assert.Single(rig.Audio.Starts);
    }

    private static float[] SineBlock()
    {
        var samples = new float[1024];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            samples[frame * 2] = MathF.Sin(2 * MathF.PI * 60 * frame / 48_000);
        }

        return samples;
    }

    private sealed class Rig : IDisposable
    {
        public FakeAudio Audio { get; } = new();
        public ManualTime Time { get; } = new();
        public List<Notice> Notices { get; } = [];
        public OutputState Output { get; set; } = OutputState.Default;
        public HapticEngine Engine { get; }
        public HapticBlockSource? Source { get; private set; }
        public int Opened { get; private set; }
        private int _block;
        private Link? _link;
        private bool _streaming;

        public Rig() => Engine = new HapticEngine(Audio, SetSource, () => Output, Notices.Add, Time);

        public void Configure(HapticsConfig config, Link? link = Link.Bluetooth)
        {
            _link = link;
            Engine.Configure(config, link);
            Engine.PollAudio();
        }

        public sbyte[] Block()
        {
            var samples = new sbyte[HapticReport.PayloadLength];
            Assert.NotNull(Source);
            Source(_block++, samples);
            return samples;
        }

        public void Dispose() => Engine.Dispose();

        private bool SetSource(HapticBlockSource? source)
        {
            bool streaming = source is not null && _link == Link.Bluetooth;
            if (streaming && !_streaming)
            {
                Opened++;
            }

            Source = source;
            _streaming = streaming;
            return streaming;
        }
    }

    private sealed class FakeAudio : IAudioSystem
    {
        public List<AudioTarget> Starts { get; } = [];
        public FakeCapture? Latest { get; private set; }
        public string? Error { get; set; }
        public Action? OnStart { get; set; }

        public IReadOnlyList<AudioApp> ListApps() => [];

        public IAudioCapture Start(AudioTarget target)
        {
            Starts.Add(target);
            if (Error is { } error)
            {
                throw new AudioUnavailableException(error);
            }

            Latest = new FakeCapture(target);
            OnStart?.Invoke();
            return Latest;
        }
    }

    private sealed class FakeCapture(AudioTarget target) : IAudioCapture
    {
        public AudioTarget Target { get; } = target;
        public bool Running { get; private set; } = true;
        public bool Disposed { get; private set; }
        public event AudioDataHandler? Data;
        public event Action<string>? Failed;

        public void Push(ReadOnlySpan<float> samples) => Data?.Invoke(samples);

        public void Fail(string message)
        {
            Running = false;
            Failed?.Invoke(message);
        }

        public void Dispose()
        {
            Running = false;
            Disposed = true;
        }
    }
}
