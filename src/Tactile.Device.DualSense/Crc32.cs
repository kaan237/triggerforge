namespace Tactile.Device.DualSense;

/// <summary>CRC-32 (IEEE, reflected) as used by DualSense Bluetooth reports.</summary>
public static class Crc32
{
    public const byte InputSeed = 0xA1;
    public const byte OutputSeed = 0xA2;
    public const byte FeatureSeed = 0xA3;

    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);

    /// <summary>CRC of <paramref name="seed"/> followed by <paramref name="data"/>.</summary>
    public static uint ComputeSeeded(byte seed, ReadOnlySpan<byte> data)
    {
        uint crc = Update(0xFFFFFFFF, [seed]);
        return ~Update(crc, data);
    }

    /// <summary>Writes the CRC of everything except the last 4 bytes into those bytes (little-endian).</summary>
    public static void Stamp(byte seed, Span<byte> report)
    {
        uint crc = ComputeSeeded(seed, report[..^4]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(report[^4..], crc);
    }

    public static bool Verify(byte seed, ReadOnlySpan<byte> report)
    {
        if (report.Length < 5)
        {
            return false;
        }

        uint stored = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report[^4..]);
        return stored == ComputeSeeded(seed, report[..^4]);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
