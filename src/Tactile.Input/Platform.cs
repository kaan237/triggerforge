using Tactile.Core;
namespace Tactile.Input;

/// <summary>Synthetic keyboard and mouse output. Implementations are safe to call from any thread.</summary>
public interface IInputInjector
{
    void Key(Key key, bool down);

    void MouseButton(MouseButton button, bool down);

    /// <summary>Relative move in pixels.</summary>
    void MouseMove(int dx, int dy);

    /// <summary>120 = one notch. Positive is up (vertical) or right (horizontal).</summary>
    void Wheel(int delta, bool horizontal);

    /// <summary>Types text as Unicode characters, independent of the keyboard layout.</summary>
    void Text(ReadOnlySpan<char> text)
    {
    }
}

/// <summary>The Windows default recording device.</summary>
public interface IMicrophone
{
    /// <summary>Flips mute and returns the new state, or null when there is no recording device.</summary>
    bool? ToggleMute();

    /// <summary>Null when there is no recording device.</summary>
    bool? IsMuted { get; }
}

public interface ILauncher
{
    /// <summary>Starts a program. Throws <see cref="InvalidOperationException"/> with a readable message on failure.</summary>
    void Run(string path, string arguments);
}

public interface IElevationProbe
{
    /// <summary>
    /// True when the foreground window belongs to an elevated process and this one is not:
    /// Windows then drops synthetic input to it without telling the sender.
    /// </summary>
    bool ForegroundBlocksInput();
}

/// <summary>Everything the mapping engine needs from the operating system.</summary>
public sealed record InputPlatform(IInputInjector Injector, IMicrophone Microphone, ILauncher Launcher, IElevationProbe Elevation)
{
    /// <summary>Does nothing. Used by tests and when no platform is given.</summary>
    public static InputPlatform None { get; } = new(new NullPlatform(), new NullPlatform(), new NullPlatform(), new NullPlatform());

    private sealed class NullPlatform : IInputInjector, IMicrophone, ILauncher, IElevationProbe
    {
        public bool? IsMuted => null;

        public void Key(Key key, bool down)
        {
        }

        public void MouseButton(MouseButton button, bool down)
        {
        }

        public void MouseMove(int dx, int dy)
        {
        }

        public void Wheel(int delta, bool horizontal)
        {
        }

        public void Text(ReadOnlySpan<char> text)
        {
        }

        public bool? ToggleMute() => null;

        public void Run(string path, string arguments) => throw new LocalizedMessage("Launching programs is unavailable here.", "Program çalıştırma bu ortamda yok.").CreateException(message => new InvalidOperationException(message));

        public bool ForegroundBlocksInput() => false;
    }
}
