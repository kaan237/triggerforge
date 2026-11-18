using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class CaptureFileTests
{
    [Fact]
    public void Write_then_read_round_trips()
    {
        var stream = new MemoryStream();
        using (BinaryWriter writer = CaptureFile.Create(new NonClosingStream(stream), Link.Bluetooth))
        {
            CaptureFile.Append(writer, new CaptureRecord(5, CaptureKind.Feature, [0x05, 1, 2]));
            CaptureFile.Append(writer, new CaptureRecord(1000, CaptureKind.Input, [0x31, 9, 8, 7]));
        }

        stream.Position = 0;
        (Link link, List<CaptureRecord> records) = CaptureFile.Read(stream);

        Assert.Equal(Link.Bluetooth, link);
        Assert.Equal(2, records.Count);
        Assert.Equal(CaptureKind.Feature, records[0].Kind);
        Assert.Equal(1000, records[1].Microseconds);
        Assert.Equal(new byte[] { 0x31, 9, 8, 7 }, records[1].Data);
    }

    [Fact]
    public void Rejects_foreign_file()
    {
        Assert.Throws<InvalidDataException>(() => CaptureFile.Read(new MemoryStream("nope!"u8.ToArray())));
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) => Flush();
    }
}
