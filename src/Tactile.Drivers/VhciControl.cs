using Tactile.Core;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Drivers;

/// <summary>Raised for driver failures; The message follows the selected language.</summary>
public sealed class DriverException(LocalizedMessage translation, Exception? inner = null) : LocalizedException(translation, inner)
{
    public DriverException(string message, Exception? inner = null) : this(new LocalizedMessage(message, message), inner) { }

    public int Win32Error => (InnerException as Win32Exception)?.NativeErrorCode ?? 0;
}

/// <summary>Raw VHCI operations, behind an interface so the journal logic is testable.</summary>
internal interface IVhciControl
{
    /// <summary>Number of present VHCI device interfaces (exactly one is usable).</summary>
    int InterfaceCount();

    int PlugIn(string host, string service, string busId);

    /// <summary>Returns false if the hub port was already empty.</summary>
    bool PlugOut(int hubPort);

    IReadOnlyList<ImportedDevice> GetImported();

    int StopAttachAttempts(string host, string service, string busId);
}

internal sealed unsafe class WindowsVhciControl : IVhciControl
{
    public int InterfaceCount() => InterfacePaths().Count;

    public static List<string> InterfacePaths()
    {
        Guid guid = VhciLayout.InterfaceGuid;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.CM_Get_Device_Interface_List_Size(out uint len, ref guid, null, Native.CmGetDeviceInterfaceListPresent) != Native.CrSuccess || len <= 1)
                return [];
            var buf = new char[len];
            int cr = Native.CM_Get_Device_Interface_List(ref guid, null, buf, len, Native.CmGetDeviceInterfaceListPresent);
            if (cr == Native.CrBufferSmall)
                continue;
            if (cr != Native.CrSuccess)
                return [];
            return [.. new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }

        return [];
    }

    private static SafeFileHandle OpenVhci()
    {
        List<string> paths = InterfacePaths();
        if (paths.Count != 1)
            throw new DriverException(paths.Count == 0 ? new LocalizedMessage("usbip-win2 driver not found.", "usbip-win2 sürücüsü bulunamadı.") : new LocalizedMessage("Found more than one usbip-win2 controller.", "Birden fazla usbip-win2 denetleyicisi bulundu."));
        SafeFileHandle h = Native.Open(paths[0], Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite);
        if (h.IsInvalid)
        {
            var e = new Win32Exception();
            h.Dispose();
            throw new DriverException(new LocalizedMessage("Could not open the usbip-win2 controller.", "usbip-win2 denetleyicisi açılamadı."), e);
        }

        return h;
    }

    private static bool Ioctl(SafeFileHandle h, uint code, byte[] buffer, int inSize, int outSize, out int returned)
    {
        fixed (byte* p = buffer)
            return Native.DeviceIoControl(h, code, p, inSize, outSize == 0 ? null : p, outSize, out returned, 0);
    }

    public int PlugIn(string host, string service, string busId)
    {
        byte[] b = VhciLayout.Plugin(host, service, busId);
        using SafeFileHandle h = OpenVhci();
        // Pends in the driver until the import handshake with our server and the plug-in finish.
        if (!Ioctl(h, VhciLayout.IoctlPluginHardware, b, b.Length, b.Length, out _))
            throw new DriverException(new LocalizedMessage("Could not connect the virtual controller.", "Sanal kontrolcü bağlanamadı."), new Win32Exception());
        int port = VhciLayout.PluginPort(b);
        if (port < 1)
            throw new DriverException(new LocalizedMessage("Could not connect the virtual controller (invalid port).", "Sanal kontrolcü bağlanamadı (geçersiz port)."));
        return port;
    }

    public bool PlugOut(int hubPort)
    {
        byte[] b = VhciLayout.Plugout(hubPort);
        using SafeFileHandle h = OpenVhci();
        if (Ioctl(h, VhciLayout.IoctlPlugoutHardware, b, b.Length, 0, out _))
            return true;
        var e = new Win32Exception();
        if (e.NativeErrorCode == Native.ErrorDeviceNotConnected)
            return false;
        throw new DriverException(new LocalizedMessage("Could not detach the virtual controller.", "Sanal kontrolcü ayrılamadı."), e);
    }

    public IReadOnlyList<ImportedDevice> GetImported()
    {
        using SafeFileHandle h = OpenVhci();
        for (int capacity = 4; capacity <= 1024; capacity *= 2)
        {
            byte[] b = VhciLayout.ImportedRequest(capacity);
            if (Ioctl(h, VhciLayout.IoctlGetImportedDevices, b, 4, b.Length, out int returned))
                return VhciLayout.ParseImported(b, returned);
            var e = new Win32Exception();
            if (e.NativeErrorCode is not (Native.ErrorInsufficientBuffer or Native.ErrorMoreData))
                throw new DriverException(new LocalizedMessage("Could not read the attached virtual devices.", "Bağlı sanal aygıtlar okunamadı."), e);
        }

        throw new DriverException(new LocalizedMessage("Could not read the attached virtual devices.", "Bağlı sanal aygıtlar okunamadı."));
    }

    public int StopAttachAttempts(string host, string service, string busId)
    {
        byte[] b = VhciLayout.StopAttachAttempts(host, service, busId);
        using SafeFileHandle h = OpenVhci();
        if (!Ioctl(h, VhciLayout.IoctlStopAttachAttempts, b, b.Length, b.Length, out _))
            throw new DriverException(new LocalizedMessage("Could not stop reconnection attempts.", "Yeniden bağlanma denemeleri durdurulamadı."), new Win32Exception());
        return VhciLayout.StopCountOf(b);
    }
}
