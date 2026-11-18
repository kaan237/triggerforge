namespace Tactile.Device.DualSense;

public enum MicLed : byte { Off = 0, On = 1, Pulse = 2 }

public enum LedBrightness : byte { High = 0, Medium = 1, Low = 2 }

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Everything the app wants the controller to show. Immutable; build with <c>with</c>.</summary>
public sealed record OutputState
{
    public Rgb Lightbar { get; init; } = new(0, 0, 64);
    public LedBrightness Brightness { get; init; } = LedBrightness.High;
    /// <summary>Five player LEDs, bit 0 = leftmost.</summary>
    public byte PlayerLeds { get; init; }
    public MicLed MicLed { get; init; } = MicLed.Off;
    /// <summary>Low-frequency (left, heavy) motor 0-255.</summary>
    public byte RumbleLeft { get; init; }
    /// <summary>High-frequency (right, light) motor 0-255.</summary>
    public byte RumbleRight { get; init; }
    public TriggerEffect LeftTrigger { get; init; } = TriggerEffect.Off;
    public TriggerEffect RightTrigger { get; init; } = TriggerEffect.Off;
    /// <summary>0-255.</summary>
    public byte SpeakerVolume { get; init; } = 0x7F;

    public static OutputState Default { get; } = new();

    public bool Equals(OutputState? other) =>
        other is not null &&
        Lightbar == other.Lightbar && Brightness == other.Brightness && PlayerLeds == other.PlayerLeds &&
        MicLed == other.MicLed && RumbleLeft == other.RumbleLeft && RumbleRight == other.RumbleRight &&
        LeftTrigger == other.LeftTrigger && RightTrigger == other.RightTrigger && SpeakerVolume == other.SpeakerVolume;

    public override int GetHashCode() => HashCode.Combine(Lightbar, PlayerLeds, RumbleLeft, RumbleRight, LeftTrigger, RightTrigger, MicLed);
}
