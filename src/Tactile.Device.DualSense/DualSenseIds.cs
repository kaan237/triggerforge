using Tactile.Device.Hid;

namespace Tactile.Device.DualSense;

public enum ControllerModel { DualSense, DualSenseEdge, DualShock4 }

public enum Link { Usb, Bluetooth }

public sealed record ControllerInfo(HidDeviceInfo Hid, ControllerModel Model, Link Link);

public static class DualSenseIds
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseProductId = 0x0CE6;
    public const ushort DualSenseEdgeProductId = 0x0DF2;
    public const ushort DualShock4V1ProductId = 0x05C4;
    public const ushort DualShock4V2ProductId = 0x09CC;
    public const ushort DualShock4DongleProductId = 0x0BA0;

    public const int UsbInputReportLength = 64;
    public const int BluetoothInputReportLength = 78;

    public static bool IsDualShock4(ControllerModel model) => model == ControllerModel.DualShock4;

    /// <summary>
    /// Model and link from the product id and the HID input report length, or null for anything else.
    /// Windows exposes the gamepad as one collection, so the length tells the link apart. A Bluetooth
    /// DualShock 4 reports its largest input report (0x19, 547 bytes), not the 78 bytes of report 0x11.
    /// </summary>
    public static (ControllerModel Model, Link Link)? Classify(ushort productId, int inputReportLength)
    {
        switch (productId)
        {
            case DualSenseProductId:
            case DualSenseEdgeProductId:
                ControllerModel model = productId == DualSenseProductId ? ControllerModel.DualSense : ControllerModel.DualSenseEdge;
                return inputReportLength switch
                {
                    UsbInputReportLength => (model, Link.Usb),
                    BluetoothInputReportLength => (model, Link.Bluetooth),
                    _ => null,
                };
            case DualShock4V1ProductId:
            case DualShock4V2ProductId:
                if (inputReportLength == UsbInputReportLength)
                {
                    return (ControllerModel.DualShock4, Link.Usb);
                }

                return inputReportLength >= BluetoothInputReportLength ? (ControllerModel.DualShock4, Link.Bluetooth) : null;
            case DualShock4DongleProductId:
                // The wireless adaptor only exposes the USB report layout.
                return inputReportLength == UsbInputReportLength ? (ControllerModel.DualShock4, Link.Usb) : null;
            default:
                return null;
        }
    }

    public static List<ControllerInfo> FindControllers()
    {
        var found = new List<ControllerInfo>();
        foreach (HidDeviceInfo hid in HidDevice.Enumerate(SonyVendorId))
        {
            if (Classify(hid.ProductId, hid.InputReportLength) is { } kind)
            {
                found.Add(new ControllerInfo(hid, kind.Model, kind.Link));
            }
        }

        return found;
    }
}
