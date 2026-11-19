using Tactile.Core;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tactile.Audio;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat(ushort tag, ushort channels, int rate, ushort bits)
{
    internal const ushort Pcm = 1;
    internal const ushort Float = 3;
    internal const ushort Extensible = 0xfffe;

    public ushort Tag = tag;
    public ushort Channels = channels;
    public int Rate = rate;
    public int BytesPerSecond = rate * channels * (bits / 8);
    public ushort BlockAlign = (ushort)(channels * (bits / 8));
    public ushort Bits = bits;
    public ushort ExtraSize = 0;
}

internal sealed class AudioFormatConverter
{
    internal const int OutputRate = 48_000;
    private readonly WaveFormat _format;
    private readonly int _sampleBytes;
    private float[] _output = [];
    private long _phase;
    private float _previousLeft;
    private float _previousRight;
    private bool _hasPrevious;

    internal AudioFormatConverter(WaveFormat format)
    {
        _sampleBytes = format.Bits / 8;
        bool supported = format.Tag switch
        {
            WaveFormat.Float => format.Bits is 32 or 64,
            WaveFormat.Pcm => format.Bits is 8 or 16 or 24 or 32,
            _ => false
        };
        if (!supported || format.Channels == 0 || format.Rate <= 0 ||
            format.BlockAlign < format.Channels * _sampleBytes)
            throw new AudioUnavailableException(new LocalizedMessage("Unsupported audio format.", "Ses biçimi desteklenmiyor."));

        _format = format;
    }

    internal unsafe ReadOnlySpan<float> ConvertPacket(nint data, int frames, bool silent)
    {
        int byteCount = silent ? 0 : checked(frames * _format.BlockAlign);
        return Convert(new ReadOnlySpan<byte>((void*)data, byteCount), frames, silent);
    }

    internal ReadOnlySpan<float> Convert(ReadOnlySpan<byte> packet, int frames, bool silent = false)
    {
        if (frames == 0)
            return [];

        int maxSamples = checked((int)((((long)frames * OutputRate + _format.Rate - 1) / _format.Rate + 1) * 2));
        if (_output.Length < maxSamples)
            _output = new float[maxSamples];

        if (silent)
            _previousLeft = _previousRight = 0;

        int count = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * _format.BlockAlign;
            float left = silent ? 0 : ReadSample(packet[offset..]);
            float right = silent || _format.Channels == 1 ? left : ReadSample(packet[(offset + _sampleBytes)..]);
            if (_format.Rate == OutputRate)
            {
                _output[count++] = left;
                _output[count++] = right;
                continue;
            }

            if (!_hasPrevious)
            {
                _output[count++] = left;
                _output[count++] = right;
                _hasPrevious = true;
                _phase = _format.Rate;
            }
            else
            {
                while (_phase <= OutputRate)
                {
                    float fraction = (float)_phase / OutputRate;
                    _output[count++] = _previousLeft + (left - _previousLeft) * fraction;
                    _output[count++] = _previousRight + (right - _previousRight) * fraction;
                    _phase += _format.Rate;
                }
                _phase -= OutputRate;
            }

            _previousLeft = left;
            _previousRight = right;
        }

        return _output.AsSpan(0, count);
    }

    private float ReadSample(ReadOnlySpan<byte> sample)
    {
        if (_format.Tag == WaveFormat.Float)
        {
            double value = _sampleBytes == 4
                ? BinaryPrimitives.ReadSingleLittleEndian(sample)
                : BinaryPrimitives.ReadDoubleLittleEndian(sample);
            return double.IsFinite(value) ? (float)Math.Clamp(value, -1, 1) : 0;
        }

        return _sampleBytes switch
        {
            1 => (sample[0] - 128) / 128f,
            2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            3 => (sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f
        };
    }
}
