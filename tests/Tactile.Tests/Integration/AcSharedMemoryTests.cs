using System.Buffers.Binary;
using System.Text;
using Tactile.Core.Integration;

namespace Tactile.Tests.Integration;

public sealed class AcSharedMemoryTests
{
    private static void F(byte[] b, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), value);

    private static void I(byte[] b, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);

    private static byte[] Physics(int size, int packetId = 1)
    {
        byte[] b = new byte[size];
        I(b, 0, packetId);
        F(b, 4, 0.5f);
        F(b, 8, 0.25f);
        I(b, 16, 4);
        I(b, 20, 4000);
        F(b, 24, 0.5f);
        F(b, 28, 100);
        F(b, 56, -2); F(b, 60, 1); F(b, 64, 0.5f); F(b, 68, 3);
        F(b, 276, 0.7f);
        F(b, 364, 0.4f);
        return b;
    }

    private static byte[] Graphics(int status)
    {
        byte[] b = new byte[64];
        I(b, 4, status);
        return b;
    }

    private static byte[] Statics(int maxRpm = 8000, string? version = null)
    {
        byte[] b = new byte[456];
        I(b, 452, maxRpm);
        if (version is not null)
        {
            Encoding.Unicode.GetBytes(version).CopyTo(b, 0);
        }

        return b;
    }

    [Fact]
    public void Acc_parser_extracts_signals_assists_and_kerb_vibrations()
    {
        byte[] physics = Physics(800, 7);
        I(physics, 588, 5000);
        I(physics, 676, 1);
        I(physics, 672, 1);
        F(physics, 784, 0.6f);
        F(physics, 788, 0.2f);
        F(physics, 796, 0.1f);
        Assert.True(AccSharedMemory.TryParse(physics, Graphics(2), Statics(), out int packetId, out int status, out AcTelemetryFrame frame));
        Assert.Equal(7, packetId);
        Assert.Equal(2, status);
        Assert.True(frame.IsAcc);
        TelemetryValues v = frame.Values;
        Assert.Equal(1, v.RaceOn);
        Assert.Equal(100, v.SpeedKmh);
        Assert.Equal(0.8, v.Rpm, 6);
        Assert.Equal(0.8, frame.RevLights, 6);
        Assert.Equal(0, frame.Drs);
        Assert.Equal(0.5, v.Gas);
        Assert.Equal(0.25, v.Brake);
        Assert.Equal(0.4, v.Clutch, 6);
        Assert.Equal(3, v.Gear);
        Assert.Equal(0.5, v.Steer);
        Assert.Equal(2, v.SlipFront);
        Assert.Equal(3, v.SlipRear);
        Assert.Equal(0.7, v.Boost, 6);
        Assert.Equal(1, v.Abs);
        Assert.Equal(1, v.Traction);
        Assert.Equal(1, v.RumbleStrip);
        Assert.Equal(0.6, v.Surface, 6);

        // currentMaxRpm missing: falls back to the static value, then 8000.
        I(physics, 588, 0);
        AccSharedMemory.TryParse(physics, Graphics(2), Statics(10000), out _, out _, out frame);
        Assert.Equal(0.4, frame.Values.Rpm, 6);
        AccSharedMemory.TryParse(physics, Graphics(2), Statics(0), out _, out _, out frame);
        Assert.Equal(0.5, frame.Values.Rpm, 6);
    }

    [Fact]
    public void Ac_original_parser_extracts_drs_assists_and_suspension_kerb()
    {
        byte[] physics = Physics(712);
        F(physics, 200, 1);
        F(physics, 204, 0.3f);
        F(physics, 252, 1);
        F(physics, 48, 1);
        AcSuspensionState state = default;
        Assert.True(AcSharedMemory.TryParse(physics, Graphics(2), Statics(), ref state, out _, out _, out AcTelemetryFrame first));
        Assert.Equal(0, first.Values.RumbleStrip);
        F(physics, 184, 0.02f);
        Assert.True(AcSharedMemory.TryParse(physics, Graphics(2), Statics(), ref state, out _, out _, out AcTelemetryFrame frame));
        Assert.False(frame.IsAcc);
        Assert.Equal(1, frame.Drs);
        Assert.Equal(1, frame.Values.Abs);
        Assert.Equal(1, frame.Values.Traction);
        Assert.Equal(1, frame.Values.RumbleStrip);
        Assert.True(frame.Values.Surface > 0);
        Assert.Equal(0.5, frame.Values.Rpm, 6);
    }

    [Fact]
    public void Parsers_sanitize_non_finite_values_and_bad_max_rpm()
    {
        byte[] physics = Physics(712);
        F(physics, 4, float.NaN);
        F(physics, 28, float.PositiveInfinity);
        F(physics, 24, float.NegativeInfinity);
        F(physics, 56, float.NaN);
        F(physics, 276, float.NaN);
        AcSuspensionState state = default;
        Assert.True(AcSharedMemory.TryParse(physics, Graphics(2), Statics(-5), ref state, out _, out _, out AcTelemetryFrame frame));
        TelemetryValues v = frame.Values;
        Assert.Equal(0, v.Gas);
        Assert.Equal(0, v.SpeedKmh);
        Assert.Equal(0, v.Steer);
        Assert.Equal(1, v.SlipFront);
        Assert.Equal(0, v.Boost);
        Assert.Equal(0.5, v.Rpm, 6);
    }

    [Fact]
    public void Parsers_reject_buffers_shorter_than_the_minimum()
    {
        AcSuspensionState state = default;
        Assert.False(AcSharedMemory.TryParse(Physics(368)[..367], Graphics(2), Statics(), ref state, out _, out _, out _));
        Assert.False(AcSharedMemory.TryParse(Physics(712), new byte[7], Statics(), ref state, out _, out _, out _));
        Assert.False(AcSharedMemory.TryParse(Physics(712), Graphics(2), new byte[455], ref state, out _, out _, out _));
        Assert.False(AccSharedMemory.TryParse(Physics(799), Graphics(2), Statics(), out _, out _, out _));
        Assert.False(AccSharedMemory.IsAccBuffer(Physics(799), Statics(8000, "ACC 1.8")));
    }

    [Fact]
    public void Detection_uses_version_text_or_bytes_past_the_original_layout()
    {
        Assert.False(AccSharedMemory.IsAccBuffer(Physics(800), Statics(8000, "1.0")));
        Assert.True(AccSharedMemory.IsAccBuffer(Physics(800), Statics(8000, "1.8.12")));
        Assert.True(AccSharedMemory.IsAccBuffer(Physics(800), Statics(8000, "ACC")));
        byte[] physics = Physics(800);
        F(physics, 784, 0.5f);
        Assert.True(AccSharedMemory.IsAccBuffer(physics, Statics()));

        // A page-rounded original AC map: its own non-zero fields at 588/672/676 do not make it ACC.
        byte[] rounded = Physics(4096);
        I(rounded, 588, 12);
        I(rounded, 672, 3);
        I(rounded, 676, 3);
        Assert.False(AccSharedMemory.IsAccBuffer(rounded, Statics()));
    }

    [Fact]
    public void Poller_waits_silently_when_closed_throttles_to_60hz_and_closes_handles_when_packet_id_freezes()
    {
        TimeSpan t = TimeSpan.FromSeconds(10);
        FakeSource source = new() { Open = false };
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.False(reader.TryPoll(out _, out _));

        // Backoff: no read attempt within 500 ms of a failed one.
        source.Open = true;
        source.SetAc(Physics(712, 1), Graphics(2));
        t += TimeSpan.FromMilliseconds(100);
        Assert.False(reader.TryPoll(out _, out _));
        Assert.Equal(1, source.Reads);

        t += TimeSpan.FromMilliseconds(500);
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out bool changed));
        Assert.True(changed);
        Assert.Equal(1, frame.Values.RaceOn);
        Assert.False(frame.IsAcc);

        // Faster than 16 ms: skipped without touching the source.
        int reads = source.Reads;
        t += TimeSpan.FromMilliseconds(10);
        Assert.False(reader.TryPoll(out _, out _));
        Assert.Equal(reads, source.Reads);

        t += TimeSpan.FromMilliseconds(20);
        Assert.True(reader.TryPoll(out frame, out changed));
        Assert.False(changed);
        Assert.Equal(1, frame.Values.RaceOn);

        t += TimeSpan.FromMilliseconds(150);
        source.SetAc(Physics(712, 2), Graphics(2));
        Assert.True(reader.TryPoll(out frame, out changed));
        Assert.True(changed);
        Assert.Equal(1, frame.Values.RaceOn);
        // The failed open at the start closed once; a live read closes nothing.
        int closes = source.Closes;

        t += TimeSpan.FromMilliseconds(250);
        Assert.True(reader.TryPoll(out frame, out _));
        Assert.Equal(0, frame.Values.RaceOn);
        Assert.Equal(closes + 1, source.Closes);
    }

    [Fact]
    public void Frozen_buffer_after_reopen_never_reports_race_on_again()
    {
        TimeSpan t = TimeSpan.FromSeconds(1);
        FakeSource source = new();
        source.SetAc(Physics(712, 5), Graphics(2));
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.True(reader.TryPoll(out _, out _));

        t += TimeSpan.FromMilliseconds(300);
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out _));
        Assert.Equal(0, frame.Values.RaceOn);

        for (int i = 0; i < 20; i++)
        {
            t += TimeSpan.FromMilliseconds(600);
            Assert.False(reader.TryPoll(out _, out _));
        }

        // Only a new packet id brings it back.
        source.SetAc(Physics(712, 6), Graphics(2));
        t += TimeSpan.FromMilliseconds(600);
        Assert.True(reader.TryPoll(out frame, out bool changed));
        Assert.True(changed);
        Assert.Equal(1, frame.Values.RaceOn);
    }

    [Fact]
    public void Pause_emits_exactly_one_race_off_frame_and_closes()
    {
        TimeSpan t = TimeSpan.FromSeconds(1);
        FakeSource source = new();
        source.SetAc(Physics(712, 1), Graphics(2));
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.True(reader.TryPoll(out _, out _));

        t += TimeSpan.FromMilliseconds(20);
        source.SetAc(Physics(712, 2), Graphics(3));
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out _));
        Assert.Equal(0, frame.Values.RaceOn);
        Assert.Equal(1, source.Closes);

        for (int i = 0; i < 5; i++)
        {
            t += TimeSpan.FromMilliseconds(600);
            source.SetAc(Physics(712, 3 + i), Graphics(3));
            Assert.False(reader.TryPoll(out _, out _));
        }

        t += TimeSpan.FromMilliseconds(600);
        source.SetAc(Physics(712, 20), Graphics(2));
        Assert.True(reader.TryPoll(out frame, out _));
        Assert.Equal(1, frame.Values.RaceOn);
    }

    [Fact]
    public void Repeated_packet_id_keeps_the_last_frame_instead_of_a_zero_suspension_delta()
    {
        TimeSpan t = TimeSpan.FromSeconds(1);
        FakeSource source = new();
        source.SetAc(Physics(712, 1), Graphics(2));
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.True(reader.TryPoll(out _, out _));

        byte[] bump = Physics(712, 2);
        F(bump, 184, 0.02f);
        source.SetAc(bump, Graphics(2));
        t += TimeSpan.FromMilliseconds(20);
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out bool changed));
        Assert.True(changed);
        Assert.Equal(1, frame.Values.RumbleStrip);

        t += TimeSpan.FromMilliseconds(20);
        Assert.True(reader.TryPoll(out frame, out changed));
        Assert.False(changed);
        Assert.Equal(1, frame.Values.RumbleStrip);
        Assert.Equal(1, frame.Values.RaceOn);
    }

    [Fact]
    public void Read_failure_while_driving_ends_the_session_once()
    {
        TimeSpan t = TimeSpan.FromSeconds(1);
        FakeSource source = new();
        source.SetAc(Physics(712, 1), Graphics(2));
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.True(reader.TryPoll(out _, out _));

        source.Open = false;
        t += TimeSpan.FromMilliseconds(20);
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out bool changed));
        Assert.False(changed);
        Assert.Equal(0, frame.Values.RaceOn);

        t += TimeSpan.FromMilliseconds(600);
        Assert.False(reader.TryPoll(out _, out _));

        // A later open with the menu showing does not produce a late race-off frame.
        source.Open = true;
        source.SetAc(Physics(712, 5), Graphics(3));
        t += TimeSpan.FromMilliseconds(600);
        Assert.False(reader.TryPoll(out _, out _));
    }

    [Fact]
    public void Unauthorized_access_is_treated_like_a_closed_game()
    {
        TimeSpan t = TimeSpan.FromSeconds(1);
        FakeSource source = new() { Throw = new UnauthorizedAccessException() };
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.False(reader.TryPoll(out _, out _));
        Assert.Equal(1, source.Closes);

        source.Throw = null;
        source.SetAc(Physics(712, 1), Graphics(2));
        t += TimeSpan.FromMilliseconds(600);
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out _));
        Assert.Equal(1, frame.Values.RaceOn);
    }

    [Fact]
    public void Poller_detects_acc_and_handles_page_rounded_ac_maps()
    {
        TimeSpan t = TimeSpan.FromSeconds(1);
        FakeSource source = new();
        byte[] ac = new byte[4096];
        Physics(712, 1).CopyTo(ac, 0);
        I(ac, 588, 77);
        source.SetAc(ac, Graphics(2));
        using AccSharedMemoryReader reader = new(() => t, source);
        Assert.True(reader.TryPoll(out AcTelemetryFrame frame, out _));
        Assert.False(frame.IsAcc);

        byte[] acc = Physics(4096, 2);
        F(acc, 784, 0.9f);
        source.SetAc(acc, Graphics(2));
        t += TimeSpan.FromMilliseconds(20);
        Assert.True(reader.TryPoll(out frame, out _));
        Assert.True(frame.IsAcc);
        Assert.Equal(1, frame.Values.RumbleStrip);
    }

    private sealed class FakeSource : ISharedMemorySource
    {
        private byte[] _physics = [];
        private byte[] _graphics = [];

        public bool Open { get; set; } = true;
        public Exception? Throw { get; set; }
        public int Reads { get; private set; }
        public int Closes { get; private set; }

        public void SetAc(byte[] physics, byte[] graphics)
        {
            _physics = physics;
            _graphics = graphics;
        }

        public bool TryRead(Span<byte> physics, out int physicsLength, Span<byte> graphics, out int graphicsLength, Span<byte> statics, out int staticsLength)
        {
            Reads++;
            physicsLength = graphicsLength = staticsLength = 0;
            if (Throw is not null)
            {
                throw Throw;
            }

            if (!Open)
            {
                return false;
            }

            byte[] fixedStatics = Statics();
            _physics.CopyTo(physics);
            _graphics.CopyTo(graphics);
            fixedStatics.CopyTo(statics);
            physicsLength = _physics.Length;
            graphicsLength = _graphics.Length;
            staticsLength = fixedStatics.Length;
            return true;
        }

        public void Close() => Closes++;

        public void Dispose()
        {
        }
    }
}
