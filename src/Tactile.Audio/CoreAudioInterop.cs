using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Audio;

internal static partial class CoreAudioInterop
{
    internal const int Render = 0;
    internal const int ConsoleRole = 0;
    internal const uint DeviceActive = 1;
    internal const uint Loopback = 0x00020000;
    internal const uint EventCallback = 0x00040000;
    internal const uint ConvertPcm = 0x80000000;
    internal const uint DefaultSrcQuality = 0x08000000;
    internal const uint Silent = 0x2;
    internal const int UnsupportedFormat = unchecked((int)0x88890008);
    internal const int BufferEmpty = 0x08890001;

    internal static void Check(int result, string message)
    {
        if (result < 0)
            throw new AudioUnavailableException(message, Marshal.GetExceptionForHR(result));
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    internal static string TakeString(nint pointer)
    {
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    internal static IMMDevice DefaultRender(IMMDeviceEnumerator devices)
    {
        Check(devices.GetDefaultAudioEndpoint(Render, ConsoleRole, out var device),
            "Varsayılan ses çıkışı bulunamadı.");
        return device;
    }

    internal static T Activate<T>(IMMDevice device) where T : class
    {
        Guid iid = typeof(T).GUID;
        Check(device.Activate(in iid, 0x17, 0, out var value), "Ses çıkışı açılamadı.");
        return RequireInterface<T>(value);
    }

    internal static T RequireInterface<T>(object? value) where T : class
    {
        if (value is T result)
            return result;
        Release(value);
        throw new AudioUnavailableException("Windows ses arabirimi kullanılamıyor.");
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AvRevertMmThreadCharacteristics(nint handle);

    [LibraryImport("mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ActivateAudioInterfaceAsync(string devicePath, in Guid iid, in BlobVariant parameters,
        nint completionHandler, out nint operation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeWaitHandle OpenProcess(uint access, int inheritHandle, uint processId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationProcess(SafeWaitHandle process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnLength);

    /// <summary>Parent process id, or null when the process cannot be queried.</summary>
    internal static unsafe int? ParentProcessId(int processId)
    {
        const uint queryLimitedInformation = 0x1000;
        using SafeWaitHandle handle = OpenProcess(queryLimitedInformation, 0, (uint)processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        return NtQueryInformationProcess(handle, 0, out ProcessBasicInformation info, sizeof(ProcessBasicInformation), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessActivation
    {
        internal int Type;
        internal uint ProcessId;
        internal int Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlobVariant
    {
        internal ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        internal uint Size;
        internal nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        internal Guid Format;
        internal uint Id;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator;

    // Keep the SDK vtable order, including slots that this library never calls.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, int context, nint parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyVariant
    {
        internal ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        internal nint Pointer;
        private nint _unionPadding;
    }

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropertyVariant value);

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, nint devicePosition, nint clock);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(nint operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject;

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionId, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionId, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, in Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, in Guid context);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(in Guid groupingId, in Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint events);
        [PreserveSig] int UnregisterAudioSessionNotification(nint events);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }
}
