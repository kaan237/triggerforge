using System.Globalization;
using System.Runtime.InteropServices;

namespace Tactile.Device.DualSense;

/// <summary>Drops a Bluetooth connection, which makes the controller switch itself off.</summary>
public static partial class BluetoothLink
{
    private const uint IoctlBthDisconnectDevice = 0x0041000C;

    /// <param name="mac">"AA:BB:CC:DD:EE:FF".</param>
    /// <returns>True when a radio accepted the request.</returns>
    public static bool Disconnect(string mac)
    {
        if (!TryParseAddress(mac, out ulong address))
        {
            return false;
        }

        var search = new FindRadioParams { Size = (uint)Marshal.SizeOf<FindRadioParams>() };
        nint find = BluetoothFindFirstRadio(ref search, out nint radio);
        if (find == 0)
        {
            return false;
        }

        try
        {
            do
            {
                bool ok = DeviceIoControl(radio, IoctlBthDisconnectDevice, ref address, sizeof(ulong), 0, 0, out _, 0);
                CloseHandle(radio);
                if (ok)
                {
                    return true;
                }
            }
            while (BluetoothFindNextRadio(find, out radio));

            return false;
        }
        finally
        {
            BluetoothFindRadioClose(find);
        }
    }

    /// <summary>BTH_ADDR: the first MAC byte is the most significant of the low 48 bits.</summary>
    public static bool TryParseAddress(string? mac, out ulong address)
    {
        address = 0;
        string[] parts = (mac ?? "").Split(':', '-');
        if (parts.Length != 6)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length != 2 || !byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                address = 0;
                return false;
            }

            address = (address << 8) | b;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams
    {
        public uint Size;
    }

    [LibraryImport("bthprops.cpl", SetLastError = true)]
    private static partial nint BluetoothFindFirstRadio(ref FindRadioParams search, out nint radio);

    [LibraryImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindNextRadio(nint find, out nint radio);

    [LibraryImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindRadioClose(nint find);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, ref ulong input, int inputSize,
        nint output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
