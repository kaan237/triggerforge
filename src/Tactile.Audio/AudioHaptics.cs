namespace Tactile.Audio;

/// <summary>Converts 48 kHz stereo audio to bounded 3 kHz stereo haptic blocks.</summary>
public sealed class AudioHaptics(HapticMode mode)
{
    private const int SampleRate = 48_000;
    private const int BlockSamples = 512 * 2;
    private const int PayloadSamples = 32 * 2;
    private const int Decimation = 16;
    private const int Capacity = 4;

    // Cutoffs in Hz, gate in dBFS, envelope attack/release in ms.
    private static readonly Settings Game = new(20, 400, -45, 0.15, 18, 0.55f, 1.5f, 0);
    private static readonly Settings Film = new(0, 140, -40, 8, 90, 0.20f, 4, 0);
    private static readonly Settings Music = new(0, 180, -50, 0.5, 25, 0.65f, 1.5f, 0.65f);
    private static readonly float TransientFollow = Follow(35);

    private readonly Lock _gate = new();
    private readonly float[] _blocks = new float[Capacity * BlockSamples];
    private readonly float[] _partial = new float[BlockSamples];
    private int _head;
    private int _count;
    private int _pending;
    private HapticMode _mode = mode;
    private HapticMode? _activeMode;
    private float _gain = 1;
    private Channel _left;
    private Channel _right;

    /// <summary>Gets or sets the mode applied to the next block read.</summary>
    public HapticMode Mode
    {
        get { lock (_gate) return _mode; }
        set { lock (_gate) _mode = value; }
    }

    /// <summary>Gets or sets output gain, clamped to 0–2.</summary>
    public float Gain
    {
        get { lock (_gate) return _gain; }
        set { lock (_gate) _gain = float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 2); }
    }

    /// <summary>Gets the number of complete blocks waiting to be read.</summary>
    public int BufferedBlocks
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Queues interleaved samples, retaining partial blocks between calls.</summary>
    public void Push(ReadOnlySpan<float> stereo48k)
    {
        while (!stereo48k.IsEmpty)
        {
            lock (_gate)
            {
                int take = Math.Min(stereo48k.Length, BlockSamples - _pending);
                stereo48k[..take].CopyTo(_partial.AsSpan(_pending));
                stereo48k = stereo48k[take..];
                _pending += take;
                if (_pending == BlockSamples)
                {
                    if (_count == Capacity)
                    {
                        _head = (_head + 1) % Capacity;
                        _count--;
                    }

                    int tail = (_head + _count) % Capacity;
                    _partial.CopyTo(_blocks.AsSpan(tail * BlockSamples, BlockSamples));
                    _count++;
                    _pending = 0;
                }
            }
        }
    }

    /// <summary>Fills 64 interleaved values, or silence and false when no whole block is queued.</summary>
    public bool Read(Span<sbyte> payload)
    {
        if (payload.Length != PayloadSamples)
            throw new ArgumentException("Haptik paketi 64 örnek içermeli.", nameof(payload));

        // Push holds the lock only for a short copy, so waiting here never stalls a tick.
        _gate.Enter();
        try
        {
            if (_count == 0)
            {
                payload.Clear();
                return false;
            }

            Settings settings = _mode switch
            {
                HapticMode.Film => Film,
                HapticMode.Music => Music,
                _ => Game
            };
            bool reset = _activeMode != _mode;
            if (reset)
            {
                _left = default;
                _right = default;
                _activeMode = _mode;
            }

            ReadOnlySpan<float> block = _blocks.AsSpan(_head * BlockSamples, BlockSamples);
            _head = (_head + 1) % Capacity;
            _count--;
            int sample = 0;
            for (int i = 0; i < payload.Length; i += 2)
            {
                float left = 0;
                float right = 0;
                for (int frame = 0; frame < Decimation; frame++)
                {
                    left += _left.Filter(block[sample++], settings);
                    right += _right.Filter(block[sample++], settings);
                }

                // Fade in the rebuilt filter over 2 ms after a reset.
                float fade = reset ? Math.Min(i / 12f, 1f) : 1f;
                float scale = _gain * fade / Decimation;
                payload[i] = Quantize(left * scale);
                payload[i + 1] = Quantize(right * scale);
            }
            return true;
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Discards queued samples and resets filter history.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _head = _count = _pending = 0;
            _activeMode = null;
        }
    }

    private static sbyte Quantize(float value) => (sbyte)MathF.Round(127 * MathF.Tanh(value));

    private static float Follow(double milliseconds) =>
        (float)(1 - Math.Exp(-1000 / (SampleRate * milliseconds)));

    private sealed class Settings(int highPass, int lowPass, int gateDb, double attackMs,
        double releaseMs, float threshold, float ratio, float transient)
    {
        public readonly Coefficients First = highPass == 0
            ? Coefficients.Create(lowPass, highPass: false)
            : Coefficients.Create(highPass, highPass: true);
        public readonly Coefficients Second = Coefficients.Create(lowPass, highPass: false);
        public readonly float Gate = MathF.Pow(10, gateDb / 20f);
        public readonly float Attack = Follow(attackMs);
        public readonly float Release = Follow(releaseMs);
        public readonly float Threshold = threshold;
        public readonly float Ratio = ratio;
        public readonly float Transient = transient;
    }

    private struct Channel
    {
        private Biquad _first;
        private Biquad _second;
        private float _envelope;
        private float _slowEnvelope;

        public float Filter(float sample, Settings settings)
        {
            sample = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
            sample = _second.Apply(_first.Apply(sample, settings.First), settings.Second);
            float peak = MathF.Abs(sample);
            _envelope += (peak - _envelope) * (peak > _envelope ? settings.Attack : settings.Release);
            _slowEnvelope += (_envelope - _slowEnvelope) * TransientFollow;
            if (_envelope <= settings.Gate)
                return 0;

            float gate = Math.Min((_envelope - settings.Gate) / settings.Gate, 1);
            float compressed = _envelope > settings.Threshold
                ? (settings.Threshold + (_envelope - settings.Threshold) / settings.Ratio) / _envelope
                : 1;
            float boost = Math.Max(0, _envelope - _slowEnvelope) * settings.Transient;
            return sample * gate * compressed * (1 + boost);
        }
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2)
    {
        public static Coefficients Create(int cutoff, bool highPass)
        {
            double omega = 2 * Math.PI * cutoff / SampleRate;
            double cos = Math.Cos(omega);
            double alpha = Math.Sin(omega) / Math.Sqrt(2);
            double a0 = 1 + alpha;
            double b0 = (highPass ? 1 + cos : 1 - cos) / (2 * a0);
            return new(b0, (highPass ? -2 : 2) * b0, b0, -2 * cos / a0, (1 - alpha) / a0);
        }
    }

    private struct Biquad
    {
        private double _z1;
        private double _z2;

        public float Apply(float sample, Coefficients c)
        {
            double output = c.B0 * sample + _z1;
            _z1 = c.B1 * sample - c.A1 * output + _z2;
            _z2 = c.B2 * sample - c.A2 * output;
            return (float)output;
        }
    }
}
