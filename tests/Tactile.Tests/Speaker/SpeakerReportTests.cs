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
}
