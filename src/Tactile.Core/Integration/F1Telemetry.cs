using System.Buffers.Binary;

namespace Tactile.Core.Integration;

internal enum F1PacketResult { Ignored, Updated, Rejected, Unsupported }

/// <summary>Merges the player's car data from the separate F1 UDP packets into one signal frame.</summary>
internal sealed class F1TelemetrySession
{
    private const int HeaderSize = 29;
    private const int TelemetryCarSize = 60;
    private const int StatusCarSize = 55;
    private const int NoPlayer = 255;

    private ulong? _sessionUid;
    private double _maxRpm;
    private double _idleRpm;
    private double _boost;
    private double _slipFront;
    private double _slipRear;
    private double _gSurface;

    public int? Format { get; private set; }

    public F1PacketResult TryParse(ReadOnlySpan<byte> packet, out TelemetryValues? values)
    {
        values = null;
        if (packet.Length < HeaderSize)
        {
            return F1PacketResult.Rejected;
        }

        int format = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if (format is not (2024 or 2025 or 2026))
        {
            return F1PacketResult.Unsupported;
        }

        int id = packet[6];
        if (packet[5] == 0 || id > 15)
        {
            return F1PacketResult.Rejected;
        }

        int player = packet[27];
        int maxCars = format == 2026 ? 24 : 22;
        if (player != NoPlayer && player >= maxCars)
        {
            return F1PacketResult.Rejected;
        }

        // A known packet with the wrong size is rejected before it can touch the session, spectator packets included.
        int expected = ExpectedLength(id, format, maxCars);
        if (expected >= 0 && packet.Length != expected)
        {
            return F1PacketResult.Rejected;
        }

        ulong uid = BinaryPrimitives.ReadUInt64LittleEndian(packet[7..]);
        if (_sessionUid != uid)
        {
            _sessionUid = uid;
            ResetCache();
        }

        Format = format;
        if (player == NoPlayer)
        {
            ResetCache();
            values = new TelemetryValues(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return F1PacketResult.Updated;
        }

        switch (id)
        {
            case 0:
                return ReadMotion(packet, format, player);
            case 6:
                values = ReadTelemetry(packet, HeaderSize + player * TelemetryCarSize);
                return F1PacketResult.Updated;
            case 7:
                int status = HeaderSize + player * StatusCarSize;
                _maxRpm = BinaryPrimitives.ReadUInt16LittleEndian(packet[(status + 17)..]);
                _idleRpm = BinaryPrimitives.ReadUInt16LittleEndian(packet[(status + 19)..]);
                _boost = Math.Clamp(FloatAt(packet, status + 37) / 4_000_000.0, 0, 1);
                return F1PacketResult.Ignored;
            case 13:
                // Wheel order in the game is rear left, rear right, front left, front right.
                _slipRear = Math.Max(Math.Abs(FloatAt(packet, 93)), Math.Abs(FloatAt(packet, 97)));
                _slipFront = Math.Max(Math.Abs(FloatAt(packet, 101)), Math.Abs(FloatAt(packet, 105)));
                return F1PacketResult.Ignored;
            default:
                return F1PacketResult.Ignored;
        }
    }

    private static int ExpectedLength(int id, int format, int maxCars) => id switch
    {
        0 => format == 2026 ? 1325 : 1349,
        6 => HeaderSize + maxCars * TelemetryCarSize + 3,
        7 => HeaderSize + maxCars * StatusCarSize,
        13 => format == 2026 ? 273 : 237,
        _ => -1,
    };

    private F1PacketResult ReadMotion(ReadOnlySpan<byte> packet, int format, int player)
    {
        if (format != 2026)
        {
            _gSurface = Math.Clamp(Math.Abs(FloatAt(packet, HeaderSize + player * 60 + 44)) / 3.0, 0, 1);
        }

        return F1PacketResult.Ignored;
    }

    private TelemetryValues ReadTelemetry(ReadOnlySpan<byte> packet, int car)
    {
        double speed = BinaryPrimitives.ReadUInt16LittleEndian(packet[car..]);
        double gas = Math.Clamp(FloatAt(packet, car + 2), 0, 1);
        double steer = Math.Clamp(FloatAt(packet, car + 6), -1, 1);
        double brake = Math.Clamp(FloatAt(packet, car + 10), 0, 1);
        double clutch = Math.Clamp(packet[car + 14] / 100.0, 0, 1);
        int gear = Math.Max(0, (int)unchecked((sbyte)packet[car + 15]));
        int engineRpm = BinaryPrimitives.ReadUInt16LittleEndian(packet[(car + 16)..]);
        int drs = packet[car + 18] != 0 ? 1 : 0;
        double revLights = Math.Clamp(packet[car + 19] / 100.0, 0, 1);

        bool rumble = false, rough = false;
        for (int wheel = 0; wheel < 4; wheel++)
        {
            byte surfaceType = packet[car + 56 + wheel];
            rumble |= surfaceType == 1;
            rough |= surfaceType > 1;
        }

        double rpm;
        if (_maxRpm > _idleRpm && _maxRpm > 0)
        {
            rpm = Math.Clamp((engineRpm - _idleRpm) / (_maxRpm - _idleRpm), 0, 1);
        }
        else if (revLights > 0)
        {
            rpm = revLights;
        }
        else
        {
            rpm = engineRpm > 0 ? Math.Clamp((engineRpm - 3500.0) / 10000.0, 0, 1) : 0;
        }

        double surface = rough ? Math.Max(_gSurface, 0.35) : _gSurface;
        return new TelemetryValues(
            engineRpm > 0 || speed > 0 || gas > 0 || brake > 0 ? 1 : 0,
            speed, rpm, gas, brake, clutch, 0, gear, steer, _slipFront, _slipRear, surface,
            rumble ? 1 : 0, _boost,
            brake > 0.1 && _slipFront > 0.15 && speed > 5 ? 1 : 0,
            gas > 0.1 && _slipRear > 0.15 && speed > 5 ? 1 : 0,
            revLights, drs);
    }

    private void ResetCache()
    {
        _maxRpm = 0;
        _idleRpm = 0;
        _boost = 0;
        _slipFront = 0;
        _slipRear = 0;
        _gSurface = 0;
    }

    private static double FloatAt(ReadOnlySpan<byte> packet, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(packet[offset..]);
        return float.IsFinite(value) ? value : 0;
    }
}
