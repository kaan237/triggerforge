using System.Runtime.InteropServices;

namespace Tactile.Input.Windows;

internal static partial class Native
{
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;

    public const uint KeyeventfExtendedKey = 0x0001;
    public const uint KeyeventfKeyUp = 0x0002;
    public const uint KeyeventfUnicode = 0x0004;
    public const uint KeyeventfScanCode = 0x0008;

    public const uint MouseeventfMove = 0x0001;
    public const uint MouseeventfLeftDown = 0x0002;
    public const uint MouseeventfLeftUp = 0x0004;
    public const uint MouseeventfRightDown = 0x0008;
    public const uint MouseeventfRightUp = 0x0010;
    public const uint MouseeventfMiddleDown = 0x0020;
    public const uint MouseeventfMiddleUp = 0x0040;
    public const uint MouseeventfXDown = 0x0080;
    public const uint MouseeventfXUp = 0x0100;
    public const uint MouseeventfWheel = 0x0800;
    public const uint MouseeventfHWheel = 0x1000;
    public const uint XButton1 = 0x0001;
    public const uint XButton2 = 0x0002;

    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const int TokenElevationClass = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    // The union starts at the pointer-size boundary: offset 8, total size 40 on x64.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputRecord
    {
        public uint Type;
        public InputUnion U;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static unsafe partial uint SendInput(uint count, InputRecord* inputs, int size);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int infoClass, out uint info, uint length, out uint returned);
}
