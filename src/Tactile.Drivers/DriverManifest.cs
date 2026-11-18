namespace Tactile.Drivers;

/// <summary>One pinned official installer (docs/protocol-emulation.md §8).</summary>
internal sealed record DriverPackage(
    DriverKind Kind,
    string Name,
    string Version,
    Uri Url,
    string FileName,
    long Size,
    string Sha256,
    string SilentArgs,
    string? RequiredVersion,
    string? ExpectedSigner);

internal static class DriverManifest
{
    public static readonly DriverPackage UsbIp = new(
        DriverKind.UsbIp,
        "usbip-win2",
        "0.9.8.1",
        new Uri("https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe"),
        "USBip-0.9.8.1-x64.exe",
        26_083_632,
        "38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a",
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        // Struct layouts change between releases with equal sizes, so only this exact one is driven.
        RequiredVersion: "0.9.8.1",
        ExpectedSigner: "Cloudyne Systems (Scheibling Consulting AB)"); // verified 2026-11-19

    public static readonly DriverPackage HidHide = new(
        DriverKind.HidHide,
        "HidHide",
        "1.5.230",
        new Uri("https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe"),
        "HidHide_1.5.230_x64.exe",
        8_078_016,
        "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6",
        "/exenoui /qn /norestart",
        RequiredVersion: null,
        ExpectedSigner: "Nefarius Software Solutions e.U."); // verified 2026-11-19

    public const string UsbIpUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1";

    public static DriverPackage Get(DriverKind kind) => kind switch
    {
        DriverKind.UsbIp => UsbIp,
        DriverKind.HidHide => HidHide,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
