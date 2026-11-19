using System.Runtime.InteropServices;

namespace Tactile.Device.DualSense;

/// <summary>
/// Bluetooth haptics-only report 0x32: 32 stereo frames of signed 8-bit samples at 3 kHz,
/// sent every 10.667 ms. Sub-packet values come from community reverse engineering.
/// </summary>
public sealed class HapticReport
{
    public const int Length = 142;
    public const int SpeakerLength = 398;
    public const int SpeakerFrameLength = 200;
    public const int FramesPerReport = 32;
    public const int SampleRate = 3000;
    public const int PayloadLength = FramesPerReport * 2;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(512.0 / 48000);

    private readonly byte[] _report = new byte[Length];
    private readonly byte[] _speakerReport = new byte[SpeakerLength];
    private readonly byte[] _primeState = new byte[63];
    private int _sequence;
    private byte _counter;
    private volatile byte _bufferLength = 64;

    /// <summary>Controller-side buffer length, 16-128. Larger = smoother, more latency.</summary>
    public byte BufferLength { get => _bufferLength; set => _bufferLength = value; }
    public bool IncludeStateBlock { get; set; }

    /// <summary>
    /// Opens the controller's Bluetooth audio/haptics session. Must precede haptic reports;
    /// carries a full state block (sub-packet 0x10) with the audio-stream flags.
    /// </summary>
    public byte[] BuildPrime(OutputState state, OutputOptions options)
    {
        byte[] r = new byte[Length];
        r[0] = 0x32;
        r[1] = 0x10;
        r[2] = 0x90;
        r[3] = 0x3F;
        OutputReport.WriteState(state, r.AsSpan(4), options with { Rumble = false, AudioStream = true });
        r.AsSpan(4, _primeState.Length).CopyTo(_primeState);
        Crc32.Stamp(Crc32.OutputSeed, r);
        _sequence = 0;
        _counter = 0;
        return r;
    }

    /// <param name="frames">Interleaved left/right samples, 64 values.</param>
    public byte[] Build(ReadOnlySpan<sbyte> frames)
    {
        if (frames.Length != PayloadLength)
        {
            throw new ArgumentException($"Expected {PayloadLength} samples.", nameof(frames));
        }

        byte[] r = _report;
        Array.Clear(r);
        r[0] = 0x32;
        r[1] = NextSequence();

        // Sub-packet 0x11: session flags, buffer lengths, packet counter.
        r[2] = 0x91;
        r[3] = 4;
        r[4] = 0x66;
        r[5] = r[6] = BufferLength;
        r[7] = _counter++;

        // Sub-packet 0x12: haptic samples.
        r[8] = 0x92;
        r[9] = PayloadLength;
        for (int i = 0; i < PayloadLength; i++)
        {
            r[10 + i] = (byte)frames[i];
        }

        Crc32.Stamp(Crc32.OutputSeed, r);
        return r;
    }

    public byte[] BuildSpeaker(ReadOnlySpan<sbyte> haptics64, ReadOnlySpan<byte> opus200)
    {
        if (haptics64.Length != PayloadLength)
        {
            throw new ArgumentException($"Expected {PayloadLength} samples.", nameof(haptics64));
        }

        if (opus200.Length != SpeakerFrameLength)
        {
            throw new ArgumentException($"Expected {SpeakerFrameLength} bytes.", nameof(opus200));
        }

        byte[] r = _speakerReport;
        Array.Clear(r);
        r[0] = 0x36;
        r[1] = NextSequence();
        r[2] = 0x91;
        r[3] = 7;
        r[4] = 0xFE;
        r.AsSpan(5, 5).Fill(0x40);
        r[10] = _counter++;

        int offset = 11;
        if (IncludeStateBlock)
        {
            r[offset++] = 0x90;
            r[offset++] = (byte)_primeState.Length;
            _primeState.CopyTo(r.AsSpan(offset));
            offset += _primeState.Length;
        }

        r[offset++] = 0x92;
        r[offset++] = PayloadLength;
        MemoryMarshal.AsBytes(haptics64).CopyTo(r.AsSpan(offset));
        offset += PayloadLength;
        r[offset++] = 0x93;
        r[offset++] = SpeakerFrameLength;
        opus200.CopyTo(r.AsSpan(offset));
        Crc32.Stamp(Crc32.OutputSeed, r);
        return r;
    }

    private byte NextSequence()
    {
        byte value = (byte)(_sequence << 4);
        _sequence = (_sequence + 1) & 0x0F;
        return value;
    }

    /// <summary>Downsamples one 512-frame 48 kHz stereo block (floats -1..1) to a 3 kHz payload by averaging 16-frame windows.</summary>
    public static void Downsample(ReadOnlySpan<float> stereo48k, Span<sbyte> payload, float gain = 1f)
    {
        const int window = 16;
        if (stereo48k.Length != FramesPerReport * window * 2)
        {
            throw new ArgumentException("Expected 512 stereo frames.", nameof(stereo48k));
        }

        for (int f = 0; f < FramesPerReport; f++)
        {
            float l = 0, r = 0;
            for (int k = 0; k < window; k++)
            {
                int i = (f * window + k) * 2;
                l += stereo48k[i];
                r += stereo48k[i + 1];
            }

            payload[f * 2] = ToS8(l / window * gain);
            payload[f * 2 + 1] = ToS8(r / window * gain);
        }
    }

    private static sbyte ToS8(float v) => (sbyte)Math.Clamp(MathF.Round(v * 127f), -127, 127);
}
