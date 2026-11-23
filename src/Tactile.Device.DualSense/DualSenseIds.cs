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

    public static List<ControllerInfo> FindControllers()
    {
        var found = new List<ControllerInfo>();
        foreach (HidDeviceInfo hid in HidDevice.Enumerate(SonyVendorId))
        {
            ControllerModel? model = hid.ProductId switch
            {
                DualSenseProductId => ControllerModel.DualSense,
                DualSenseEdgeProductId => ControllerModel.DualSenseEdge,
                DualShock4V1ProductId or DualShock4V2ProductId or DualShock4DongleProductId => ControllerModel.DualShock4,
                _ => null,
            };

            // Windows exposes the gamepad as one collection; the input report length tells the link apart.
            Link? link = hid.InputReportLength switch
            {
                UsbInputReportLength => Link.Usb,
                BluetoothInputReportLength => Link.Bluetooth,
                _ => null,
            };

            // The wireless adaptor only exposes the USB report layout.
            if (hid.ProductId == DualShock4DongleProductId && link != Link.Usb)
            {
                link = null;
            }

            if (model is not null && link is not null)
            {
                found.Add(new ControllerInfo(hid, model.Value, link.Value));
            }
        }

        return found;
    }
}
