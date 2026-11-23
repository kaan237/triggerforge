using System.Buffers.Binary;

namespace Tactile.Core.Integration;

public enum ForzaFormat { Sled, Fm7, Fm, Fh }

public sealed record TelemetryValues(
    int RaceOn, double SpeedKmh, double Rpm, double Gas, double Brake, double Clutch,
    double Handbrake, int Gear, double Steer, double SlipFront, double SlipRear,
    double Surface, int RumbleStrip, double Boost, int Abs, int Traction,
    double RevLights = 0, int Drs = 0)
{
    internal double Get(TelemetrySignal signal) => signal switch
    {
        TelemetrySignal.RaceOn => RaceOn,
        TelemetrySignal.SpeedKmh => SpeedKmh,
        TelemetrySignal.Rpm => Rpm,
        TelemetrySignal.Gas => Gas,
        TelemetrySignal.Brake => Brake,
        TelemetrySignal.Clutch => Clutch,
        TelemetrySignal.Handbrake => Handbrake,
        TelemetrySignal.Gear => Gear,
        TelemetrySignal.Steer => Steer,
        TelemetrySignal.SlipFront => SlipFront,
        TelemetrySignal.SlipRear => SlipRear,
        TelemetrySignal.Surface => Surface,
        TelemetrySignal.RumbleStrip => RumbleStrip,
        TelemetrySignal.Boost => Boost,
        TelemetrySignal.Abs => Abs,
        TelemetrySignal.Traction => Traction,
        TelemetrySignal.RevLights => RevLights,
        TelemetrySignal.Drs => Drs,
        _ => double.NaN,
    };
}

internal readonly record struct TelemetryFrame(ForzaFormat Format, TelemetryValues Values);

internal static class ForzaPacket
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out TelemetryFrame frame)
    {
        frame = default;
        ForzaFormat? format = packet.Length switch
        {
            232 => ForzaFormat.Sled,
            311 => ForzaFormat.Fm7,
            331 => ForzaFormat.Fm,
            323 or 324 => ForzaFormat.Fh,
            _ => null,
        };
        if (format is null)
        {
            return false;
        }

        double maxRpm = FloatAt(packet, 8);
        double idleRpm = FloatAt(packet, 12);
        double rpm = maxRpm > idleRpm ? Math.Clamp((FloatAt(packet, 16) - idleRpm) / (maxRpm - idleRpm), 0, 1) : 0;
        double slipFront = MaxAbsPair(packet, 180);
        double slipRear = MaxAbsPair(packet, 188);
        double surface = Math.Max(MaxAbsPair(packet, 148), MaxAbsPair(packet, 156));
        int rumbleStrip = 0;
        for (int offset = 116; offset <= 128; offset += 4)
        {
            if (IntAt(packet, offset) == 1)
            {
                rumbleStrip = 1;
                break;
            }
        }

        double speed;
        double gas = 0, brake = 0, clutch = 0, handbrake = 0, steer = 0, boost = 0;
        int gear = 0;
        if (format == ForzaFormat.Sled)
        {
            double x = FloatAt(packet, 32), y = FloatAt(packet, 36), z = FloatAt(packet, 40);
            speed = Math.Sqrt(x * x + y * y + z * z) * 3.6;
        }
        else
        {
            ReadOnlySpan<byte> dash = packet[(format == ForzaFormat.Fh ? 244 : 232)..];
            speed = FloatAt(dash, 12) * 3.6;
            boost = FloatAt(dash, 40);
            gas = dash[71] / 255.0;
            brake = dash[72] / 255.0;
            clutch = dash[73] / 255.0;
            handbrake = dash[74] / 255.0;
            gear = dash[75];
            steer = Math.Max(-1, unchecked((sbyte)dash[76]) / 127.0);
        }

        frame = new(format.Value, new TelemetryValues(
            IntAt(packet, 0) == 1 ? 1 : 0, speed, rpm, gas, brake, clutch, handbrake,
            gear, steer, slipFront, slipRear, surface, rumbleStrip, boost,
            brake > 0.1 && slipFront > 1 && speed > 5 ? 1 : 0,
            gas > 0.1 && slipRear > 1 && speed > 5 ? 1 : 0,
            rpm, 0));
        return true;
    }

    private static double MaxAbsPair(ReadOnlySpan<byte> packet, int offset) =>
        Math.Max(Math.Abs(FloatAt(packet, offset)), Math.Abs(FloatAt(packet, offset + 4)));

    private static int IntAt(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet[offset..]);

    private static double FloatAt(ReadOnlySpan<byte> packet, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(packet[offset..]);
        return float.IsFinite(value) ? value : 0;
    }
}
