using Tactile.Core;

namespace Tactile.Audio;

// Fixed contract for phase 5a (docs/contract-phase5a.md). Do not edit; implementations live
// in other files of this project.

/// <summary>A process that currently has an audio render session.</summary>
public sealed record AudioApp(int ProcessId, string ProcessName, string DisplayName);

/// <summary>What to capture.</summary>
public abstract record AudioTarget;

/// <summary>Loopback of the default render device: everything the user hears.</summary>
public sealed record SystemAudio : AudioTarget;

/// <summary>
/// Loopback of one application (and its child processes). <see cref="ProcessName"/> is the
/// exe file name, case-insensitive; the first running process with that name is used.
/// </summary>
public sealed record AppAudio(string ProcessName) : AudioTarget;

/// <summary>Interleaved stereo float samples (-1..1) at 48 kHz. Valid only during the call.</summary>
public delegate void AudioDataHandler(ReadOnlySpan<float> stereo48k);

public interface IAudioCapture : IDisposable
{
    AudioTarget Target { get; }

    /// <summary>True from start until disposed or failed.</summary>
    bool Running { get; }

    /// <summary>Raised on the capture thread. Handlers must be quick and must not throw.</summary>
    event AudioDataHandler? Data;

    /// <summary>Raised once when capture stops by itself (device removed, app exited). localized message.</summary>
    event Action<string>? Failed;
}

public sealed class AudioUnavailableException(LocalizedMessage translation, Exception? inner = null)
    : LocalizedException(translation, inner)
{
    public AudioUnavailableException(string message, Exception? inner = null) : this(new LocalizedMessage(message, message), inner) { }
}

public interface IAudioSystem
{
    /// <summary>Processes with an active render session, one entry per process name.</summary>
    IReadOnlyList<AudioApp> ListApps();

    /// <summary>Starts capturing. Throws <see cref="AudioUnavailableException"/> with a localized message.</summary>
    IAudioCapture Start(AudioTarget target);
}

public enum HapticMode { Game, Film, Music }
