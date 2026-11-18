using System.Diagnostics;
using Tactile.Device.DualSense;
using Tactile.Device.Hid;

// tactile-capture list
// tactile-capture record <file.bin> [seconds]

string command = args.Length > 0 ? args[0] : "list";
List<ControllerInfo> controllers = DualSenseIds.FindControllers();

if (command == "list")
{
    if (controllers.Count == 0)
    {
        Console.WriteLine("No DualSense found.");
        return 1;
    }

    foreach (ControllerInfo c in controllers)
    {
        Console.WriteLine($"{c.Model} over {c.Link}  in={c.Hid.InputReportLength} out={c.Hid.OutputReportLength} feature={c.Hid.FeatureReportLength}");
        Console.WriteLine($"  {c.Hid.Path}");
    }

    return 0;
}

if (controllers.Count == 0)
{
    Console.Error.WriteLine("No DualSense found.");
    return 1;
}

if (command == "probe" && args.Length >= 2)
{
    return Probe.Run(controllers[0], args[1..]);
}

if (command != "record" || args.Length < 2)
{
    Console.Error.WriteLine("usage: tactile-capture list | record <file.bin> [seconds] | probe <info|light|rumble|trigger|haptic> ...");
    return 2;
}

ControllerInfo target = controllers[0];
double seconds = args.Length > 2 && double.TryParse(args[2], out double s) ? s : 10;
using HidDevice device = HidDevice.Open(target.Hid);
using BinaryWriter writer = CaptureFile.Create(File.Create(args[1]), target.Link);
var clock = Stopwatch.StartNew();
long Micros() => clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

// Feature 0x05 (calibration) and 0x20 (firmware). Reading 0x05 also puts Bluetooth into full report mode.
foreach (byte id in new byte[] { 0x05, 0x20 })
{
    byte[] feature = new byte[target.Hid.FeatureReportLength];
    feature[0] = id;
    if (device.TryGetFeature(feature))
    {
        CaptureFile.Append(writer, new CaptureRecord(Micros(), CaptureKind.Feature, feature));
    }
    else
    {
        Console.Error.WriteLine($"Feature 0x{id:X2} read failed.");
    }
}

Console.WriteLine($"Recording {target.Model} over {target.Link} for {seconds:0.#} s. Move sticks, press buttons, touch the pad.");
byte[] buffer = new byte[target.Hid.InputReportLength];
int count = 0;
var ids = new Dictionary<byte, int>();
while (clock.Elapsed.TotalSeconds < seconds)
{
    int read = device.Read(buffer);
    if (read <= 0)
    {
        continue;
    }

    CaptureFile.Append(writer, new CaptureRecord(Micros(), CaptureKind.Input, buffer[..read]));
    ids[buffer[0]] = ids.GetValueOrDefault(buffer[0]) + 1;
    count++;
}

Console.WriteLine($"{count} reports ({count / seconds:0} per second).");
foreach ((byte id, int n) in ids)
{
    Console.WriteLine($"  report 0x{id:X2}: {n}");
}

return 0;
