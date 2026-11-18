using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class Crc32Tests
{
    [Fact]
    public void Matches_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void Seeded_equals_seed_byte_prepended()
    {
        byte[] data = [0x31, 0x10, 0x20, 0x30];
        Assert.Equal(Crc32.Compute([Crc32.OutputSeed, .. data]), Crc32.ComputeSeeded(Crc32.OutputSeed, data));
    }

    [Fact]
    public void Stamp_then_verify_round_trips()
    {
        byte[] report = new byte[78];
        report[0] = 0x31;
        report[2] = 0x10;
        Crc32.Stamp(Crc32.OutputSeed, report);

        Assert.True(Crc32.Verify(Crc32.OutputSeed, report));
        Assert.False(Crc32.Verify(Crc32.InputSeed, report));

        report[10] ^= 0xFF;
        Assert.False(Crc32.Verify(Crc32.OutputSeed, report));
    }
}
