using System.Runtime.InteropServices;
using System.Text;

namespace Tactile.Drivers;

internal sealed class DeviceTree : IDeviceTree
{
    public const string VhciHardwareId = @"ROOT\USBIP_WIN2\UDE";
    private const string VhciInstancePrefix = @"ROOT\USBIP_WIN2";

    public bool IsVirtual(string hidDevicePath)
    {
        string? id = InstanceId(hidDevicePath);
        if (id is null || Native.CM_Locate_DevNode(out uint node, id, Native.CmLocateDevnodeNormal) != Native.CrSuccess)
            return false;
        for (int depth = 0; depth < 32 && Native.CM_Get_Parent(out uint parent, node, 0) == Native.CrSuccess; depth++)
        {
            node = parent;
            if (IsVhciNode(DeviceId(node), HardwareIds(node)))
                return true;
        }

        return false;
    }

    public string? InstanceId(string hidDevicePath)
    {
        if (string.IsNullOrWhiteSpace(hidDevicePath))
            return null;
        return InstanceIdFromSetupApi(hidDevicePath) ?? InstanceIdFromPath(hidDevicePath);
    }

    /// <summary>
    /// The VHCI root device is matched by hardware id because its instance id can be generic
    /// (e.g. ROOT\USB\0000).
    /// </summary>
    internal static bool IsVhciNode(string? instanceId, IEnumerable<string> hardwareIds) =>
        (instanceId?.StartsWith(VhciInstancePrefix, StringComparison.OrdinalIgnoreCase) ?? false)
        || hardwareIds.Any(h => string.Equals(h, VhciHardwareId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fallback: \\?\HID#VID..#7&amp;abc#{guid} → HID\VID..\7&amp;abc.</summary>
    internal static string? InstanceIdFromPath(string path)
    {
        string p = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[4..] : path;
        int g = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (g > 0)
            p = p[..g];
        p = p.Replace('#', '\\');
        return p.Contains('\\') ? p : null;
    }

    private static string? InstanceIdFromSetupApi(string path)
    {
        nint set = Native.SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1)
            return null;
        try
        {
            var ifData = new Native.DeviceInterfaceData { Size = Marshal.SizeOf<Native.DeviceInterfaceData>() };
            if (!Native.SetupDiOpenDeviceInterface(set, path, 0, ref ifData))
                return null;
            var info = new Native.DevInfoData { Size = Marshal.SizeOf<Native.DevInfoData>() };
            if (!Native.SetupDiEnumDeviceInfo(set, 0, ref info))
                return null;
            var buf = new char[512];
            if (!Native.SetupDiGetDeviceInstanceId(set, ref info, buf, buf.Length, out _))
                return null;
            int end = Array.IndexOf(buf, '\0');
            return new string(buf, 0, end < 0 ? buf.Length : end);
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? DeviceId(uint node)
    {
        var buf = new char[512];
        if (Native.CM_Get_Device_ID(node, buf, buf.Length, 0) != Native.CrSuccess)
            return null;
        int end = Array.IndexOf(buf, '\0');
        return new string(buf, 0, end < 0 ? buf.Length : end);
    }

    private static List<string> HardwareIds(uint node)
    {
        uint len = 0;
        int cr = Native.CM_Get_DevNode_Registry_Property(node, Native.CmDrpHardwareId, out _, null, ref len, 0);
        if ((cr != Native.CrBufferSmall && cr != Native.CrSuccess) || len == 0)
            return [];
        var buf = new byte[len];
        if (Native.CM_Get_DevNode_Registry_Property(node, Native.CmDrpHardwareId, out _, buf, ref len, 0) != Native.CrSuccess)
            return [];
        return [.. Encoding.Unicode.GetString(buf, 0, (int)len & ~1).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }
}
