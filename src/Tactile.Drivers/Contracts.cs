// Fixed in phase 4 (docs/contract-phase4.md). Do not edit.
using Tactile.Core;

namespace Tactile.Drivers;

public enum DriverKind { UsbIp, HidHide }

public enum DriverState { Missing, Ready, Unsupported, Error }

public sealed record DriverStatus(DriverKind Kind, DriverState State, string? Version, string? Detail)
{
    private readonly LocalizedMessage? _translation;
    private readonly string? _detail = Detail;

    public string? Detail
    {
        get => _translation?.Value ?? _detail;
        init
        {
            _detail = value;
            _translation = null;
        }
    }

    public string? EnglishDetail => _translation?.En ?? _detail;

    internal DriverStatus(DriverKind kind, DriverState state, string? version, string en, string tr)
        : this(kind, state, version, en) => _translation = new(en, tr);
}

public sealed record InstallResult(bool Ok, bool RebootRequired, string Message)
{
    internal LocalizedMessage? Translation { get; init; }
    public string EnglishMessage => Translation?.En ?? Message;
}

public interface IDriverManager
{
    DriverStatus Status(DriverKind kind);

    /// <summary>
    /// Downloads the pinned official installer, checks SHA-256 and Authenticode signer,
    /// then starts it elevated (UAC). Never runs anything unverified.
    /// </summary>
    Task<InstallResult> InstallAsync(DriverKind kind, IProgress<string>? progress, CancellationToken ct);
}

public interface IVhci
{
    /// <summary>Asks the VHCI driver to import busId from 127.0.0.1:tcpPort. Returns the hub port.</summary>
    int Attach(int tcpPort, string busId);

    void Detach(int hubPort);
}

public interface IDeviceHider
{
    /// <summary>Hides the device from every process except Tactile. Journal first.</summary>
    bool Hide(string deviceInstanceId);

    void Unhide(string deviceInstanceId);
}

/// <summary>Journal of system changes, %APPDATA%\Tactile\recovery.json.</summary>
public interface IRecovery
{
    /// <summary>Journal existed at startup with entries.</summary>
    bool UncleanShutdown { get; }

    /// <summary>Detach every journaled hub port, undo every journaled HidHide change.</summary>
    void RestoreAll();

    void MarkCleanExit();
}

/// <summary>Device tree helpers (SetupAPI / cfgmgr32).</summary>
public interface IDeviceTree
{
    /// <summary>True if the HID device path belongs to a device under the usbip-win2 VHCI.</summary>
    bool IsVirtual(string hidDevicePath);

    /// <summary>Device instance id for a HID device path (what HidHide's blacklist takes), or null.</summary>
    string? InstanceId(string hidDevicePath);
}

public sealed partial record DriverPlatform(IDriverManager Drivers, IVhci Vhci, IDeviceHider Hider, IRecovery Recovery, IDeviceTree Tree)
{
    public static partial DriverPlatform CreateWindows(string dataDir);
}
