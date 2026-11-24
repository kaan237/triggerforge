using System.Buffers.Binary;

namespace Tactile.Emulation;

internal partial class VirtualDualSense
{
    private static readonly byte[] AudioConfig = Hex("""
        09 02 E3 00 04 01 00 C0 FA
        09 04 00 00 00 01 01 00 00
        0A 24 01 00 01 49 00 02 01 02
        0C 24 02 01 01 01 06 04 33 00 00 00
        0C 24 06 02 01 01 03 00 00 00 00 00
        09 24 03 03 01 03 04 02 00
        0C 24 02 04 02 04 03 02 03 00 00 00
        09 24 06 05 04 01 03 00 00
        09 24 03 06 01 01 01 05 00
        09 04 01 00 00 01 02 00 00
        09 04 01 01 01 01 02 00 00
        07 24 01 01 01 01 00
        0B 24 02 01 04 02 10 01 80 BB 00
        09 05 01 09 88 01 01 00 00
        07 25 01 00 00 00 00
        09 04 02 00 00 01 02 00 00
        09 04 02 01 01 01 02 00 00
        07 24 01 06 01 01 00
        0B 24 02 01 02 02 10 01 80 BB 00
        09 05 82 05 C0 00 01 00 00
        07 25 01 00 00 00 00
        09 04 03 00 02 03 00 00 00
        09 21 11 01 00 01 22 11 01
        07 05 84 03 40 00 04
        07 05 03 03 40 00 04
        """);

