using System.Buffers.Binary;
using System.Text;

namespace Tactile.Device.DualSense;

/// <summary>IMU calibration from feature report 0x05. Converts raw readings to deg/s and g.</summary>
public sealed class ImuCalibration
{
    private const float GyroResolution = 1024f;   // counts per deg/s after calibration
    private const float AccelResolution = 8192f;  // counts per g after calibration

    private readonly (float Bias, float Scale)[] _gyro = new (float, float)[3];
    private readonly (float Bias, float Scale)[] _accel = new (float, float)[3];

    public static ImuCalibration Identity { get; } = new();

    private ImuCalibration()
    {
        for (int i = 0; i < 3; i++)
        {
            _gyro[i] = (0, 1f / GyroResolution);
            _accel[i] = (0, 1f / AccelResolution);
        }
    }

    /// <param name="feature">Report starting with the id byte 0x05.</param>
    public static ImuCalibration Parse(ReadOnlySpan<byte> feature)
    {
        if (feature.Length < 35 || feature[0] != 0x05)
        {
            return Identity;
        }

        short[] v = new short[17];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = BinaryPrimitives.ReadInt16LittleEndian(feature[(1 + i * 2)..]);
        }

        short R(int offset) => v[(offset - 1) / 2];
        var c = new ImuCalibration();

        // Firmware applies the gyro bias itself; the bias field only centres the range.
        short[] bias = [R(1), R(3), R(5)];
        (short Plus, short Minus)[] gyroRange = [(R(7), R(9)), (R(11), R(13)), (R(15), R(17))];
        int speed2x = R(19) + R(21);
        for (int i = 0; i < 3; i++)
        {
            int span = Math.Abs(gyroRange[i].Plus - bias[i]) + Math.Abs(gyroRange[i].Minus - bias[i]);
            c._gyro[i] = span == 0 ? (0, 1f / GyroResolution) : (0, speed2x / (float)span);
        }

        (short Plus, short Minus)[] accelRange = [(R(23), R(25)), (R(27), R(29)), (R(31), R(33))];
        for (int i = 0; i < 3; i++)
        {
            int range = accelRange[i].Plus - accelRange[i].Minus;
            float center = accelRange[i].Plus - range / 2f;
            c._accel[i] = range == 0
                ? (0, 1f / AccelResolution)
                : (center, 2f / range);
        }

        return c;
    }

    /// <summary>True when the calibration payload is all zero, as a dongle without a paired controller answers.</summary>
    public static bool IsBlank(ReadOnlySpan<byte> feature)
    {
        if (feature.Length < 35)
        {
            return true;
        }

        foreach (byte b in feature[1..35])
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// DualShock 4 calibration: feature 0x02 (USB, 37 bytes, no CRC) or 0x05 (Bluetooth, 41 bytes, CRC).
    /// <paramref name="grouped"/> selects the gyro range layout: pitch+, yaw+, roll+, pitch-, yaw-, roll-
    /// (Bluetooth and the wireless adaptor) instead of pitch+, pitch-, yaw+, yaw-, roll+, roll-.
    /// Bad input gives <see cref="Identity"/>; an implausible axis falls back to identity values alone.
    /// </summary>
    public static ImuCalibration ParseDualShock4(ReadOnlySpan<byte> feature, bool grouped)
    {
        if (feature.IsEmpty)
        {
            return Identity;
        }

        if (feature[0] == 0x02)
        {
            if (feature.Length < 37)
            {
                return Identity;
            }
        }
        else if (feature[0] == 0x05)
        {
            if (feature.Length < 41 || !Crc32.Verify(Crc32.FeatureSeed, feature[..41]))
            {
                return Identity;
            }
        }
        else
        {
            return Identity;
        }

        byte[] data = feature.ToArray();
        short R(int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));
        var c = new ImuCalibration();

        (int Plus, int Minus)[] gyroRange = grouped
            ? [(R(7), R(13)), (R(9), R(15)), (R(11), R(17))]
            : [(R(7), R(9)), (R(11), R(13)), (R(15), R(17))];
        int speed2x = R(19) + R(21);
        for (int i = 0; i < 3; i++)
        {
            int bias = R(1 + i * 2);
            int span = Math.Abs(gyroRange[i].Plus - gyroRange[i].Minus);
            if (span == 0 || speed2x <= 0 || Math.Abs(bias) > 1024)
            {
                continue;
            }

            float scale = speed2x / (float)span;
            if (!Plausible(scale, Ds4GyroNominal))
            {
                continue;
            }

            c._gyro[i] = (bias, scale);
        }

        (int Plus, int Minus)[] accelRange = [(R(23), R(25)), (R(27), R(29)), (R(31), R(33))];
        for (int i = 0; i < 3; i++)
        {
            int range = accelRange[i].Plus - accelRange[i].Minus;
            if (range == 0)
            {
                continue;
            }

            float center = accelRange[i].Plus - range / 2f;
            float scale = 2f / range;
            if (Math.Abs(center) > 1024 || !Plausible(scale, 1f / AccelResolution))
            {
                continue;
            }

            c._accel[i] = (center, scale);
        }

        return c;
    }

    private const float Ds4GyroNominal = 1f / 16f;   // deg/s per raw count

    private static bool Plausible(float scale, float nominal) =>
        scale > 0 && Math.Abs(scale - nominal) <= nominal * 0.5f;

    public Vector3 GyroDegPerSec(Vector3 raw) => new(
        (raw.X - _gyro[0].Bias) * _gyro[0].Scale,
        (raw.Y - _gyro[1].Bias) * _gyro[1].Scale,
        (raw.Z - _gyro[2].Bias) * _gyro[2].Scale);

    public Vector3 AccelG(Vector3 raw) => new(
        (raw.X - _accel[0].Bias) * _accel[0].Scale,
        (raw.Y - _accel[1].Bias) * _accel[1].Scale,
        (raw.Z - _accel[2].Bias) * _accel[2].Scale);
}

/// <summary>Feature report 0x20.</summary>
public sealed record FirmwareInfo(string BuildDate, string BuildTime, uint HardwareVersion, uint FirmwareVersion, ushort UpdateVersion)
{
    public static FirmwareInfo? Parse(ReadOnlySpan<byte> feature)
    {
        if (feature.Length < 48 || feature[0] != 0x20)
        {
            return null;
        }

        string date = Encoding.ASCII.GetString(feature.Slice(1, 11));
        string time = Encoding.ASCII.GetString(feature.Slice(12, 8));
        return new FirmwareInfo(date, time,
            BinaryPrimitives.ReadUInt32LittleEndian(feature[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(feature[28..]),
            BinaryPrimitives.ReadUInt16LittleEndian(feature[44..]));
    }

    public string Display => $"{BuildDate} {BuildTime}";
}
