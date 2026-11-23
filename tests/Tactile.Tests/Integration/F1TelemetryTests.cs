using System.Buffers.Binary;
using Tactile.Core.Integration;

namespace Tactile.Tests.Integration;

public sealed class F1TelemetryTests
{
    private static int MaxCars(int format) => format == 2026 ? 24 : 22;

    internal static byte[] Packet(int format, int id, int length, int player, ulong uid = 1, byte version = 1)
    {
        byte[] packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)format);
        packet[5] = version;
        packet[6] = (byte)id;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), uid);
        packet[27] = (byte)player;
        return packet;
    }

    private static void Float(byte[] packet, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), value);

    private static void Short(byte[] packet, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset), (ushort)value);

    internal static byte[] CarTelemetry(int format, int player, ulong uid = 1, int rpm = 10000, int speed = 200,
        float gas = 0.8f, float brake = 0.6f, byte revLights = 50)
    {
        byte[] packet = Packet(format, 6, 29 + MaxCars(format) * 60 + 3, player, uid);
        int car = 29 + player * 60;
        Short(packet, car, speed);
        Float(packet, car + 2, gas);
        Float(packet, car + 6, -0.5f);
        Float(packet, car + 10, brake);
        packet[car + 14] = 20;
        packet[car + 15] = 4;
        Short(packet, car + 16, rpm);
        packet[car + 18] = 1;
        packet[car + 19] = revLights;
        packet[car + 57] = 1;
        return packet;
    }

    private static byte[] CarStatus(int format, int player, ulong uid = 1)
    {
        byte[] packet = Packet(format, 7, 29 + MaxCars(format) * 55, player, uid);
        int car = 29 + player * 55;
        Short(packet, car + 17, 12000);
        Short(packet, car + 19, 4000);
        Float(packet, car + 37, 2_000_000f);
        return packet;
    }

    private static byte[] MotionEx(int format, int player, ulong uid = 1)
    {
        byte[] packet = Packet(format, 13, format == 2026 ? 273 : 237, player, uid);
        Float(packet, 93, -0.4f);
        Float(packet, 97, 0.1f);
        Float(packet, 101, 0.05f);
        Float(packet, 105, 0.3f);
        return packet;
    }

    [Theory]
    [InlineData(2024, 3)]
    [InlineData(2025, 21)]
    [InlineData(2026, 23)]
    public void Parses_f1_formats_and_merges_status_and_motion_ex(int format, int player)
    {
        var session = new F1TelemetrySession();
        Assert.Equal(F1PacketResult.Updated, session.TryParse(CarTelemetry(format, player), out TelemetryValues? first));
        Assert.Equal(format, session.Format);
        Assert.Equal(1, first!.RaceOn);
        Assert.Equal(200, first.SpeedKmh);
        Assert.Equal(0.8, first.Gas, 5);
        Assert.Equal(0.6, first.Brake, 5);
        Assert.Equal(-0.5, first.Steer, 5);
        Assert.Equal(0.2, first.Clutch, 5);
        Assert.Equal(4, first.Gear);
        Assert.Equal(1, first.Drs);
        Assert.Equal(0.5, first.RevLights, 5);
        Assert.Equal(0.5, first.Rpm, 5);
        Assert.Equal(1, first.RumbleStrip);
        Assert.Equal(0, first.Abs);
        Assert.Equal(0, first.Traction);

        Assert.Equal(F1PacketResult.Ignored, session.TryParse(CarStatus(format, player), out TelemetryValues? none));
        Assert.Null(none);
        Assert.Equal(F1PacketResult.Ignored, session.TryParse(MotionEx(format, player), out none));
        Assert.Equal(F1PacketResult.Updated, session.TryParse(CarTelemetry(format, player), out TelemetryValues? merged));
        Assert.Equal(0.75, merged!.Rpm, 5);
        Assert.Equal(0.5, merged.Boost, 5);
        Assert.Equal(0.4, merged.SlipRear, 5);
        Assert.Equal(0.3, merged.SlipFront, 5);
        Assert.Equal(1, merged.Abs);
        Assert.Equal(1, merged.Traction);
    }

    [Fact]
    public void Known_packets_with_a_wrong_length_never_touch_the_session_or_end_it()
    {
        var session = new F1TelemetrySession();
        Assert.Equal(F1PacketResult.Ignored, session.TryParse(CarStatus(2025, 3), out _));

        foreach (int id in new[] { 0, 6, 7, 13 })
        {
            Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, id, 29, 0, uid: 9), out _));
            Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2025, id, 29, 255, uid: 9), out TelemetryValues? spectator));
            Assert.Null(spectator);
        }

        Assert.Equal(2025, session.Format);
        Assert.Equal(F1PacketResult.Updated, session.TryParse(CarTelemetry(2025, 3), out TelemetryValues? values));
        Assert.Equal(0.75, values!.Rpm, 5);
        Assert.Equal(0.5, values.Boost, 5);

        // A spectator packet of the right size still ends the session.
        Assert.Equal(F1PacketResult.Updated, session.TryParse(Packet(2025, 6, 29 + 22 * 60 + 3, 255), out values));
        Assert.Equal(0, values!.RaceOn);
    }

    [Fact]
    public void Rpm_falls_back_to_the_raw_engine_value_without_rev_lights_or_status()
    {
        var session = new F1TelemetrySession();
        session.TryParse(CarTelemetry(2024, 0, rpm: 8500, revLights: 0), out TelemetryValues? values);
        Assert.Equal(0.5, values!.Rpm, 5);
        session.TryParse(CarTelemetry(2024, 0, rpm: 0, speed: 0, gas: 0, brake: 0, revLights: 0), out values);
        Assert.Equal(0, values!.Rpm);
        Assert.Equal(0, values.RaceOn);
    }

    [Fact]
    public void Surface_uses_vertical_g_and_a_floor_for_rough_ground()
    {
        var session = new F1TelemetrySession();
        byte[] motion = Packet(2024, 0, 1349, 2);
        Float(motion, 29 + 2 * 60 + 44, -1.5f);
        Assert.Equal(F1PacketResult.Ignored, session.TryParse(motion, out _));
        session.TryParse(CarTelemetry(2024, 2), out TelemetryValues? values);
        Assert.Equal(0.5, values!.Surface, 5);

        byte[] rough = CarTelemetry(2024, 2);
        rough[29 + 2 * 60 + 58] = 4;
        session = new F1TelemetrySession();
        session.TryParse(rough, out values);
        Assert.Equal(0.35, values!.Surface, 5);
    }

    [Fact]
    public void Counts_unknown_formats_as_unsupported_and_rejects_bad_sizes_or_car_indices()
    {
        var session = new F1TelemetrySession();
        foreach (int format in new[] { 2023, 2027 })
        {
            Assert.Equal(F1PacketResult.Unsupported, session.TryParse(Packet(format, 6, 1352, 0), out _));
        }

        Assert.Equal(F1PacketResult.Rejected, session.TryParse(new byte[28], out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(CarTelemetry(2024, 0)[..1351], out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 6, 1472, 0), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 7, 1238, 0), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 13, 236, 0), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2026, 13, 237, 0), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 0, 1325, 0), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 6, 1352, 22), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2026, 6, 1472, 24), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 6, 1352, 0, version: 0), out _));
        Assert.Equal(F1PacketResult.Rejected, session.TryParse(Packet(2024, 16, 1352, 0), out _));
        Assert.Equal(F1PacketResult.Updated, session.TryParse(CarTelemetry(2026, 23), out _));
        Assert.Equal(F1PacketResult.Ignored, session.TryParse(Packet(2025, 3, 100, 0), out TelemetryValues? values));
        Assert.Null(values);
    }

    [Fact]
    public void Spectator_emits_an_idle_frame_and_a_new_session_clears_cached_values()
    {
        var session = new F1TelemetrySession();
        session.TryParse(CarStatus(2025, 1), out _);
        session.TryParse(MotionEx(2025, 1), out _);
        session.TryParse(CarTelemetry(2025, 1, rpm: 8000, revLights: 0), out TelemetryValues? values);
        Assert.Equal(0.5, values!.Rpm, 5);
        Assert.Equal(0.4, values.SlipRear, 5);

        session.TryParse(CarTelemetry(2025, 1, uid: 2, rpm: 8000, revLights: 0), out values);
        Assert.Equal(0.45, values!.Rpm, 5);
        Assert.Equal(0, values.SlipRear);
        Assert.Equal(0, values.Boost);

        Assert.Equal(F1PacketResult.Updated, session.TryParse(Packet(2025, 6, 1352, 255, uid: 2), out values));
        Assert.Equal(0, values!.RaceOn);
        Assert.Equal(0, values.SpeedKmh);
    }

    [Fact]
    public void Non_finite_floats_become_zero()
    {
        var session = new F1TelemetrySession();
        byte[] packet = CarTelemetry(2024, 0, gas: float.NaN, brake: float.PositiveInfinity);
        Float(packet, 29 + 6, float.NegativeInfinity);
        session.TryParse(packet, out TelemetryValues? values);
        Assert.Equal(0, values!.Gas);
        Assert.Equal(0, values.Brake);
        Assert.Equal(0, values.Steer);
    }
}
