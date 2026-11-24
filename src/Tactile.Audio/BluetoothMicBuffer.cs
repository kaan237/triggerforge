using System.Buffers.Binary;

namespace Tactile.Audio;

public interface IMicDecoder : IDisposable
{
    /// <returns>Samples decoded, or a negative error code.</returns>
    int Decode(ReadOnlySpan<byte> packet, Span<short> mono480);

    int DecodePlc(Span<short> mono480);
}

/// <summary>
/// Decodes the controller microphone's 10 ms Opus frames into a small jitter buffer and hands them out as
/// 1 ms stereo S16LE packets. One thread pushes frames, another reads; neither holds a lock while the other decodes.
/// </summary>
public sealed class BluetoothMicBuffer : IDisposable
{
    public const int FrameSamples = 480;
    public const int StereoFramesPerRead = 48;
    private const int Capacity = FrameSamples * 8;          // 80 ms
    private const int PrimeSamples = FrameSamples * 3;      // 30 ms
    private const int TrimAbove = 2880;                     // 60 ms
    private const int TrimTo = 1680;                        // 35 ms
    private const int MaxLost = 4;

    private readonly IMicDecoder _decoder;
    private readonly Lock _decodeGate = new();
    private readonly Lock _ringGate = new();
    private readonly short[] _ring = new short[Capacity];
    private int _head;
    private int _count;
    private bool _primed;
    private short _last;
    private bool _closed;
    private bool _haveSequence;
    private byte _lastSequence;
    private bool _disposed;
    private long _decodeErrors;
    private long _concealed;

    public BluetoothMicBuffer() : this(new OpusDecoder())
    {
    }

    public BluetoothMicBuffer(IMicDecoder decoder) => _decoder = decoder;

    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);

    public long ConcealedFrames => Interlocked.Read(ref _concealed);

    public int BufferedSamples
    {
        get
        {
            lock (_ringGate)
            {
                return _count;
            }
        }
    }

    /// <summary>Decodes one frame (and conceals frames missing before it). Never throws.</summary>
    public void Push(byte sequence, ReadOnlySpan<byte> opus)
    {
        Span<short> frame = stackalloc short[FrameSamples];
        lock (_decodeGate)
        {
            if (_disposed)
            {
                return;
            }

            int lost = 0;
            if (_haveSequence)
            {
                byte delta = (byte)(sequence - _lastSequence);
                if (delta == 0)
                {
                    return;
                }

                if (delta is >= 2 and <= MaxLost + 1)
                {
                    lost = delta - 1;
                }
            }

            _haveSequence = true;
            _lastSequence = sequence;
            for (int i = 0; i < lost; i++)
            {
                Conceal(frame);
                Interlocked.Increment(ref _concealed);
                Append(frame);
            }

            int n;
            try
            {
                n = _decoder.Decode(opus, frame);
            }
            catch (Exception)
            {
                n = -1;
            }

            if (n != FrameSamples)
            {
                Interlocked.Increment(ref _decodeErrors);
                Conceal(frame);
            }

            Append(frame);
        }
    }

    /// <summary>Writes up to 48 stereo frames (4 bytes each). Zeros while priming, after an underflow, or when muted.</summary>
    /// <returns>Bytes written.</returns>
    public int ReadStereoS16(Span<byte> dst, bool muted)
    {
        int frames = Math.Min(StereoFramesPerRead, dst.Length / 4);
        dst[..(frames * 4)].Clear();
        lock (_ringGate)
        {
            if (_closed || !_primed)
            {
                return frames * 4;
            }

            int available = Math.Min(_count, frames);
            bool underflow = _count < frames;
            for (int i = 0; i < available; i++)
            {
                short sample = _ring[(_head + i) % Capacity];
                // Muted: nothing of the audio is kept, so an unmute that runs dry ramps from silence.
                _last = muted ? (short)0 : sample;
                WriteFrame(dst, i, sample, muted);
            }

            _head = (_head + available) % Capacity;
            _count -= available;
            if (underflow)
            {
                // Ran dry: the last sample ramps to zero over the rest of this read instead of stepping to silence.
                int rest = frames - available;
                for (int k = 0; k < rest; k++)
                {
                    WriteFrame(dst, available + k, (short)(_last * (rest - k) / (rest + 1)), muted);
                }

                _last = 0;
                _primed = false;
            }
        }

        return frames * 4;
    }

    private static void WriteFrame(Span<byte> dst, int index, short sample, bool muted)
    {
        if (!muted)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dst[(index * 4)..], sample);
            BinaryPrimitives.WriteInt16LittleEndian(dst[(index * 4 + 2)..], sample);
        }
    }

    public void Clear()
    {
        lock (_decodeGate)
        {
            _haveSequence = false;
            ResetRing(close: false);
        }
    }

    public void Dispose()
    {
        lock (_decodeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Samples never outlive the session: the ring is wiped before the decoder goes.
            ResetRing(close: true);
            _decoder.Dispose();
        }
    }

    private void ResetRing(bool close)
    {
        lock (_ringGate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _primed = false;
            _last = 0;
            _closed |= close;
        }
    }

    private void Conceal(Span<short> frame)
    {
        int n;
        try
        {
            n = _decoder.DecodePlc(frame);
        }
        catch (Exception)
        {
            n = -1;
        }

        if (n != FrameSamples)
        {
            frame.Clear();
        }
    }

    private void Append(ReadOnlySpan<short> frame)
    {
        lock (_ringGate)
        {
            if (_closed)
            {
                return;
            }

            if (_count + frame.Length > Capacity)
            {
                int drop = _count + frame.Length - Capacity;
                _head = (_head + drop) % Capacity;
                _count -= drop;
            }

            int tail = (_head + _count) % Capacity;
            int first = Math.Min(frame.Length, Capacity - tail);
            frame[..first].CopyTo(_ring.AsSpan(tail));
            frame[first..].CopyTo(_ring);
            _count += frame.Length;

            if (_count > TrimAbove)
            {
                int drop = _count - TrimTo;
                _head = (_head + drop) % Capacity;
                _count = TrimTo;
            }

            if (!_primed && _count >= PrimeSamples)
            {
                _primed = true;
            }
        }
    }
}
