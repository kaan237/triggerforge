using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Tactile.Audio;

namespace Tactile.Tests;

public sealed class BluetoothMicBufferTests
{
    private sealed class FakeDecoder : IMicDecoder
    {
        public int Plc;
        public int Decoded;
        public bool Disposed;
        public short Value = 1000;

        public int Decode(ReadOnlySpan<byte> packet, Span<short> mono480)
        {
            Decoded++;
            if (packet.Length == 0 || packet[0] == 0xFF)
            {
                return -4;
            }

            mono480[..480].Fill(Value);
            return 480;
        }

        public int DecodePlc(Span<short> mono480)
        {
            Plc++;
            mono480[..480].Fill(7);
            return 480;
        }

        public void Dispose() => Disposed = true;
    }

    private static readonly byte[] Packet = [1, 2, 3];

    [Fact]
    public void Opus_decoder_roundtrips_mono_frame_and_conceals_lost_frames()
    {
        byte[] opus = EncodeSine();
        using var decoder = new OpusDecoder();
        short[] pcm = new short[480];
        Assert.Equal(480, decoder.Decode(opus, pcm));
        Assert.True(pcm.Sum(s => (long)s * s) > 0);
        Assert.Equal(480, decoder.DecodePlc(pcm));

        using var buffer = new BluetoothMicBuffer(new OpusDecoder());
        buffer.Push(1, opus);
        Assert.Equal(480, buffer.BufferedSamples);
        buffer.Push(1, opus);
        Assert.Equal(480, buffer.BufferedSamples);
        buffer.Push(3, opus);
        Assert.Equal(1440, buffer.BufferedSamples);
        Assert.Equal(1, buffer.ConcealedFrames);

        buffer.Push(4, [0xFF, 0xFF, 0xFF]);
        buffer.Push(5, []);
    }

    [Fact]
    public void Sequence_gaps_use_plc_and_far_jumps_resync()
    {
        var fake = new FakeDecoder();
        using var buffer = new BluetoothMicBuffer(fake);
        buffer.Push(10, Packet);
        buffer.Push(10, Packet);
        buffer.Push(12, Packet);
        Assert.Equal(1, fake.Plc);
        Assert.Equal(3 * 480, buffer.BufferedSamples);
        buffer.Push(200, Packet);
        buffer.Push(255, Packet);
        buffer.Push(0, Packet);
        Assert.Equal(1, fake.Plc);

        buffer.Push(1, [0xFF]);
        Assert.Equal(1, buffer.DecodeErrors);
    }

