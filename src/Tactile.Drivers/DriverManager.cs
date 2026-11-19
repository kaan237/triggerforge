using Tactile.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Tactile.Drivers;

internal sealed class DriverManager(IVhciControl vhci, IHidHideControl hidHide, string downloadDir) : IDriverManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public DriverStatus Status(DriverKind kind)
    {
        try
        {
            return kind switch
            {
                DriverKind.UsbIp => UsbIpStatus(ReadUninstallValue(DriverManifest.UsbIpUninstallKey, "DisplayVersion"), vhci.InterfaceCount()),
                DriverKind.HidHide => HidHideStatus(HidHideVersion(), hidHide.Probe()),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }
        catch (Exception e) when (e is not ArgumentOutOfRangeException)
        {
            return new DriverStatus(kind, DriverState.Error, null, $"Could not read driver status: {Text.English(e)}", $"Sürücü durumu okunamadı: {e.Message}");
        }
    }

    internal static DriverStatus UsbIpStatus(string? version, int interfaceCount)
    {
        string required = DriverManifest.UsbIp.RequiredVersion!;
        if (version is null)
        {
            return interfaceCount == 0
                ? new DriverStatus(DriverKind.UsbIp, DriverState.Missing, null, null)
                : new DriverStatus(DriverKind.UsbIp, DriverState.Unsupported, null, $"Could not detect the usbip-win2 version. Version {required} is required.", $"usbip-win2 sürümü belirlenemedi; {required} gerekli.");
        }

        if (version != required)
            return new DriverStatus(DriverKind.UsbIp, DriverState.Unsupported, version, $"usbip-win2 {version} is unsupported. Version {required} is required.", $"usbip-win2 {version} desteklenmiyor; {required} gerekli.");
        return interfaceCount switch
        {
            1 => new DriverStatus(DriverKind.UsbIp, DriverState.Ready, version, null),
            0 => new DriverStatus(DriverKind.UsbIp, DriverState.Error, version, "usbip-win2 is installed but inactive. Restart your PC.", "usbip-win2 yüklü ama sürücü etkin değil. Bilgisayarı yeniden başlatın."),
            _ => new DriverStatus(DriverKind.UsbIp, DriverState.Error, version, "Found more than one usbip-win2 controller.", "Birden fazla usbip-win2 denetleyicisi bulundu."),
        };
    }

    internal static DriverStatus HidHideStatus(string? version, HidHideProbe probe) => probe switch
    {
        HidHideProbe.Ok => new DriverStatus(DriverKind.HidHide, DriverState.Ready, version, null),
        HidHideProbe.NotFound when version is null => new DriverStatus(DriverKind.HidHide, DriverState.Missing, null, null),
        HidHideProbe.NotFound => new DriverStatus(DriverKind.HidHide, DriverState.Error, version, "HidHide is installed but inactive. Restart your PC.", "HidHide yüklü ama sürücü çalışmıyor. Bilgisayarı yeniden başlatın."),
        HidHideProbe.Busy => new DriverStatus(DriverKind.HidHide, DriverState.Error, version, "Another app is using HidHide, such as HidHide Settings. Close it and try again.", "HidHide başka bir uygulama tarafından kullanılıyor (ör. HidHide ayarları). Onu kapatıp tekrar deneyin."),
        _ => new DriverStatus(DriverKind.HidHide, DriverState.Error, version, "Could not access the HidHide driver.", "HidHide sürücüsüne erişilemedi."),
    };

    private static string? ReadUninstallValue(string subKey, string name)
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? k = hklm.OpenSubKey(subKey);
        return (k?.GetValue(name) as string)?.Trim() is { Length: > 0 } v ? v : null;
    }

    private static string? HidHideVersion()
    {
        using (RegistryKey? dep = Registry.ClassesRoot.OpenSubKey(@"Installer\Dependencies\NSS.Drivers.HidHide.x64"))
        {
            if ((dep?.GetValue("Version") as string)?.Trim() is { Length: > 0 } v)
                return v;
        }

        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (root is null)
            return null;
        foreach (string name in root.GetSubKeyNames())
        {
            using RegistryKey? k = root.OpenSubKey(name);
            if (k?.GetValue("DisplayName") is string dn && dn.Contains("HidHide", StringComparison.OrdinalIgnoreCase)
                && (k.GetValue("DisplayVersion") as string)?.Trim() is { Length: > 0 } v)
                return v;
        }

        return null;
    }

    public async Task<InstallResult> InstallAsync(DriverKind kind, IProgress<string>? progress, CancellationToken ct)
    {
        DriverPackage pkg = DriverManifest.Get(kind);
        string file = Path.Combine(downloadDir, pkg.FileName);
        try
        {
            Directory.CreateDirectory(downloadDir);
            if (pkg.Url.Scheme != Uri.UriSchemeHttps)
                return Fail("Downloads require HTTPS.", "Yalnızca HTTPS üzerinden indirilebilir.");

            progress?.Report(Text.Pick($"Downloading {pkg.Name} {pkg.Version}…", $"{pkg.Name} {pkg.Version} indiriliyor…"));
            try
            {
                await DownloadAsync(pkg, file, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(file);
                return Fail("Installation cancelled.", "Kurulum iptal edildi.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or DownloadFailure)
            {
                TryDelete(file);
                return Fail($"Download failed: {Text.English(e)}", $"İndirme başarısız: {e.Message}");
            }

            progress?.Report(Text.Pick("Verifying file…", "Dosya doğrulanıyor…"));
            if (VerifyOrDelete(file, pkg) is { } error)
                return Fail(error.En, error.Tr);

            progress?.Report(Text.Pick("Starting setup. Windows will ask for administrator permission.", "Kurulum başlatılıyor; Windows yönetici izni isteyecek."));
            return await RunInstallerAsync(file, pkg, progress, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return Fail($"Installation failed: {Text.English(e)}", $"Kurulum başarısız: {e.Message}");
        }
    }

    private static async Task DownloadAsync(DriverPackage pkg, string file, IProgress<string>? progress, CancellationToken ct)
    {
        TryDelete(file);
        using HttpResponseMessage resp = await Http.GetAsync(pkg.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.RequestMessage?.RequestUri?.Scheme is { } s && s != Uri.UriSchemeHttps)
            throw new DownloadFailure(new("The download redirected to a non-HTTPS address.", "HTTPS olmayan bir adrese yönlendirildi."));
        await using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long total = 0;
        int lastPercent = -1;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > pkg.Size)
                throw new DownloadFailure(new("The download is larger than expected.", "İndirilen dosya beklenenden büyük."));
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            int percent = (int)(total * 100 / pkg.Size);
            if (percent / 10 != lastPercent / 10)
            {
                lastPercent = percent;
                progress?.Report(Text.Pick($"Downloading… {percent}%", $"İndiriliyor… %{percent}"));
            }
        }
    }

    /// <summary>Size, SHA-256, Authenticode and (if pinned) signer. Any failure deletes the file.</summary>
    internal static LocalizedMessage? VerifyOrDelete(string file, DriverPackage pkg)
    {
        LocalizedMessage? error = Verify(file, pkg);
        if (error is not null)
            TryDelete(file);
        return error;
    }

    private static LocalizedMessage? Verify(string file, DriverPackage pkg)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != pkg.Size)
            return new LocalizedMessage("The download size did not match. The file was deleted.", "İndirilen dosyanın boyutu beklenenden farklı; dosya silindi.");
        string hash;
        using (FileStream fs = File.OpenRead(file))
            hash = Convert.ToHexString(SHA256.HashData(fs));
        if (!string.Equals(hash, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
            return new LocalizedMessage("The SHA-256 checksum did not match. The file was deleted.", "İndirilen dosyanın SHA-256 özeti eşleşmiyor; dosya silindi.");
        if (!Authenticode.IsTrusted(file))
            return new LocalizedMessage("The file's digital signature is invalid. The file was deleted.", "Dosyanın dijital imzası geçerli değil; dosya silindi.");
        if (pkg.ExpectedSigner is { } signer && !Authenticode.SignerMatches(file, signer))
            return new LocalizedMessage("The file was signed by an unexpected publisher. The file was deleted.", "Dosya beklenen yayıncı tarafından imzalanmamış; dosya silindi.");
        return null;
    }

    private static async Task<InstallResult> RunInstallerAsync(string file, DriverPackage pkg, IProgress<string>? progress, CancellationToken ct)
    {
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(file, pkg.SilentArgs) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Native.ErrorCancelled)
        {
            return Fail("Administrator permission was declined.", "Yönetici izni verilmedi.");
        }

        if (p is null)
            return Fail("Could not start setup.", "Kurulum başlatılamadı.");
        using (p)
        {
            progress?.Report(Text.Pick("Installing…", "Kurulum sürüyor…"));
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Fail("Setup is still running in the background. Restart your PC when it finishes.", "Kurulum arka planda sürüyor; bitince bilgisayarı yeniden başlatın.");
            }

            int code = p.ExitCode;
            TryDelete(file);
            return code is 0 or 3010 or 1641
                ? new InstallResult(true, true, Text.Pick($"{pkg.Name} was installed. Restart your PC to enable the driver.", $"{pkg.Name} kuruldu. Sürücünün çalışması için bilgisayarı yeniden başlatın."))
                : Fail($"Installation failed (exit code {code}).", $"Kurulum başarısız oldu (çıkış kodu {code}).");
        }
    }

    private static InstallResult Fail(string en, string tr) =>
        new(false, false, Text.Pick(en, tr)) { Translation = new(en, tr) };

    private sealed class DownloadFailure(LocalizedMessage translation) : LocalizedException(translation, null);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x80;

    public static bool IsTrusted(string file)
    {
        nint path = Marshal.StringToHGlobalUni(file);
        nint fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new Native.WinTrustFileInfo { Size = Marshal.SizeOf<Native.WinTrustFileInfo>(), FilePath = path }, fileInfo, false);
            var data = new Native.WinTrustData
            {
                Size = Marshal.SizeOf<Native.WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfo,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdRevocationCheckChainExcludeRoot,
            };
            Guid action = GenericVerifyV2;
            int result = Native.WinVerifyTrust(-1, ref action, ref data);
            data.StateAction = WtdStateActionClose;
            Native.WinVerifyTrust(-1, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(path);
        }
    }

    /// <summary>Matches the signer's common name, or the full subject string.</summary>
    public static bool SignerMatches(string file, string expected)
    {
        try
        {
#pragma warning disable SYSLIB0057 // no loader replacement exists for reading the signer of a signed PE file
            using X509Certificate cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            using var c2 = new X509Certificate2(cert);
            return string.Equals(c2.GetNameInfo(X509NameType.SimpleName, false), expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c2.Subject, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
