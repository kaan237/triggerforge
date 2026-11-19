namespace Tactile.Emulation;

public static class SecondaryPads
{
    /// <summary>Creates a gamepad-only device with the USB identity of the secondary slot.</summary>
    public static IVirtualPad CreateSecondaryPad(this IUsbIpServer server, int slot, VirtualKind kind, DualSenseIdentity? identity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 3);
        return server is UsbIpServer usb ? usb.CreateSecondary(slot, kind, identity) : server.Create(kind, identity);
    }

    internal static string Serial(int slot) => $"TACTILE{slot}";
}