    [Fact]
    public void Jitter_buffer_primes_at_30ms_trims_overflow_and_handles_underflow_and_mute()
    {
        var fake = new FakeDecoder();
        using var buffer = new BluetoothMicBuffer(fake);
        byte[] dst = new byte[192];
        byte seq = 0;

        buffer.Push(seq++, Packet);
        buffer.Push(seq++, Packet);
        Assert.Equal(192, buffer.ReadStereoS16(dst, false));
        Assert.All(dst, b => Assert.Equal(0, b));

        buffer.Push(seq++, Packet);
        buffer.ReadStereoS16(dst, false);
        for (int i = 0; i < 48; i++)
        {
            short l = BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(i * 4));
            short r = BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(i * 4 + 2));
            Assert.Equal(1000, l);
            Assert.Equal(l, r);
        }

        Assert.Equal(1440 - 48, buffer.BufferedSamples);

        // Muted reads write zeros but still drain.
        int before = buffer.BufferedSamples;
        buffer.ReadStereoS16(dst, true);
        Assert.All(dst, b => Assert.Equal(0, b));
        Assert.Equal(before - 48, buffer.BufferedSamples);

        // Drain to exactly empty (every size is a multiple of 48): the next read runs dry and ramps the last sample down.
        while (buffer.BufferedSamples >= 48)
        {
            buffer.ReadStereoS16(dst, false);
        }

        Assert.Equal(0, buffer.BufferedSamples);
        buffer.ReadStereoS16(dst, false);
        short previous = 1000;
        for (int i = 0; i < 48; i++)
        {
            short l = BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(i * 4));
            Assert.Equal(l, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(i * 4 + 2)));
            Assert.InRange(l, 0, previous);
            previous = l;
        }

        Assert.NotEqual(0, BinaryPrimitives.ReadInt16LittleEndian(dst));
        Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(47 * 4)), 0, 40);

        // Re-primes: silence until 30 ms are queued again.
        buffer.ReadStereoS16(dst, false);
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Muted_reads_keep_nothing_so_an_unmute_that_runs_dry_stays_silent()
    {
        var fake = new FakeDecoder();
        using var buffer = new BluetoothMicBuffer(fake);
        byte[] dst = new byte[192];
        for (byte seq = 0; seq < 3; seq++)
        {
            buffer.Push(seq, Packet);
        }

        // Muted until the ring is empty (it stays primed), then unmuted with nothing queued: no ramp from old audio.
        while (buffer.BufferedSamples >= 48)
        {
            buffer.ReadStereoS16(dst, true);
        }

        Assert.Equal(0, buffer.BufferedSamples);
        buffer.ReadStereoS16(dst, false);
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_short_tail_keeps_its_samples_and_fades_to_zero_in_the_same_read()
    {
        using var buffer = new BluetoothMicBuffer(new FakeDecoder());
        for (byte i = 0; i < 3; i++)
        {
            buffer.Push(i, Packet);
        }

        // 1440 samples in reads of 7 frames leave 1440 % 7 = 6 behind.
        byte[] small = new byte[7 * 4];
        while (buffer.BufferedSamples >= 7)
        {
            buffer.ReadStereoS16(small, false);
        }

        int tail = buffer.BufferedSamples;
        Assert.InRange(tail, 1, 6);
        buffer.ReadStereoS16(small, false);
        for (int i = 0; i < tail; i++)
        {
            Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(small.AsSpan(i * 4)));
        }

        short ramp = BinaryPrimitives.ReadInt16LittleEndian(small.AsSpan(tail * 4));
        Assert.InRange(ramp, 1, 999);
        Assert.Equal(0, buffer.BufferedSamples);
        short last = BinaryPrimitives.ReadInt16LittleEndian(small.AsSpan(6 * 4));
        Assert.InRange(last, 0, ramp);
    }

    [Fact]
    public void Push_trims_overflow_to_35ms()
    {
        using var buffer = new BluetoothMicBuffer(new FakeDecoder());
        for (byte i = 0; i < 7; i++)
        {
            buffer.Push(i, Packet);
        }

        Assert.Equal(1680, buffer.BufferedSamples);
    }

    [Fact]
    public void Dispose_is_idempotent_and_blocks_further_pushes()
    {
        var fake = new FakeDecoder();
        var buffer = new BluetoothMicBuffer(fake);
        buffer.Dispose();
        buffer.Dispose();
        buffer.Push(1, Packet);
        Assert.True(fake.Disposed);
        Assert.Equal(0, fake.Decoded);
    }

    [Fact]
    public async Task Concurrent_push_and_read_survive_dispose()
    {
        var buffer = new BluetoothMicBuffer(new FakeDecoder());
        using var stop = new CancellationTokenSource();
        Task pusher = Task.Run(() =>
        {
            byte seq = 0;
            while (!stop.IsCancellationRequested)
            {
                buffer.Push(seq++, Packet);
            }
        });
        Task reader = Task.Run(() =>
        {
            byte[] dst = new byte[192];
            while (!stop.IsCancellationRequested)
            {
                buffer.ReadStereoS16(dst, false);
            }
        });
        await Task.Delay(100);
        buffer.Dispose();
        await Task.Delay(20);
        await stop.CancelAsync();
        await Task.WhenAll(pusher, reader);
    }

    private static byte[] EncodeSine()
    {
        nint encoder = Create(48000, 1, 2049, out int error);
        Assert.Equal(0, error);
        try
        {
            short[] pcm = new short[480];
            for (int i = 0; i < pcm.Length; i++)
            {
                pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 8000);
            }

            byte[] packet = new byte[71];
            int n = Encode(encoder, pcm, 480, packet, packet.Length);
            Assert.True(n > 0);
            return packet[..n];
        }
        finally
        {
            Destroy(encoder);
        }
    }

    [DllImport("opus", EntryPoint = "opus_encoder_create")]
    private static extern nint Create(int rate, int channels, int application, out int error);

    [DllImport("opus", EntryPoint = "opus_encode")]
    private static extern int Encode(nint encoder, short[] pcm, int frameSize, byte[] data, int maxBytes);

    [DllImport("opus", EntryPoint = "opus_encoder_destroy")]
    private static extern void Destroy(nint encoder);

    private static short[] Ring(BluetoothMicBuffer buffer) =>
        (short[])typeof(BluetoothMicBuffer).GetField("_ring", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(buffer)!;

    [Fact]
    public void Clear_and_dispose_zero_the_ring_and_reads_after_dispose_are_silent()
    {
        var buffer = new BluetoothMicBuffer(new FakeDecoder());
        for (byte i = 1; i <= 4; i++)
        {
            buffer.Push(i, Packet);
        }

        Assert.Contains(Ring(buffer), s => s != 0);
        buffer.Clear();
        Assert.All(Ring(buffer), s => Assert.Equal(0, s));
        Assert.Equal(0, buffer.BufferedSamples);

        for (byte i = 1; i <= 4; i++)
        {
            buffer.Push(i, Packet);
        }

        buffer.Dispose();
        Assert.All(Ring(buffer), s => Assert.Equal(0, s));
        byte[] output = new byte[192];
        Array.Fill(output, (byte)0xAA);
        Assert.Equal(192, buffer.ReadStereoS16(output, muted: false));
        Assert.All(output, b => Assert.Equal(0, b));
        buffer.Push(9, Packet);
        Assert.Equal(0, buffer.BufferedSamples);
        Assert.All(Ring(buffer), s => Assert.Equal(0, s));
    }
}
