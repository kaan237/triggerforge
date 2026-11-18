using System.Text;

namespace Tactile.Device.DualSense;

public enum CaptureKind : byte { Input = 0, Feature = 1 }

public readonly record struct CaptureRecord(long Microseconds, CaptureKind Kind, byte[] Data);

/// <summary>
/// Raw report capture format: "TCAP", version byte, link byte, then records of
/// [int64 µs][byte kind][uint16 length][bytes]. Little-endian.
/// </summary>
public static class CaptureFile
{
    private static readonly byte[] Magic = "TCAP"u8.ToArray();
    private const byte Version = 1;

    public static BinaryWriter Create(Stream stream, Link link)
    {
        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((byte)link);
        return writer;
    }

    public static void Append(BinaryWriter writer, CaptureRecord record)
    {
        writer.Write(record.Microseconds);
        writer.Write((byte)record.Kind);
        writer.Write((ushort)record.Data.Length);
        writer.Write(record.Data);
    }

    public static (Link Link, List<CaptureRecord> Records) Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic) || reader.ReadByte() != Version)
        {
            throw new InvalidDataException("Not a capture file or unsupported version.");
        }

        var link = (Link)reader.ReadByte();
        var records = new List<CaptureRecord>();
        while (stream.Position < stream.Length)
        {
            long us = reader.ReadInt64();
            var kind = (CaptureKind)reader.ReadByte();
            int length = reader.ReadUInt16();
            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
            {
                throw new InvalidDataException("Truncated record.");
            }

            records.Add(new CaptureRecord(us, kind, data));
        }

        return (link, records);
    }
}
