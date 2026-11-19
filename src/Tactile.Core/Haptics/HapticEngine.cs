using Tactile.Audio;
using Tactile.Device.DualSense;
using Tactile.Emulation;

namespace Tactile.Core;

public sealed record HapticStatus(bool Streaming, string Source, string? Problem);

internal sealed class HapticEngine : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    private readonly IAudioSystem _audio;
    private readonly Func<HapticBlockSource?, bool> _setSource;
    private readonly Action<Notice> _notice;
    private readonly TimeProvider _time;
    private readonly HapticMixer _mixer;
    private readonly HapticBlockSource _fill;
    private readonly Lock _captureLock = new();
    private readonly Lock _speakerLock = new();
    private readonly List<CaptureTarget> _captures = new(2);
    private volatile State _state = new(false, "off", null, null, null, null, null, null);
    private volatile HapticsConfig _config = new();
    private volatile bool _disposed;

    internal HapticEngine(IAudioSystem audio, Func<HapticBlockSource?, bool> setSource,
        Func<OutputState> output, Action<Notice> notice, TimeProvider time)
    {
        _audio = audio;
        _setSource = setSource;
        _notice = notice;
        _time = time;
        _mixer = new HapticMixer(output);
        _fill = Fill;
    }

    internal HapticStatus Status
    {
        get
        {
            State state = _state;
            LocalizedMessage? problem = state.Problem ?? state.Target?.Problem
                ?? state.Game?.ActivePlayback?.Problem ?? state.Playback?.Problem ?? state.Speaker?.Problem;
            return new HapticStatus(state.Streaming, state.Source, problem?.Value);
        }
    }

    internal IReadOnlyList<AudioApp> ListApps() => _audio.ListApps();

    // Called by the core's serialized configuration path, independently of WASAPI startup.
    internal void Configure(HapticsConfig config, Link? link, IVirtualAudio? virtualAudio = null)
    {
        bool gameEnabled = virtualAudio is not null && config.GameAudio != GameAudioMode.Off;
        // Windows can keep the speaker interface selected long after a game stops; only recent sound
        // holds the Bluetooth stream (and with it the loss of motor rumble) open.
        bool gameActive = gameEnabled && virtualAudio!.SpeakerActive &&
            _state.Game is { } running && running.Source == virtualAudio && running.HasRecentSound;
        bool needed = config.Audio != AudioHapticSource.Off || config.Speaker != AudioHapticSource.Off
            || config.Clicks || config.RumbleToHaptics || gameActive;
        bool bluetooth = link == Link.Bluetooth;
        State previous = _state;
        GameInput? game = bluetooth && gameEnabled ? previous.Game : null;
        if (bluetooth && gameEnabled && (game?.Source != virtualAudio || game?.Mode != config.GameAudio))
        {
            SpeakerPlayback? gamePlayback = config.GameAudio is GameAudioMode.Both or GameAudioMode.Speaker
                ? OpenSpeaker() : null;
            game = new GameInput(virtualAudio!, config, _time, gamePlayback);
        }

        game?.Configure(config);
        CaptureTarget? target = bluetooth ? FindTarget(config.Audio, config.App, previous.Target, previous.Speaker) : null;
        CaptureTarget? speaker = bluetooth ? FindTarget(config.Speaker, config.SpeakerApp, target, previous.Speaker, previous.Target) : null;
        AudioHaptics? dsp = target is null ? null : target == previous.Target ? previous.Dsp : new AudioHaptics(config.Mode);
        if (dsp is not null)
        {
            if (config.Mode != _config.Mode)
            {
                dsp.Mode = config.Mode;
            }

            // Strength goes in before the DSP's soft clip, so >100% lifts quiet parts instead of hard-clipping.
            dsp.Gain = (float)config.Strength;
        }

        SpeakerPlayback? playback = speaker is null ? null
            : speaker == previous.Speaker ? previous.Playback : OpenSpeaker();
        string source = config.Audio == AudioHapticSource.Off
            ? SourceName(config.Speaker, config.SpeakerApp) : SourceName(config.Audio, config.App);
        _config = config;
        LocalizedMessage? problem = !needed ? null : link switch
        {
            Link.Usb => new("Bluetooth required.", "Bluetooth gerekli."),
            null => new("No controller connected.", "Kontrolcü bağlı değil."),
            _ => null,
        };
        // The streamer can ask for silence as soon as it starts its preroll.
        if (previous.Target != target || previous.Speaker != speaker || previous.Game != game)
        {
            Publish(previous with { Target = target, Dsp = dsp, Speaker = speaker, Playback = playback, Game = game });
        }

        bool streaming = _setSource(needed ? _fill : null);
        _mixer.ConfigureClicks(bluetooth, streaming && config.Clicks);
        if (!streaming || !needed)
        {
            _mixer.ResetRumble();
        }

        if (bluetooth && needed && !streaming)
        {
            problem = new("Could not start the haptics stream.", "Haptik akışı başlatılamadı.");
        }

        State current = _state;
        if (current.Streaming != streaming || current.Source != source || current.Problem != problem)
        {
            _state = current with { Streaming = streaming, Source = source, Problem = problem };
        }
    }

    private static CaptureTarget? FindTarget(AudioHapticSource source, string app,
        CaptureTarget? first, CaptureTarget? second, CaptureTarget? third = null)
    {
        if (source == AudioHapticSource.Off)
        {
            return null;
        }

        return Match(first) ?? Match(second) ?? Match(third) ??
            new CaptureTarget(source == AudioHapticSource.System ? new SystemAudio() : new AppAudio(app));

        CaptureTarget? Match(CaptureTarget? candidate) => candidate?.Target switch
        {
            SystemAudio when source == AudioHapticSource.System => candidate,
            AppAudio existing when source == AudioHapticSource.App &&
                string.Equals(existing.ProcessName, app, StringComparison.OrdinalIgnoreCase) => candidate,
            _ => null,
        };
    }

    private static string SourceName(AudioHapticSource source, string app) => source switch
    {
        AudioHapticSource.System => "system",
        AudioHapticSource.App => $"app:{app}",
        _ => "off",
    };

    private SpeakerPlayback OpenSpeaker()
    {
        try
        {
            return new SpeakerPlayback(new SpeakerFeed());
        }
        catch (AudioUnavailableException e)
        {
            _notice(new Notice(NoticeKinds.Info, e.Message));
            return new SpeakerPlayback(null) { Problem = e.Translation };
        }
    }

    private void Publish(State state)
    {
        lock (_speakerLock)
        {
            State old = _state;
            _state = state;
            if (old.Playback != state.Playback)
            {
                old.Playback?.Feed?.Dispose();
            }

            if (old.Game != state.Game)
            {
                old.Game?.Dispose();
            }
        }
    }

    internal bool ReadSpeakerFrame(int block, Span<byte> frame)
    {
        lock (_speakerLock)
        {
            State state = _state;
            if ((state.Game?.ActivePlayback ?? state.Playback) is not { Feed: { } feed, Problem: null } playback)
            {
                return false;
            }

            try
            {
                if (block < 0 || !feed.Read(frame))
                {
                    feed.EncodeSilence(frame);
                }

                return true;
            }
            catch (AudioUnavailableException e)
            {
                playback.Problem = e.Translation;
                _notice(new Notice(NoticeKinds.Info, e.Message));
                return false;
            }
        }
    }

    internal void Observe(InputState input) => _mixer.Observe(input);

    internal void PollAudio()
    {
        if (_disposed || !_captureLock.TryEnter())
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            State state = _state;
            for (int i = _captures.Count - 1; i >= 0; i--)
            {
                CaptureTarget capture = _captures[i];
                if (!state.Uses(capture))
                {
                    StopCapture(capture);
                    _captures.RemoveAt(i);
                }
            }

            if (state.Target is { } target)
            {
                PollCapture(target);
            }

            if (state.Speaker is { } speaker && speaker != state.Target && state.Playback?.Problem is null)
            {
                PollCapture(speaker);
            }
        }
        finally
        {
            _captureLock.Exit();
        }
    }

    private void PollCapture(CaptureTarget target)
    {
        if (_disposed || !_state.Uses(target))
        {
            return;
        }

        if (target.Attempt is { } attempt)
        {
            if (attempt.Failure is null && attempt.Capture.Running)
            {
                return;
            }

            LocalizedMessage problem = attempt.DescribeFailure();
            StopCapture(target);
            Fail(target, problem);
            return;
        }

        if (target.FailedAt is long failedAt && _time.GetElapsedTime(failedAt) < RetryInterval)
        {
            return;
        }

        try
        {
            IAudioCapture capture = _audio.Start(target.Target);
            if (_disposed || !_state.Uses(target))
            {
                capture.Dispose();
                return;
            }

            var started = new CaptureAttempt(capture, samples => Push(target, samples));
            target.Attempt = started;
            if (!_captures.Contains(target))
            {
                _captures.Add(target);
            }

            capture.Failed += started.OnFailure;
            capture.Data += started.Data;
            if (started.Failure is null && capture.Running)
            {
                target.Problem = null;
                target.FailedAt = null;
            }
            else
            {
                LocalizedMessage problem = started.DescribeFailure();
                StopCapture(target);
                Fail(target, problem);
            }
        }
        catch (AudioUnavailableException e)
        {
            Fail(target, e.Translation);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Publish(new State(false, "off", null, null, null, null, null, null));
        _setSource(null);
        lock (_captureLock)
        {
            foreach (CaptureTarget target in _captures)
            {
                StopCapture(target);
            }

            _captures.Clear();
        }
    }

    private void Fill(int _, Span<sbyte> samples)
    {
        State state = _state;
        AudioHaptics? dsp = state.Dsp;
        if (dsp is null)
        {
            samples.Clear();
        }
        else
        {
            dsp.Read(samples);
        }

        if (state.Game is { HapticsEnabled: true, Source.SpeakerActive: true } game)
        {
            game.Audio.OverrideIfLive(samples);
        }

        HapticsConfig config = _config;
        _mixer.Mix(samples, 1f, (float)config.ClickStrength);
    }

    private void Fail(CaptureTarget target, LocalizedMessage problem)
    {
        target.FailedAt = _time.GetTimestamp();
        if (_disposed || !_state.Uses(target))
        {
            return;
        }

        bool firstFailure = target.Problem is null;
        target.Problem = problem;
        if (firstFailure)
        {
            _notice(new Notice(NoticeKinds.Info, problem.Value));
        }
    }

    private void Push(CaptureTarget target, ReadOnlySpan<float> samples)
    {
        State state = _state;
        if (state.Target == target)
        {
            state.Dsp?.Push(samples);
        }

        if (state.Speaker == target && state.Playback is { Problem: null } playback)
        {
            playback.Feed?.Push(samples);
        }
    }

    private void StopCapture(CaptureTarget target)
    {
        if (target.Attempt is { } attempt)
        {
            attempt.Capture.Data -= attempt.Data;
            attempt.Capture.Failed -= attempt.OnFailure;
            attempt.Capture.Dispose();
            target.Attempt = null;
            State state = _state;
            if (state.Target == target)
            {
                state.Dsp?.Clear();
            }

            if (state.Speaker == target)
            {
                state.Playback?.Feed?.Clear();
            }
        }
    }

    private sealed record State(bool Streaming, string Source, LocalizedMessage? Problem, CaptureTarget? Target, AudioHaptics? Dsp,
        CaptureTarget? Speaker, SpeakerPlayback? Playback, GameInput? Game)
    {
        public bool Uses(CaptureTarget target) =>
            Streaming && (Target == target || (Speaker == target && Playback?.Problem is null));
    }

    private sealed class GameInput : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly SpeakerPlayback? _playback;
        private static readonly TimeSpan RecentSound = TimeSpan.FromSeconds(3);
        private readonly TimeProvider _time;
        private const long NoSound = long.MinValue;
        private long _lastSound = NoSound;
        private bool _buffered;
        private bool _disposed;

        public GameInput(IVirtualAudio source, HapticsConfig config, TimeProvider time, SpeakerPlayback? playback)
        {
            Source = source;
            Mode = config.GameAudio;
            _time = time;
            Audio = new GameAudio(time);
            _playback = playback;
            if (playback?.Feed is { } feed)
            {
                Audio.Speaker += feed.Push;
            }

            Configure(config);
            Source.Audio += Push;
        }

        public IVirtualAudio Source { get; }
        public GameAudioMode Mode { get; }
        public GameAudio Audio { get; }
        public bool HapticsEnabled => Mode is GameAudioMode.Both or GameAudioMode.Haptics;
        public SpeakerPlayback? ActivePlayback => Source.SpeakerActive ? _playback : null;

        /// <summary>Non-silent game audio arrived in the last few seconds.</summary>
        public bool HasRecentSound => Volatile.Read(ref _lastSound) is long last && last != NoSound && _time.GetElapsedTime(last) < RecentSound;

        public void Configure(HapticsConfig config)
        {
            Audio.HapticStrength = (float)config.GameStrength;
            Audio.LowPass = config.GameLowPass;
            if (!Source.SpeakerActive)
            {
                lock (_gate)
                {
                    if (_buffered && !Source.SpeakerActive)
                    {
                        Audio.Clear();
                        _playback?.Feed?.Clear();
                        _buffered = false;
                    }
                }
            }
        }

        private void Push(ReadOnlySpan<short> frames)
        {
            lock (_gate)
            {
                if (!_disposed && Source.SpeakerActive)
                {
                    if (frames.ContainsAnyExcept((short)0))
                    {
                        Volatile.Write(ref _lastSound, _time.GetTimestamp());
                    }

                    Audio.Push(frames);
                    _buffered = true;
                }
            }
        }

        public void Dispose()
        {
            Source.Audio -= Push;
            lock (_gate)
            {
                _disposed = true;
            }

            _playback?.Feed?.Dispose();
        }
    }

    private sealed class CaptureTarget(AudioTarget target)
    {
        public AudioTarget Target { get; } = target;
        public CaptureAttempt? Attempt;
        public long? FailedAt;
        public volatile LocalizedMessage? Problem;
    }

    private sealed class SpeakerPlayback(SpeakerFeed? feed)
    {
        public SpeakerFeed? Feed { get; } = feed;
        public volatile LocalizedMessage? Problem;
    }

    private sealed class CaptureAttempt(IAudioCapture capture, AudioDataHandler data)
    {
        public IAudioCapture Capture { get; } = capture;
        public AudioDataHandler Data { get; } = data;
        public volatile string? Failure;
        public void OnFailure(string problem) => Failure = problem;

        public LocalizedMessage DescribeFailure() =>
            AudioSystemFactory.CaptureFailure(Capture)
            ?? (Failure is { } reason ? new(reason, reason) : new("Audio capture stopped.", "Ses yakalama durdu."));
    }
}
