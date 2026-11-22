using System.Numerics;
using Tactile.Device.DualSense;

namespace Tactile.Core;

internal sealed class HapticMixer(Func<OutputState> output)
{
    private const Buttons LeftButtons = Buttons.Up | Buttons.Down | Buttons.Left | Buttons.Right | Buttons.L1 | Buttons.L3 | Buttons.Create;
    private const Buttons RightButtons = Buttons.Square | Buttons.Cross | Buttons.Circle | Buttons.Triangle | Buttons.R1 | Buttons.R3 | Buttons.Options;
    private const Buttons CentreButtons = Buttons.PS | Buttons.Touchpad;
    private const int TriggerThreshold = 128;
    private const int ClickFrames = 36;
    private const int RampFrames = 30;
    private const float FullScale = 127;
    private const float Tau = 2 * MathF.PI;
    private static readonly float[] Click = Enumerable.Range(0, ClickFrames)
        .Select(i => FullScale * MathF.Sin(Tau * 250 * i / HapticReport.SampleRate) * (1f - i / (float)ClickFrames))
        .ToArray();

    private static readonly float[] Confirm = new float[ClickFrames * 4];

    static HapticMixer()
    {
        for (int i = 0; i < ClickFrames; i++)
        {
            Confirm[i] = Confirm[i + ClickFrames * 3] = Click[i] * 0.65f;
        }
    }

    private int _confirmPending;
    private int _confirmFrame;
    private readonly Lock _clickLock = new();
    private bool _connected;
    private bool _clicks;
    private Buttons _buttons;
    private byte _l2;
    private byte _r2;
    private int _leftPending;
    private int _rightPending;
    private int _leftTail;
    private int _rightTail;
    private int _resetRumble;
    private Motor _left;
    private Motor _right;

    internal void PulseConfirm()
    {
        lock (_clickLock)
        {
            _confirmPending++;
        }
    }

    internal void ResetRumble()
    {
        Volatile.Write(ref _resetRumble, 1);
        lock (_clickLock)
        {
            _confirmPending = _confirmFrame = 0;
        }
    }

    internal void ConfigureClicks(bool connected, bool enabled)
    {
        lock (_clickLock)
        {
            _connected = connected;
            _clicks = connected && enabled;
            if (!_clicks)
            {
                _leftPending = _rightPending = _leftTail = _rightTail = 0;
            }

            if (!connected)
            {
                _buttons = Buttons.None;
                _l2 = _r2 = 0;
            }
        }
    }

    internal void Observe(InputState input)
    {
        lock (_clickLock)
        {
            if (!_connected)
            {
                return;
            }

            if (_clicks)
            {
                Buttons pressed = input.Buttons & ~_buttons;
                int centre = BitOperations.PopCount((uint)(pressed & CentreButtons));
                _leftPending += centre + BitOperations.PopCount((uint)(pressed & LeftButtons))
                    + (_l2 < TriggerThreshold && input.L2 >= TriggerThreshold ? 1 : 0);
                _rightPending += centre + BitOperations.PopCount((uint)(pressed & RightButtons))
                    + (_r2 < TriggerThreshold && input.R2 >= TriggerThreshold ? 1 : 0);
            }

            _buttons = input.Buttons;
            _l2 = input.L2;
            _r2 = input.R2;
        }
    }

    internal void Mix(Span<sbyte> samples, float audioStrength, float clickStrength)
    {
        if (Interlocked.Exchange(ref _resetRumble, 0) != 0)
        {
            _left = _right = default;
        }

        OutputState state = output();
        _left.SetTarget(state.RumbleLeft);
        _right.SetTarget(state.RumbleRight);
        Span<float> confirm = stackalloc float[HapticReport.FramesPerReport];
        confirm.Clear();
        int leftClicks, rightClicks, leftTail, rightTail;
        lock (_clickLock)
        {
            leftClicks = _leftPending;
            rightClicks = _rightPending;
            leftTail = _leftTail;
            rightTail = _rightTail;
            _leftTail = leftClicks;
            _rightTail = rightClicks;
            _leftPending = _rightPending = 0;
            for (int i = 0; i < confirm.Length && _confirmPending > 0; i++)
            {
                confirm[i] = Confirm[_confirmFrame++];
                if (_confirmFrame == Confirm.Length)
                {
                    _confirmFrame = 0;
                    _confirmPending--;
                }
            }
        }

        for (int frame = 0; frame < HapticReport.FramesPerReport; frame++)
        {
            int i = frame * 2;
            int tailIndex = frame + HapticReport.FramesPerReport;
            float tail = tailIndex < ClickFrames ? Click[tailIndex] : 0;
            samples[i] = Quantize(samples[i] * audioStrength + _left.Next(50) + confirm[frame]
                + (leftClicks * Click[frame] + leftTail * tail) * clickStrength);
            samples[i + 1] = Quantize(samples[i + 1] * audioStrength + _right.Next(175) + confirm[frame]
                + (rightClicks * Click[frame] + rightTail * tail) * clickStrength);
        }
    }

    private static sbyte Quantize(float sample) => (sbyte)Math.Clamp(MathF.Round(sample), -FullScale, FullScale);

    private struct Motor
    {
        private byte _target;
        private float _amplitude;
        private float _increment;
        private float _phase;
        private int _remaining;

        public void SetTarget(byte value)
        {
            if (value != _target)
            {
                _target = value;
                _increment = (value * (FullScale / byte.MaxValue) - _amplitude) / RampFrames;
                _remaining = RampFrames;
            }
        }

        public float Next(int frequency)
        {
            if (_remaining > 0)
            {
                _amplitude += _increment;
                if (--_remaining == 0)
                {
                    _amplitude = _target * (FullScale / byte.MaxValue);
                }
            }

            float sample = _amplitude * MathF.Sin(_phase);
            _phase += Tau * frequency / HapticReport.SampleRate;
            if (_phase >= Tau)
            {
                _phase -= Tau;
            }

            return sample;
        }
    }
}
