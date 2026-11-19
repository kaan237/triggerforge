using System.Buffers.Binary;
using Tactile.Core.Integration;

namespace Tactile.Tests.Integration;

public sealed class ForzaPacketTests
{
    [Theory]
    [InlineData(232, ForzaFormat.Sled)]
    [InlineData(311, ForzaFormat.Fm7)]
    [InlineData(323, ForzaFormat.Fh)]
    [InlineData(324, ForzaFormat.Fh)]
    [InlineData(331, ForzaFormat.Fm)]
    public void Parses_all_documented_layouts(int size, ForzaFormat format)
    {
        byte[] packet = Packet(size);
        Assert.True(ForzaPacket.TryParse(packet, out TelemetryFrame frame));
        Assert.Equal(format, frame.Format);
        TelemetryValues values = frame.Values;
        Assert.Equal(1, values.RaceOn);
        Assert.Equal(size == 232 ? 46.8 : 72, values.SpeedKmh, 6);
        Assert.Equal(0.5, values.Rpm);
        Assert.Equal(2, values.SlipFront);
        Assert.Equal(3, values.SlipRear);
        Assert.Equal(0.875, values.Surface);
        Assert.Equal(1, values.RumbleStrip);
        if (size == 232)
        {
            Assert.Equal(0, values.Gas);
            Assert.Equal(0, values.Brake);
            Assert.Equal(0, values.Clutch);
            Assert.Equal(0, values.Handbrake);
            Assert.Equal(0, values.Gear);
            Assert.Equal(0, values.Steer);
            Assert.Equal(0, values.Boost);
            Assert.Equal(0, values.Abs);
            Assert.Equal(0, values.Traction);
        }
        else
        {
            Assert.Equal(128 / 255.0, values.Gas);
            Assert.Equal(64 / 255.0, values.Brake);
            Assert.Equal(32 / 255.0, values.Clutch);
            Assert.Equal(16 / 255.0, values.Handbrake);
            Assert.Equal(4, values.Gear);
            Assert.Equal(-63 / 127.0, values.Steer);
            Assert.Equal(12.5, values.Boost);
            Assert.Equal(1, values.Abs);
            Assert.Equal(1, values.Traction);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(231)]
    [InlineData(233)]
    [InlineData(312)]
    [InlineData(325)]
    [InlineData(332)]
    [InlineData(20 * 1024)]
    public void Rejects_other_sizes(int size) => Assert.False(ForzaPacket.TryParse(new byte[size], out _));

    [Fact]
    public void Nonfinite_floats_are_zero_and_invalid_rpm_range_cannot_divide()
    {
        byte[] packet = Packet(324);
        WriteFloat(packet, 8, float.NaN);
        WriteFloat(packet, 184, float.NegativeInfinity);
        WriteFloat(packet, 188, float.PositiveInfinity);
        WriteFloat(packet, 256, float.PositiveInfinity);
        WriteFloat(packet, 284, float.NaN);
        packet[320] = 128;
        Assert.True(ForzaPacket.TryParse(packet, out TelemetryFrame frame));
        Assert.Equal(0, frame.Values.Rpm);
        Assert.Equal(0.5, frame.Values.SlipFront);
        Assert.Equal(0.75, frame.Values.SlipRear);
        Assert.Equal(0, frame.Values.SpeedKmh);
        Assert.Equal(0, frame.Values.Boost);
        Assert.Equal(-1, frame.Values.Steer);
        Assert.Equal(0, frame.Values.Abs);
        Assert.Equal(0, frame.Values.Traction);
    }

    [Theory]
    [InlineData(25, 2f, 20f)]
    [InlineData(64, 1f, 20f)]
    [InlineData(64, 2f, 1f)]
    public void Assist_signals_require_input_slip_and_speed(byte input, float slip, float speed)
    {
        byte[] packet = Packet(311);
        packet[303] = input;
        packet[304] = input;
        WriteFloat(packet, 184, slip);
        WriteFloat(packet, 188, slip);
        WriteFloat(packet, 244, speed);
        Assert.True(ForzaPacket.TryParse(packet, out TelemetryFrame frame));
        Assert.Equal(0, frame.Values.Abs);
        Assert.Equal(0, frame.Values.Traction);
    }

    internal static byte[] Packet(int size = 232, int raceOn = 1, float rpm = 4000)
    {
        byte[] packet = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(packet, raceOn);
        WriteFloat(packet, 8, 7000);
        WriteFloat(packet, 12, 1000);
        WriteFloat(packet, 16, rpm);
        WriteFloat(packet, 32, 3);
        WriteFloat(packet, 36, 4);
        WriteFloat(packet, 40, 12);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(128), 1);
        WriteFloat(packet, 148, 0.25f);
        WriteFloat(packet, 152, -0.875f);
        WriteFloat(packet, 156, 0.5f);
        WriteFloat(packet, 160, 0.75f);
        WriteFloat(packet, 180, 0.5f);
        WriteFloat(packet, 184, -2);
        WriteFloat(packet, 188, -3);
        WriteFloat(packet, 192, 0.75f);
        if (size != 232)
        {
            int dash = size is 323 or 324 ? 244 : 232;
            WriteFloat(packet, dash + 12, 20);
            WriteFloat(packet, dash + 40, 12.5f);
            packet[dash + 71] = 128;
            packet[dash + 72] = 64;
            packet[dash + 73] = 32;
            packet[dash + 74] = 16;
            packet[dash + 75] = 4;
            packet[dash + 76] = unchecked((byte)-63);
        }

        return packet;
    }

    private static void WriteFloat(byte[] packet, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), value);
}
