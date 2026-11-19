using Tactile.Core;
namespace Tactile.Audio;

public sealed class SpeakerFeed : IDisposable
{
    internal const int InputFrames = 512;
    private const int BlockSamples = InputFrames * OpusEncoder.Channels;
    private const int Slots = 5; // Four complete blocks plus the in-progress block.
    private readonly Lock _gate = new();
    private readonly float[] _blocks = new float[Slots * BlockSamples];
    private readonly OpusEncoder _encoder = new();
    internal const int PrimeBlocks = 2;
    private bool _primed;
    private int _head;
    private int _count;
    private int _pending;
    private volatile float _gain = 1f;

    public float Gain
    {
        get => _gain;
        set => _gain = float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    }

    public void Push(ReadOnlySpan<float> stereo48k)
    {
        while (!stereo48k.IsEmpty)
        {
            lock (_gate)
            {
                int tail = (_head + _count) % Slots;
                int take = Math.Min(stereo48k.Length, BlockSamples - _pending);
                stereo48k[..take].CopyTo(_blocks.AsSpan(tail * BlockSamples + _pending));
                stereo48k = stereo48k[take..];
                _pending += take;
                if (_pending == BlockSamples)
                {
                    if (++_count == Slots)
                    {
                        _head = (_head + 1) % Slots;
                        _count--;
                    }

                    _pending = 0;
                }
            }
        }
    }

    public bool Read(Span<byte> frame200)
    {
        if (frame200.Length != OpusEncoder.FrameBytes)
        {
            throw new ArgumentException(Text.Pick("The Opus packet must be 200 bytes.", "Opus paketi 200 bayt olmalı."), nameof(frame200));
        }

        Span<float> block = stackalloc float[BlockSamples];
        lock (_gate)
        {
            // Jitter buffer: capture and the Bluetooth tick run on separate clocks, so reading the moment a
            // block completes would underrun now and then. Start (and restart after an underrun) only once
            // PrimeBlocks are queued; each underrun otherwise becomes an audible click.
            if (_count == 0)
            {
                _primed = false;
                return false;
            }

            if (!_primed && _count < PrimeBlocks)
            {
                return false;
            }

            _primed = true;

            _blocks.AsSpan(_head * BlockSamples, BlockSamples).CopyTo(block);
            _head = (_head + 1) % Slots;
            _count--;
        }

        Span<float> resampled = stackalloc float[OpusEncoder.FrameSamples * OpusEncoder.Channels];
        Resample(block, resampled, Gain);
        _encoder.Encode(resampled, frame200);
        return true;
    }

    public void EncodeSilence(Span<byte> frame200)
    {
        Span<float> silence = stackalloc float[OpusEncoder.FrameSamples * OpusEncoder.Channels];
        silence.Clear();
        _encoder.Encode(silence, frame200);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = _count = _pending = 0;
            _primed = false;
        }
    }

    public void Dispose() => _encoder.Dispose();

    internal static void Resample(ReadOnlySpan<float> input, Span<float> output, float gain)
    {
        for (int frame = 0; frame < OpusEncoder.FrameSamples; frame++)
        {
            int position = frame * InputFrames;
            int offset = position / OpusEncoder.FrameSamples * 2;
            float fraction = (position % OpusEncoder.FrameSamples) / (float)OpusEncoder.FrameSamples;
            float first = (input[offset] + input[offset + 1]) * 0.5f;
            float next = (input[offset + 2] + input[offset + 3]) * 0.5f;
            output[frame * 2] = output[frame * 2 + 1] = (first + (next - first) * fraction) * gain;
        }
    }
}
