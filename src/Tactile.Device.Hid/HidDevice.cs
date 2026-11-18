using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Device.Hid;

public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength);

/// <summary>An open HID device handle. Reads block; use one reader thread per device.</summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;

    private HidDevice(HidDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: false);
    }

    public HidDeviceInfo Info { get; }

    /// <summary>Lists present HID devices, optionally filtered by vendor id.</summary>
    public static List<HidDeviceInfo> Enumerate(ushort? vendorId = null)
    {
        var result = new List<HidDeviceInfo>();
        foreach (string path in EnumeratePaths())
        {
            using SafeFileHandle handle = Native.CreateFile(path, 0, Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, 0, 0);
            if (handle.IsInvalid)
            {
                continue;
            }

            HidDeviceInfo? info = Describe(path, handle);
            if (info is not null && (vendorId is null || info.VendorId == vendorId))
            {
                result.Add(info);
            }
        }

        return result;
    }

    public static HidDevice Open(HidDeviceInfo info)
    {
        SafeFileHandle handle = Native.CreateFile(info.Path, Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {info.Path}");
        }

        return new HidDevice(info, handle);
    }

    /// <summary>Reads one input report. Byte 0 is the report id.</summary>
    public int Read(byte[] buffer) => _stream.Read(buffer, 0, Math.Min(buffer.Length, Info.InputReportLength));

    public void Write(ReadOnlySpan<byte> report) => _stream.Write(report);

    /// <summary>Reads a feature report; <paramref name="buffer"/>[0] must hold the report id.</summary>
    public bool TryGetFeature(byte[] buffer) => Native.HidD_GetFeature(_handle, buffer, buffer.Length);

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }

    private static HidDeviceInfo? Describe(string path, SafeFileHandle handle)
    {
        var attributes = new Native.HiddAttributes { Size = Marshal.SizeOf<Native.HiddAttributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes) || !Native.HidD_GetPreparsedData(handle, out nint preparsed))
        {
            return null;
        }

        try
        {
            if (Native.HidP_GetCaps(preparsed, out Native.HidpCaps caps) != Native.HidpStatusSuccess)
            {
                return null;
            }

            return new HidDeviceInfo(path, attributes.VendorId, attributes.ProductId,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }
    }

    private static IEnumerable<string> EnumeratePaths()
    {
        Native.HidD_GetHidGuid(out Guid hidGuid);
        nint set = Native.SetupDiGetClassDevs(ref hidGuid, null, 0, Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == -1)
        {
            yield break;
        }

        try
        {
            var data = new Native.DeviceInterfaceData { Size = Marshal.SizeOf<Native.DeviceInterfaceData>() };
            for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, i, ref data); i++)
            {
                string? path = GetPath(set, ref data);
                if (path is not null)
                {
                    yield return path;
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? GetPath(nint set, ref Native.DeviceInterfaceData data)
    {
        Native.SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out int size, 0);
        if (size == 0)
        {
            return null;
        }

        nint detail = Marshal.AllocHGlobal(size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, 0))
            {
                return null;
            }

            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }
}
