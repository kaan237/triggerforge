using Tactile.Device.Hid;

namespace Tactile.Device.DualSense;

public enum ControllerModel { DualSense, DualSenseEdge }

public enum Link { Usb, Bluetooth }

public sealed record ControllerInfo(HidDeviceInfo Hid, ControllerModel Model, Link Link);

public static class DualSenseIds
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseProductId = 0x0CE6;
    public const ushort DualSenseEdgeProductId = 0x0DF2;

    public const int UsbInputReportLength = 64;
    public const int BluetoothInputReportLength = 78;

    public static List<ControllerInfo> FindControllers()
    {
        var found = new List<ControllerInfo>();
        foreach (HidDeviceInfo hid in HidDevice.Enumerate(SonyVendorId))
        {
            ControllerModel? model = hid.ProductId switch
            {
                DualSenseProductId => ControllerModel.DualSense,
                DualSenseEdgeProductId => ControllerModel.DualSenseEdge,
                _ => null,
            };

            // Windows exposes the gamepad as one collection; the input report length tells the link apart.
            Link? link = hid.InputReportLength switch
            {
                UsbInputReportLength => Link.Usb,
                BluetoothInputReportLength => Link.Bluetooth,
                _ => null,
            };

            if (model is not null && link is not null)
            {
                found.Add(new ControllerInfo(hid, model.Value, link.Value));
            }
        }

        return found;
    }
}
