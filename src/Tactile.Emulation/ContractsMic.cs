namespace Tactile.Emulation;

/// <summary>Fills one 1 ms packet of 2-channel 48 kHz S16LE microphone audio (the span arrives zeroed). Valid only during the call.</summary>
public delegate void MicAudioReader(Span<byte> stereoS16Le);

/// <summary>Microphone side of a virtual DualSense created with audio.</summary>
public interface IVirtualMic
{
    /// <summary>The host selected the microphone streaming interface (interface 2, alt 1).</summary>
    bool MicActive { get; }

    /// <summary>The host muted the microphone feature unit.</summary>
    bool MicMuted { get; }

    /// <summary>Microphone feature unit volume, 0x0000..0x3000.</summary>
    ushort MicVolume { get; }

    /// <summary>Raised when <see cref="MicActive"/> flips, on the thread that handled the request. Handlers must be quick and must not throw.</summary>
    event Action<bool>? MicActiveChanged;

    /// <summary>The reader runs on the ISO scheduler thread for every packet while the mic is active.</summary>
    void SetMicReader(MicAudioReader? reader);
}
