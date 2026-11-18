namespace Tactile.Core;

public static class PlayerLeds
{
    public const int MaxNumber = 4;

    /// <summary>
    /// Bit 0 is the leftmost of the five LEDs. Player numbers, as on the console:
    /// <code>
    /// 1  . . x . .
    /// 2  . x . x .
    /// 3  x . x . x
    /// 4  x x . x x
    /// </code>
    /// </summary>
    public static byte ForNumber(int number) => number switch
    {
        1 => 0b00100,
        2 => 0b01010,
        3 => 0b10101,
        4 => 0b11011,
        _ => 0,
    };

    public static byte Mask(PlayerLedConfig config) => config.Mode switch
    {
        PlayerLedMode.Number => ForNumber(config.Number),
        PlayerLedMode.Custom => (byte)(config.Mask & 0x1F),
        _ => 0,
    };
}
