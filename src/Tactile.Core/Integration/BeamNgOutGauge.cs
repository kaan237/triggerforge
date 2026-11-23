using System.Buffers.Binary;

namespace Tactile.Core.Integration;

/// <summary>Reads OutGauge datagrams (92 bytes, or 96 with the trailing id).</summary>
internal sealed class BeamNgOutGauge
{
    private const double DefaultMaxRpm = 7000;
    private const uint ShiftLight = 1u << 0;
    private const uint HandbrakeLight = 1u << 2;
    private const uint TractionLight = 1u << 4;
    private const uint AbsLight = 1u << 10;

    private double _maxRpm = DefaultMaxRpm;

    public bool TryParse(ReadOnlySpan<byte> packet, out TelemetryValues values)
    {
        values = null!;
        if (packet.Length != 92 && packet.Length != 96)
        {
            return false;
        }

        double speed = Math.Max(0, FloatAt(packet, 12) * 3.6);
        double rawRpm = Math.Max(0, FloatAt(packet, 16));
        _maxRpm = rawRpm == 0 && speed == 0
            ? DefaultMaxRpm
            : Math.Clamp(Math.Max(_maxRpm, rawRpm), 4000.0, 20000.0);
        double rpm = Math.Clamp(rawRpm / _maxRpm, 0, 1);
        uint lights = BinaryPrimitives.ReadUInt32LittleEndian(packet[44..]);
        double gas = Math.Clamp(FloatAt(packet, 48), 0, 1);
        double brake = Math.Clamp(FloatAt(packet, 52), 0, 1);
        int abs = (lights & AbsLight) != 0 ? 1 : 0;
        int traction = (lights & TractionLight) != 0 ? 1 : 0;
        values = new TelemetryValues(
            rawRpm > 50 || speed > 0.5 || gas > 0.01 || brake > 0.01 ? 1 : 0,
            speed, rpm, gas, brake, Math.Clamp(FloatAt(packet, 56), 0, 1),
            (lights & HandbrakeLight) != 0 ? 1.0 : 0.0, Math.Max(0, packet[10] - 1), 0, abs, traction,
            0, 0, FloatAt(packet, 20), abs, traction,
            (lights & ShiftLight) != 0 ? 1.0 : rpm, 0);
        return true;
    }

    private static double FloatAt(ReadOnlySpan<byte> packet, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(packet[offset..]);
        return float.IsFinite(value) ? value : 0;
    }
}
