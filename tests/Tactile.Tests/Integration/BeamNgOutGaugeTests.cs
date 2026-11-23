using System.Buffers.Binary;
using Tactile.Core.Integration;

namespace Tactile.Tests.Integration;

public sealed class BeamNgOutGaugeTests
{
    private static void Float(byte[] packet, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), value);

    internal static byte[] Packet(int size = 92, byte gear = 3, float speed = 20, float rpm = 3500, uint lights = 0,
        float gas = 0.5f, float brake = 0.25f, float clutch = 0.1f, float turbo = 0.8f)
    {
        byte[] packet = new byte[size];
        packet[10] = gear;
        Float(packet, 12, speed);
        Float(packet, 16, rpm);
        Float(packet, 20, turbo);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(44), lights);
        Float(packet, 48, gas);
        Float(packet, 52, brake);
        Float(packet, 56, clutch);
        return packet;
    }

    [Theory]
    [InlineData(92)]
    [InlineData(96)]
    public void Parses_standard_and_id_suffixed_packets(int size)
    {
        var gauge = new BeamNgOutGauge();
        Assert.True(gauge.TryParse(Packet(size), out TelemetryValues values));
        Assert.Equal(1, values.RaceOn);
        Assert.Equal(72, values.SpeedKmh, 4);
        Assert.Equal(2, values.Gear);
        Assert.Equal(0.5, values.Rpm, 5);
        Assert.Equal(0.5, values.RevLights, 5);
        Assert.Equal(0.5, values.Gas, 5);
        Assert.Equal(0.25, values.Brake, 5);
        Assert.Equal(0.1, values.Clutch, 5);
        Assert.Equal(0.8, values.Boost, 5);
        Assert.Equal(0, values.Handbrake);
        Assert.Equal(0, values.Drs);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 4)]
    public void Gear_zero_is_reverse_and_one_is_neutral(byte raw, int gear)
    {
        Assert.True(new BeamNgOutGauge().TryParse(Packet(gear: raw), out TelemetryValues values));
        Assert.Equal(gear, values.Gear);
    }

    [Fact]
    public void Dashboard_lights_map_to_shift_handbrake_traction_and_abs()
    {
        var gauge = new BeamNgOutGauge();
        gauge.TryParse(Packet(lights: 1u << 0), out TelemetryValues values);
        Assert.Equal(1, values.RevLights);
        Assert.Equal(0.5, values.Rpm, 5);
        gauge.TryParse(Packet(lights: 1u << 2), out values);
        Assert.Equal(1, values.Handbrake);
        gauge.TryParse(Packet(lights: 1u << 4), out values);
        Assert.Equal(1, values.Traction);
        Assert.Equal(1, values.SlipRear);
        Assert.Equal(0, values.Abs);
        gauge.TryParse(Packet(lights: 1u << 10), out values);
        Assert.Equal(1, values.Abs);
        Assert.Equal(1, values.SlipFront);
        Assert.Equal(0, values.Traction);
    }

    [Fact]
    public void Rpm_ceiling_adapts_upwards_and_resets_when_stopped()
    {
        var gauge = new BeamNgOutGauge();
        gauge.TryParse(Packet(rpm: 9000), out TelemetryValues values);
        Assert.Equal(1, values.Rpm);
        gauge.TryParse(Packet(rpm: 4500), out values);
        Assert.Equal(0.5, values.Rpm, 5);
        gauge.TryParse(Packet(rpm: 0, speed: 0, gas: 0, brake: 0), out values);
        Assert.Equal(0, values.RaceOn);
        Assert.Equal(0, values.Rpm);
        gauge.TryParse(Packet(rpm: 3500), out values);
        Assert.Equal(0.5, values.Rpm, 5);
        gauge.TryParse(Packet(rpm: 100000), out values);
        Assert.Equal(1, values.Rpm);
        gauge.TryParse(Packet(rpm: 10000), out values);
        Assert.Equal(0.5, values.Rpm, 5);
    }

    [Fact]
    public void Rejects_other_sizes_and_sanitizes_nonfinite_floats()
    {
        var gauge = new BeamNgOutGauge();
        foreach (int size in new[] { 0, 91, 93, 95, 97 })
        {
            Assert.False(gauge.TryParse(new byte[size], out _));
        }

        byte[] packet = Packet(speed: float.NaN, rpm: float.PositiveInfinity, gas: float.NaN, brake: float.NegativeInfinity,
            clutch: float.NaN, turbo: float.PositiveInfinity);
        Assert.True(gauge.TryParse(packet, out TelemetryValues values));
        Assert.Equal(0, values.RaceOn);
        Assert.Equal(0, values.SpeedKmh);
        Assert.Equal(0, values.Rpm);
        Assert.Equal(0, values.Gas);
        Assert.Equal(0, values.Brake);
        Assert.Equal(0, values.Clutch);
        Assert.Equal(0, values.Boost);
    }
}
