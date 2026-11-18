using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Drivers;

internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;
    public const uint OpenExisting = 3;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorMoreData = 234;
    public const int ErrorDeviceNotConnected = 1167;
    public const int ErrorCancelled = 1223;

    public const int CrSuccess = 0;
    public const int CrBufferSmall = 0x1A;
    public const uint CmGetDeviceInterfaceListPresent = 0;
    public const uint CmLocateDevnodeNormal = 0;
    public const uint CmDrpHardwareId = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInterfaceData
    {
        public int Size;
        public Guid ClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DevInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinTrustFileInfo
    {
        public int Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinTrustData
    {
        public int Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, byte* inBuffer, int inSize, byte* outBuffer, int outSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int QueryDosDevice(string deviceName, char[] target, int max);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List(ref Guid interfaceGuid, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID(uint devInst, char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_Registry_Property(uint devInst, uint property, out uint type, byte[]? buffer, ref uint length, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint parent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiOpenDeviceInterface(nint set, string devicePath, uint flags, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(nint set, int index, ref DevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(nint set, ref DevInfoData data, char[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    public static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WinTrustData data);

    public static SafeFileHandle Open(string path, uint access, uint share) =>
        CreateFile(path, access, share, 0, OpenExisting, 0, 0);
}
