namespace Tactile.Input;

/// <summary>
/// A physical key, by its position on a US layout. The value is the set 1 scan code
/// (0xE0xx for extended keys), so a binding presses the same physical key whatever the
/// keyboard layout. JSON names are the camelCase member names ("a", "digit1", "leftCtrl").
/// </summary>
public enum Key : ushort
{
    None = 0,

    Escape = 0x01,
    Digit1 = 0x02, Digit2 = 0x03, Digit3 = 0x04, Digit4 = 0x05, Digit5 = 0x06,
    Digit6 = 0x07, Digit7 = 0x08, Digit8 = 0x09, Digit9 = 0x0A, Digit0 = 0x0B,
    Minus = 0x0C,
    Equals = 0x0D,
    Backspace = 0x0E,
    Tab = 0x0F,
    Q = 0x10, W = 0x11, E = 0x12, R = 0x13, T = 0x14, Y = 0x15, U = 0x16, I = 0x17, O = 0x18, P = 0x19,
    LeftBracket = 0x1A,
    RightBracket = 0x1B,
    Enter = 0x1C,
    LeftCtrl = 0x1D,
    A = 0x1E, S = 0x1F, D = 0x20, F = 0x21, G = 0x22, H = 0x23, J = 0x24, K = 0x25, L = 0x26,
    Semicolon = 0x27,
    Quote = 0x28,
    Backquote = 0x29,
    LeftShift = 0x2A,
    Backslash = 0x2B,
    Z = 0x2C, X = 0x2D, C = 0x2E, V = 0x2F, B = 0x30, N = 0x31, M = 0x32,
    Comma = 0x33,
    Period = 0x34,
    Slash = 0x35,
    RightShift = 0x36,
    NumpadMultiply = 0x37,
    LeftAlt = 0x38,
    Space = 0x39,
    CapsLock = 0x3A,
    F1 = 0x3B, F2 = 0x3C, F3 = 0x3D, F4 = 0x3E, F5 = 0x3F, F6 = 0x40, F7 = 0x41, F8 = 0x42, F9 = 0x43, F10 = 0x44,
    NumLock = 0x45,
    ScrollLock = 0x46,
    Numpad7 = 0x47, Numpad8 = 0x48, Numpad9 = 0x49,
    NumpadSubtract = 0x4A,
    Numpad4 = 0x4B, Numpad5 = 0x4C, Numpad6 = 0x4D,
    NumpadAdd = 0x4E,
    Numpad1 = 0x4F, Numpad2 = 0x50, Numpad3 = 0x51, Numpad0 = 0x52,
    NumpadDecimal = 0x53,
    /// <summary>The extra key next to left shift on ISO keyboards (Turkish "&lt;").</summary>
    IntlBackslash = 0x56,
    F11 = 0x57, F12 = 0x58,
    F13 = 0x64, F14 = 0x65, F15 = 0x66, F16 = 0x67, F17 = 0x68, F18 = 0x69,
    F19 = 0x6A, F20 = 0x6B, F21 = 0x6C, F22 = 0x6D, F23 = 0x6E, F24 = 0x76,

    NumpadEnter = 0xE01C,
    RightCtrl = 0xE01D,
    NumpadDivide = 0xE035,
    PrintScreen = 0xE037,
    RightAlt = 0xE038,
    Home = 0xE047,
    Up = 0xE048,
    PageUp = 0xE049,
    Left = 0xE04B,
    Right = 0xE04D,
    End = 0xE04F,
    Down = 0xE050,
    PageDown = 0xE051,
    Insert = 0xE052,
    Delete = 0xE053,
    LeftWin = 0xE05B,
    RightWin = 0xE05C,
    Menu = 0xE05D,

    // Media keys. Injected as virtual keys, since Windows acts on those, not on the scan codes.
    MediaPrevious = 0xE010,
    MediaNext = 0xE019,
    VolumeMute = 0xE020,
    MediaPlayPause = 0xE022,
    MediaStop = 0xE024,
    VolumeDown = 0xE02E,
    VolumeUp = 0xE030,
}

public static class KeyInfo
{
    public static bool IsExtended(Key key) => ((ushort)key & 0xFF00) == 0xE000;

    public static bool IsMedia(Key key) => key is Key.MediaPrevious or Key.MediaNext or Key.VolumeMute
        or Key.MediaPlayPause or Key.MediaStop or Key.VolumeDown or Key.VolumeUp;

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LeftWin or Key.RightWin;
}

public enum MouseButton { Left, Right, Middle, X1, X2 }
