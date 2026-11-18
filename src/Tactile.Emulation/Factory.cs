namespace Tactile.Emulation;

public static partial class UsbIpServerFactory
{
    public static partial IUsbIpServer Start(int port) => new UsbIpServer(port);
}
