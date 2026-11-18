namespace Tactile.Device.DualSense;

[Flags]
public enum Buttons : uint
{
    None = 0,
    Square = 1 << 0,
    Cross = 1 << 1,
    Circle = 1 << 2,
    Triangle = 1 << 3,
    L1 = 1 << 4,
    R1 = 1 << 5,
    L2 = 1 << 6,
    R2 = 1 << 7,
    Create = 1 << 8,
    Options = 1 << 9,
    L3 = 1 << 10,
    R3 = 1 << 11,
    PS = 1 << 12,
    Touchpad = 1 << 13,
    Mic = 1 << 14,
    Up = 1 << 15,
    Right = 1 << 16,
    Down = 1 << 17,
    Left = 1 << 18,
    // DualSense Edge
    Fn1 = 1 << 19,
    Fn2 = 1 << 20,
    PaddleLeft = 1 << 21,
    PaddleRight = 1 << 22,
}

public readonly record struct Stick(byte X, byte Y)
{
    /// <summary>-1..1, Y up positive.</summary>
    public (float X, float Y) Normalized => ((X - 127.5f) / 127.5f, (127.5f - Y) / 127.5f);
}

public readonly record struct Vector3(float X, float Y, float Z);

public readonly record struct TouchPoint(bool Active, byte Id, ushort X, ushort Y);

public enum ChargeState : byte { Discharging = 0, Charging = 1, Full = 2, Error = 0xF }

public readonly record struct Battery(int Percent, ChargeState Charge);

public sealed record InputState(
    Buttons Buttons,
    Stick Left,
    Stick Right,
    byte L2,
    byte R2,
    Vector3 GyroRaw,
    Vector3 AccelRaw,
    uint SensorTimestamp,
    TouchPoint Touch1,
    TouchPoint Touch2,
    Battery Battery,
    bool HeadphoneConnected,
    bool MicConnected,
    bool MicMuted)
{
    public bool IsDown(Buttons b) => (Buttons & b) == b;
}
