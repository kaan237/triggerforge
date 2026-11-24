using Tactile.Device.DualSense;

namespace Tactile.Tests;

public sealed class SpeakerReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Combined_report_matches_protocol_and_last_prime(bool includeState)
    {
        var builder = new HapticReport { IncludeStateBlock = includeState, BufferLength = 16 };
        byte[] prime = builder.BuildPrime(OutputState.Default with { SpeakerVolume = 55 }, OutputOptions.Default);
        sbyte[] haptics = Enumerable.Range(-32, 64).Select(i => (sbyte)i).ToArray();
        byte[] opus = Enumerable.Range(1, 200).Select(i => (byte)i).ToArray();
        byte[] report = builder.BuildSpeaker(haptics, opus);

        Assert.Equal(398, report.Length);
        Assert.Equal(new byte[] { 0x36, 0, 0x91, 7, 0xFE, 0x40, 0x40, 0x40, 0x40, 0x40, 0 }, report[..11]);
        int shift = includeState ? 65 : 0;
        if (includeState)
        {
            Assert.Equal(prime[2..67], report[11..76]);
        }

        Assert.Equal(new byte[] { 0x92, 64 }, report[(11 + shift)..(13 + shift)]);
        Assert.Equal(haptics.Select(i => unchecked((byte)i)), report[(13 + shift)..(77 + shift)]);
        Assert.Equal(new byte[] { 0x93, 200 }, report[(77 + shift)..(79 + shift)]);
        Assert.Equal(opus, report[(79 + shift)..(279 + shift)]);
        Assert.All(report[(279 + shift)..394], value => Assert.Equal(0, value));
        Assert.True(Crc32.Verify(Crc32.OutputSeed, report));
    }

    [Fact]
    public void Double_report_matches_protocol()
    {
        var builder = new HapticReport { IncludeStateBlock = true, BufferLength = 16 };
        sbyte[] haptics1 = Enumerable.Range(-64, 64).Select(i => (sbyte)i).ToArray();
        sbyte[] haptics2 = Enumerable.Range(0, 64).Select(i => (sbyte)i).ToArray();
        byte[] opus1 = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        byte[] opus2 = Enumerable.Range(1, 200).Select(i => (byte)i).Reverse().ToArray();
        byte[] report = builder.BuildSpeakerDouble(haptics1, haptics2, opus1, opus2);

        Assert.Equal(547, report.Length);
        Assert.Equal(new byte[] { 0x39, 0, 0x91, 6, 0x7E, 0x51, 0x1F, 0x62, 0x3C, 0, 0xD5, 0xC8 }, report[..12]);
        Assert.Equal(opus1, report[12..212]);
        Assert.Equal(opus2, report[212..412]);
        Assert.Equal(new byte[] { 0xD2, 0x40 }, report[412..414]);
        Assert.Equal(haptics1.Select(i => unchecked((byte)i)), report[414..478]);
        Assert.Equal(haptics2.Select(i => unchecked((byte)i)), report[478..542]);
        Assert.Equal(0, report[542]);
        Assert.True(Crc32.Verify(Crc32.OutputSeed, report));
        Assert.False(Crc32.Verify(Crc32.InputSeed, report));
    }

    [Fact]
    public void Report_types_share_sequence_and_packet_counter_and_wrap()
    {
        var builder = new HapticReport();
        var haptics = new sbyte[64];
        var opus = new byte[200];
        for (int i = 0; i <= 257; i++)
        {
            (byte[] report, int id, int counterOffset) = (i % 3) switch
            {
                0 => (builder.Build(haptics), 0x32, 7),
                1 => (builder.BuildSpeaker(haptics, opus), 0x36, 10),
                _ => (builder.BuildSpeakerDouble(haptics, haptics, opus, opus), 0x39, 9),
            };
            Assert.Equal(id, report[0]);
            Assert.Equal((i % 16) << 4, report[1]);
            Assert.Equal(i % 256, report[counterOffset]);
            Assert.True(Crc32.Verify(Crc32.OutputSeed, report));
        }

        builder.BuildPrime(OutputState.Default, OutputOptions.Default);
        Assert.Equal(0, builder.BuildSpeaker(haptics, opus)[10]);
    }

    [Fact]
    public void Mic_flag_sets_bit0_on_0x32_0x36_0x39_and_standalone_mic_control_has_valid_crc()
    {
        var builder = new HapticReport();
        var haptics = new sbyte[64];
        byte[] opus = new byte[200];
        Assert.Equal(0x66, builder.Build(haptics)[4]);
        Assert.Equal(0xFE, builder.BuildSpeaker(haptics, opus)[4]);
        Assert.Equal(0x7E, builder.BuildSpeakerDouble(haptics, haptics, opus, opus)[4]);

        builder.MicEnabled = true;
        Assert.Equal(0x67, builder.Build(haptics)[4]);
        Assert.Equal(0xFF, builder.BuildSpeaker(haptics, opus)[4]);
        Assert.Equal(0x7F, builder.BuildSpeakerDouble(haptics, haptics, opus, opus)[4]);

        foreach (bool enable in new[] { true, false })
        {
            byte[] control = builder.BuildMicControl(enable);
            Assert.Equal(142, control.Length);
            Assert.Equal(0x32, control[0]);
            Assert.Equal(new byte[] { 0x91, 0x07, enable ? (byte)0xFF : (byte)0xFE }, control[2..5]);
            Assert.True(Crc32.Verify(Crc32.OutputSeed, control));
        }

        var block = new byte[OutputReport.StateLength];
        OutputReport.WriteState(OutputState.Default, block, new OutputOptions(MicStream: true, MicGain: 0x80, MicMuted: true));
        Assert.Equal(0x40, block[0] & 0x40);
        Assert.Equal(0x02, block[1] & 0x02);
        Assert.Equal(0x40, block[6]);
        Assert.Equal(0x10, block[9]);

        var plain = new byte[OutputReport.StateLength];
        OutputReport.WriteState(OutputState.Default, plain);
        Assert.Equal(0, plain[0] & 0x40);
        Assert.Equal(0, plain[1] & 0x02);
        Assert.Equal(0, plain[6]);
        Assert.Equal(0, plain[9]);
    }

    [Fact]
    public void Main_bluetooth_report_carries_the_mic_bits_only_while_the_stream_is_on()
    {
        // The 0x31 report: 3 header bytes, then the state block (so state offset n is report offset n + 3).
        byte[] on = OutputReport.BuildBluetooth(OutputState.Default, 3, new OutputOptions(MicStream: true, MicGain: 0x30, MicMuted: true));
        Assert.Equal(0x31, on[0]);
        Assert.Equal(0x40, on[3] & 0x40);
        Assert.Equal(0x02, on[4] & 0x02);
        Assert.Equal(0x30, on[9]);
        Assert.Equal(0x10, on[12]);
        Assert.True(Crc32.Verify(Crc32.OutputSeed, on));

        byte[] unmuted = OutputReport.BuildBluetooth(OutputState.Default, 3, new OutputOptions(MicStream: true));
        Assert.Equal(0x40, unmuted[9]);
        Assert.Equal(0, unmuted[12]);

        byte[] off = OutputReport.BuildBluetooth(OutputState.Default, 4, new OutputOptions(MicStream: false, MicGain: 0x30, MicMuted: true));
        Assert.Equal(0, off[3] & 0x40);
        Assert.Equal(0, off[4] & 0x02);
        Assert.Equal(0, off[9]);
        Assert.Equal(0, off[12]);
        Assert.True(Crc32.Verify(Crc32.OutputSeed, off));
    }
}
