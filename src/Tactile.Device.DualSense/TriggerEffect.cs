using Tactile.Core;

namespace Tactile.Device.DualSense;

/// <summary>
/// One trigger's 11-byte effect block. Travel is split into 10 zones (0 = top, 9 = bottom);
/// strengths and amplitudes are 1-8. Packing: docs/protocol.md.
/// </summary>
public readonly struct TriggerEffect : IEquatable<TriggerEffect>
{
    public const int Size = 11;
    public const int Zones = 10;

    private readonly byte[]? _bytes;

    private TriggerEffect(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes ?? OffBytes;

    public byte Mode => Bytes[0];

    private static readonly byte[] OffBytes = [0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static TriggerEffect Off => new(OffBytes);

    /// <summary>An effect block as written by someone else (a game), passed through unchanged.</summary>
    public static TriggerEffect FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(Text.Pick($"Expected {Size} bytes.", $"{Size} bayt gerekli."), nameof(bytes));
        }

        return new TriggerEffect(bytes.ToArray());
    }

    /// <summary>Constant resistance from <paramref name="startZone"/> to the bottom.</summary>
    public static TriggerEffect Feedback(int startZone, int strength)
    {
        Check(startZone, 0, 9, nameof(startZone));
        Check(strength, 0, 8, nameof(strength));
        if (strength == 0)
        {
            return Off;
        }

        var levels = new int[Zones];
        Array.Fill(levels, strength, startZone, Zones - startZone);
        return Zoned(0x21, levels);
    }

    /// <summary>Per-zone resistance; 0 leaves a zone free.</summary>
    public static TriggerEffect Feedback(ReadOnlySpan<int> strengthPerZone) => Zoned(0x21, strengthPerZone);

    /// <summary>Resistance that ramps linearly from start to end strength, then holds.</summary>
    public static TriggerEffect Slope(int startZone, int endZone, int startStrength, int endStrength)
    {
        Check(startZone, 0, 8, nameof(startZone));
        Check(endZone, startZone + 1, 9, nameof(endZone));
        Check(startStrength, 1, 8, nameof(startStrength));
        Check(endStrength, 1, 8, nameof(endStrength));

        var levels = new int[Zones];
        for (int z = startZone; z < Zones; z++)
        {
            float t = Math.Min(1f, (z - startZone) / (float)(endZone - startZone));
            levels[z] = (int)MathF.Round(startStrength + (endStrength - startStrength) * t);
        }

        return Zoned(0x21, levels);
    }

    /// <summary>Gun trigger: resists between the zones, then breaks free.</summary>
    public static TriggerEffect Weapon(int startZone, int endZone, int strength)
    {
        Check(startZone, 2, 7, nameof(startZone));
        Check(endZone, startZone + 1, 8, nameof(endZone));
        Check(strength, 0, 8, nameof(strength));
        if (strength == 0)
        {
            return Off;
        }

        byte[] b = Range(0x25, startZone, endZone);
        b[3] = (byte)(strength - 1);
        return new TriggerEffect(b);
    }

    /// <summary>Vibration from <paramref name="startZone"/> down; frequency in Hz.</summary>
    public static TriggerEffect Vibration(int startZone, int amplitude, int frequency)
    {
        Check(startZone, 0, 9, nameof(startZone));
        Check(amplitude, 0, 8, nameof(amplitude));
        Check(frequency, 0, 255, nameof(frequency));
        if (amplitude == 0 || frequency == 0)
        {
            return Off;
        }

        var levels = new int[Zones];
        Array.Fill(levels, amplitude, startZone, Zones - startZone);
        return Vibration(levels, frequency);
    }

    public static TriggerEffect Vibration(ReadOnlySpan<int> amplitudePerZone, int frequency)
    {
        Check(frequency, 0, 255, nameof(frequency));
        if (frequency == 0)
        {
            return Off;
        }

        TriggerEffect e = Zoned(0x26, amplitudePerZone);
        if (e.Mode == Off.Mode)
        {
            return e;
        }

        e._bytes![9] = (byte)frequency;
        return e;
    }

    /// <summary>Bow string: resistance, then a snap back. Not part of Sony's SDK; may change with firmware.</summary>
    public static TriggerEffect Bow(int startZone, int endZone, int strength, int snapForce)
    {
        Check(startZone, 0, 8, nameof(startZone));
        Check(endZone, startZone + 1, 8, nameof(endZone));
        Check(strength, 0, 8, nameof(strength));
        Check(snapForce, 0, 8, nameof(snapForce));
        if (strength == 0 || snapForce == 0)
        {
            return Off;
        }

        byte[] b = Range(0x22, startZone, endZone);
        int pair = (strength - 1) | ((snapForce - 1) << 3);
        b[3] = (byte)pair;
        b[4] = (byte)(pair >> 8);
        return new TriggerEffect(b);
    }

    /// <summary>Hoof-beat rhythm. Feet are 0-7 timings within a cycle. Unofficial.</summary>
    public static TriggerEffect Galloping(int startZone, int endZone, int firstFoot, int secondFoot, int frequency)
    {
        Check(startZone, 0, 8, nameof(startZone));
        Check(endZone, startZone + 1, 9, nameof(endZone));
        Check(firstFoot, 0, 6, nameof(firstFoot));
        Check(secondFoot, firstFoot + 1, 7, nameof(secondFoot));
        Check(frequency, 0, 255, nameof(frequency));
        if (frequency == 0)
        {
            return Off;
        }

        byte[] b = Range(0x23, startZone, endZone);
        b[3] = (byte)(secondFoot | (firstFoot << 3));
        b[4] = (byte)frequency;
        return new TriggerEffect(b);
    }

    /// <summary>Vibration alternating between two amplitudes (0-7). Unofficial.</summary>
    public static TriggerEffect Machine(int startZone, int endZone, int amplitudeA, int amplitudeB, int frequency, int period)
    {
        Check(startZone, 0, 8, nameof(startZone));
        Check(endZone, startZone + 1, 9, nameof(endZone));
        Check(amplitudeA, 0, 7, nameof(amplitudeA));
        Check(amplitudeB, 0, 7, nameof(amplitudeB));
        Check(frequency, 0, 255, nameof(frequency));
        Check(period, 0, 255, nameof(period));
        if (frequency == 0)
        {
            return Off;
        }

        byte[] b = Range(0x27, startZone, endZone);
        b[3] = (byte)(amplitudeA | (amplitudeB << 3));
        b[4] = (byte)frequency;
        b[5] = (byte)period;
        return new TriggerEffect(b);
    }

    public bool Equals(TriggerEffect other) => Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is TriggerEffect other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public static bool operator ==(TriggerEffect a, TriggerEffect b) => a.Equals(b);

    public static bool operator !=(TriggerEffect a, TriggerEffect b) => !a.Equals(b);

    public override string ToString() => Convert.ToHexString(Bytes);

    // 10-bit zone mask in bytes 1-2, 3-bit level per zone packed into bytes 3-6.
    private static TriggerEffect Zoned(byte mode, ReadOnlySpan<int> levels)
    {
        if (levels.Length != Zones)
        {
            throw new ArgumentException(Text.Pick($"Expected {Zones} zones.", $"{Zones} bölge gerekli."), nameof(levels));
        }

        int mask = 0;
        uint packed = 0;
        for (int z = 0; z < Zones; z++)
        {
            Check(levels[z], 0, 8, nameof(levels));
            if (levels[z] == 0)
            {
                continue;
            }

            mask |= 1 << z;
            packed |= (uint)(levels[z] - 1) << (3 * z);
        }

        if (mask == 0)
        {
            return Off;
        }

        byte[] b = new byte[Size];
        b[0] = mode;
        b[1] = (byte)mask;
        b[2] = (byte)(mask >> 8);
        b[3] = (byte)packed;
        b[4] = (byte)(packed >> 8);
        b[5] = (byte)(packed >> 16);
        b[6] = (byte)(packed >> 24);
        return new TriggerEffect(b);
    }

    private static byte[] Range(byte mode, int startZone, int endZone)
    {
        int mask = (1 << startZone) | (1 << endZone);
        byte[] b = new byte[Size];
        b[0] = mode;
        b[1] = (byte)mask;
        b[2] = (byte)(mask >> 8);
        return b;
    }

    private static void Check(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, Text.Pick($"{name} must be between {min} and {max}.", $"{name} {min}-{max} arası olmalı."));
        }
    }
}