    internal sealed class WithAudio(UsbIpServer server, int busNum, DualSenseIdentity? identity)
        : VirtualDualSense(server, busNum, identity, audio: true), IVirtualAudio, IVirtualMic
    {
        private const int Speaker = 1;
        private const int Mic = 2;
        private const int MicPacketSize = 192;

        private readonly byte[] _mute = new byte[2];
        private readonly ushort[] _volume = [0x0000, 0x3000];
        private volatile int _alternates;
        private volatile MicAudioReader? _micReader;
        private int _micState;

        public bool SpeakerActive => Alternate(Speaker) == 1;
        public event GameAudioHandler? Audio;

        public bool MicActive => Alternate(Mic) == 1;
        public bool MicMuted => Volatile.Read(ref _mute[1]) != 0;
        public ushort MicVolume => Volatile.Read(ref _volume[1]);
        public event Action<bool>? MicActiveChanged;

        public void SetMicReader(MicAudioReader? reader) => _micReader = reader;

        public static int IsoPacketSize(uint endpoint, uint direction) => (endpoint, direction) switch
        {
            (1, 0) => 392,
            (2, 1) => MicPacketSize,
            _ => 0,
        };

        public void AudioOut(ReadOnlySpan<byte> data)
        {
            GameAudioHandler? handler = Audio;
            if (handler is null)
            {
                return;
            }

            const int bytesPerFrame = 4 * sizeof(short);
            Span<short> samples = stackalloc short[data.Length / bytesPerFrame * 4];
            // The device advertises hardware volume, so Windows leaves the PCM untouched and the
            // feature unit's mute/volume must be applied here (1/256 dB, -100..0 dB).
            float gain = Volatile.Read(ref _mute[0]) != 0 ? 0f : MathF.Pow(10f, (short)Volatile.Read(ref _volume[0]) / 256f / 20f);
            for (int i = 0; i < samples.Length; i++)
            {
                short raw = BinaryPrimitives.ReadInt16LittleEndian(data[(i * sizeof(short))..]);
                samples[i] = gain >= 1f ? raw : (short)(raw * gain);
            }

            handler(samples);
        }

        public int AudioIn(Span<byte> data)
        {
            if (!MicActive)
            {
                return 0;
            }

            int length = Math.Min(data.Length, MicPacketSize);
            Span<byte> packet = data[..length];
            packet.Clear();
            // The reader always runs, muted or not, so the buffer behind it never piles up stale audio for the unmute.
            MicAudioReader? reader = _micReader;
            if (reader is null)
            {
                return length;
            }

            try
            {
                reader(packet);
            }
            catch (Exception)
            {
                packet.Clear();
            }

            if (MicMuted)
            {
                packet.Clear();
            }
            else
            {
                ApplyMicVolume(packet, MicVolume);
            }

            return length;
        }

        /// <summary>
        /// The volume is a signed 1/256 dB value with 0x3000 as the unity top: 0x8000 is silence, every step below
        /// the top (a negative value included) attenuates, and the top or anything above it leaves the samples alone.
        /// The microphone is never amplified.
        /// </summary>
        internal static void ApplyMicVolume(Span<byte> packet, ushort volume)
        {
            short level = (short)volume;
            if (level == short.MinValue)
            {
                packet.Clear();
                return;
            }

            if (level >= 0x3000)
            {
                return;
            }

            float gain = MathF.Pow(10f, (level - 0x3000) / 256f / 20f);
            for (int i = 0; i + 1 < packet.Length; i += sizeof(short))
            {
                short raw = BinaryPrimitives.ReadInt16LittleEndian(packet[i..]);
                BinaryPrimitives.WriteInt16LittleEndian(packet[i..], (short)(raw * gain));
            }
        }

        private byte Alternate(int index) => (byte)((_alternates >> index) & 1);

        protected override void ResetAlternates() => SetAlternates(0);

        private void SetAlternates(int value)
        {
            _alternates = value;
            bool active = MicActive;
            if (Interlocked.Exchange(ref _micState, active ? 1 : 0) != (active ? 1 : 0))
            {
                try
                {
                    MicActiveChanged?.Invoke(active);
                }
                catch (Exception)
                {
                    // A faulty listener must not break the USB request that raised this.
                }
            }
        }

        protected override ControlResult Standard(SetupPacket s)
        {
            switch (s.Request)
            {
                case 0x0A: // GET_INTERFACE
                    return s.RequestType == 0x81 && s.Index <= 3
                        ? ControlResult.Ok([Alternate(s.Index)])
                        : ControlResult.Stall;
                case 0x0B: // SET_INTERFACE
                    if (s.RequestType != 0x01 || s.Index > 3 || s.Value > 1 ||
                        (s.Value == 1 && s.Index is not (Speaker or Mic)))
                    {
                        return ControlResult.Stall;
                    }

                    int mask = 1 << s.Index;
                    SetAlternates((_alternates & ~mask) | (s.Value << s.Index));
                    return ControlResult.Ok();
                case 0x09 when s.RequestType != 0x00 || s.Value > 1:
                    return ControlResult.Stall;
                default:
                    return base.Standard(s);
            }
        }

        protected override ControlResult ClassOrVendor(SetupPacket s, ReadOnlySpan<byte> data, long timestamp)
        {
            if (s.Type != 1 || s.Recipient != 1)
            {
                return ControlResult.Stall;
            }

            return (byte)s.Index switch
            {
                0 => FeatureUnit(s, data),
                3 => base.ClassOrVendor(s, data, timestamp),
                _ => ControlResult.Stall,
            };
        }

        private ControlResult FeatureUnit(SetupPacket s, ReadOnlySpan<byte> data)
        {
            int unit = s.Index >> 8;
            if (unit is not (2 or 5))
            {
                return ControlResult.Stall;
            }

            int slot = unit == 2 ? 0 : 1;
            int selector = s.Value >> 8;
            if (selector == 1 && s.Length == 1)
            {
                if (s.RequestType == 0x21 && s.Request == 0x01 && data.Length == 1)
                {
                    Volatile.Write(ref _mute[slot], data[0]);
                    return ControlResult.Ok();
                }

                return s.RequestType == 0xA1 && s.Request == 0x81
                    ? ControlResult.Ok([Volatile.Read(ref _mute[slot])])
                    : ControlResult.Stall;
            }

            if (selector != 2 || s.Length != sizeof(ushort))
            {
                return ControlResult.Stall;
            }

            if (s.RequestType == 0x21 && s.Request == 0x01 && data.Length == sizeof(ushort))
            {
                Volatile.Write(ref _volume[slot], BinaryPrimitives.ReadUInt16LittleEndian(data));
                return ControlResult.Ok();
            }

            if (s.RequestType != 0xA1 || s.Request is < 0x81 or > 0x84)
            {
                return ControlResult.Stall;
            }

            ushort value = s.Request switch
            {
                0x81 => Volatile.Read(ref _volume[slot]),
                0x82 => unit == 2 ? (ushort)0x9C00 : (ushort)0,
                0x83 => unit == 2 ? (ushort)0 : (ushort)0x3000,
                _ => unit == 2 ? (ushort)0x0100 : (ushort)0x007A,
            };
            byte[] reply = new byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(reply, value);
            return ControlResult.Ok(reply);
        }
    }
}
