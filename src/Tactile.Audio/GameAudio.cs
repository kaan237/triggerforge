namespace Tactile.Audio;

public sealed class GameAudio
{
    private const int WindowFrames = 16;
    private const int SpeakerBatchFrames = 49;
    private const int BlockSamples = 64;
    private const int QueueCapacity = 3;
    // One-pole low-pass at approximately 400 Hz, before the 16:1 decimation.
    private const float LowPassCoefficient = 0.05f;
    private static readonly TimeSpan LiveWindow = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly sbyte[] _queue = new sbyte[QueueCapacity * BlockSamples];
    private readonly sbyte[] _pending = new sbyte[BlockSamples];
    private volatile float _strength = 1f;
    private volatile bool _lowPass;
    private int _head, _count, _samples, _frames;
    private float _sumLeft, _sumRight, _filteredLeft, _filteredRight;
    private bool _nonZero;
    private long? _lastNonZero;

    public GameAudio() : this(TimeProvider.System) { }

    public GameAudio(TimeProvider time) => _time = time;

    public float HapticStrength
    {
        get => _strength;
        set => _strength = float.IsNaN(value) ? 1f : Math.Clamp(value, 0f, 2f);
    }

    public bool LowPass { get => _lowPass; set => _lowPass = value; }

    public bool HapticsLive
    {
        get
        {
            lock (_gate)
            {
                return IsLive();
            }
        }
    }

    public event Action<ReadOnlySpan<float>>? Speaker;

    public void Push(ReadOnlySpan<short> frames4ch)
    {
        Span<float> stereo = stackalloc float[SpeakerBatchFrames * 2];
        bool lowPass = LowPass;
        float scale = HapticStrength / (WindowFrames * 256);
        Action<ReadOnlySpan<float>>? speaker = Speaker;
        while (frames4ch.Length >= 4)
        {
            int frames = Math.Min(SpeakerBatchFrames, frames4ch.Length / 4);
            lock (_gate)
            {
                for (int i = 0; i < frames; i++)
                {
                    int offset = i * 4;
                    if (speaker is not null)
                    {
                        stereo[i * 2] = frames4ch[offset] / 32768f;
                        stereo[i * 2 + 1] = frames4ch[offset + 1] / 32768f;
                    }

                    float left = frames4ch[offset + 2], right = frames4ch[offset + 3];
                    _filteredLeft += (left - _filteredLeft) * LowPassCoefficient;
                    _filteredRight += (right - _filteredRight) * LowPassCoefficient;
                    _sumLeft += lowPass ? _filteredLeft : left;
                    _sumRight += lowPass ? _filteredRight : right;

                    if (++_frames == WindowFrames)
                    {
                        sbyte l = Quantize(_sumLeft * scale), r = Quantize(_sumRight * scale);
                        _pending[_samples++] = l;
                        _pending[_samples++] = r;
                        _nonZero |= l != 0 || r != 0;
                        _sumLeft = _sumRight = 0;
                        _frames = 0;
                        if (_samples == BlockSamples)
                        {
                            Enqueue();
                        }
                    }
                }
            }

            speaker?.Invoke(stereo[..(frames * 2)]);
            frames4ch = frames4ch[(frames * 4)..];
        }
    }

    public bool ReadHaptics(Span<sbyte> payload64)
    {
        if (payload64.Length != BlockSamples)
        {
            throw new ArgumentException("Haptik paketi 64 örnek olmalı.", nameof(payload64));
        }

        lock (_gate)
        {
            return ReadNext(payload64);
        }
    }

    /// <summary>Replaces the fallback payload while game haptics are live, including during underruns.</summary>
    public void OverrideIfLive(Span<sbyte> payload64)
    {
        if (payload64.Length != BlockSamples)
        {
            throw new ArgumentException("Haptik paketi 64 örnek olmalı.", nameof(payload64));
        }

        lock (_gate)
        {
            if (IsLive())
            {
                ReadNext(payload64);
            }
            else
            {
                _head = _count = 0;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = _count = _samples = _frames = 0;
            _sumLeft = _sumRight = _filteredLeft = _filteredRight = 0;
            _nonZero = false;
            _lastNonZero = null;
        }
    }

    private void Enqueue()
    {
        _pending.CopyTo(_queue.AsSpan(((_head + _count) % QueueCapacity) * BlockSamples));
        if (_count == QueueCapacity)
        {
            _head = (_head + 1) % QueueCapacity;
        }
        else
        {
            _count++;
        }

        if (_nonZero)
        {
            _lastNonZero = _time.GetTimestamp();
        }

        _samples = 0;
        _nonZero = false;
    }

    private bool IsLive() => _lastNonZero is long last && _time.GetElapsedTime(last) < LiveWindow;

    private bool ReadNext(Span<sbyte> payload)
    {
        if (_count == 0)
        {
            payload.Clear();
            return false;
        }

        _queue.AsSpan(_head * BlockSamples, BlockSamples).CopyTo(payload);
        _head = (_head + 1) % QueueCapacity;
        _count--;
        return true;
    }

    private static sbyte Quantize(float sample) =>
        (sbyte)Math.Clamp(MathF.Round(sample), sbyte.MinValue, sbyte.MaxValue);
}
