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
    public void Report_types_share_sequence_and_packet_counter_and_wrap()
    {
        var builder = new HapticReport();
        var haptics = new sbyte[64];
        var opus = new byte[200];
        for (int i = 0; i <= 257; i++)
        {
            bool hasSpeaker = i % 2 == 1;
            byte[] report = hasSpeaker ? builder.BuildSpeaker(haptics, opus) : builder.Build(haptics);
            Assert.Equal(hasSpeaker ? 0x36 : 0x32, report[0]);
            Assert.Equal((i % 16) << 4, report[1]);
            Assert.Equal(i % 256, report[hasSpeaker ? 10 : 7]);
            Assert.True(Crc32.Verify(Crc32.OutputSeed, report));
        }

        builder.BuildPrime(OutputState.Default, OutputOptions.Default);
        Assert.Equal(0, builder.BuildSpeaker(haptics, opus)[10]);
    }
}
