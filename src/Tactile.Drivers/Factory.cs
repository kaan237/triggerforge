namespace Tactile.Drivers;

public sealed partial record DriverPlatform
{
    public static partial DriverPlatform CreateWindows(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var journal = new RecoveryJournal(Path.Combine(dataDir, "recovery.json"));
        var vhciControl = new WindowsVhciControl();
        var hidHideControl = new WindowsHidHideControl();
        var vhci = new Vhci(vhciControl, journal);
        var hider = new DeviceHider(hidHideControl, journal, OwnImagePath);
        var drivers = new DriverManager(vhciControl, hidHideControl, Path.Combine(Path.GetTempPath(), "Tactile", "drivers"));
        return new DriverPlatform(drivers, vhci, hider, new Recovery(journal, vhci, hider), new DeviceTree());
    }

    private static string? OwnImagePath() =>
        Environment.ProcessPath is { } p ? NtPath.FromWin32(p, NtPath.QueryDosDevice) : null;
}
