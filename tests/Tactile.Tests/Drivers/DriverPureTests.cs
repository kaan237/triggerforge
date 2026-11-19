using Tactile.Core;
using System.Security.Cryptography;
using System.Text;
using Tactile.Drivers;

namespace Tactile.Tests.Drivers;

[Collection("Localization")]
public class DriverPureTests
{
    [Theory]
    [InlineData(DriverKind.UsbIp)]
    [InlineData(DriverKind.HidHide)]
    public void Manifest_entries_are_sane(DriverKind kind)
    {
        DriverPackage p = DriverManifest.Get(kind);
        Assert.Equal(kind, p.Kind);
        Assert.Equal(Uri.UriSchemeHttps, p.Url.Scheme);
        Assert.Equal("github.com", p.Url.Host);
        Assert.EndsWith("/" + p.FileName, p.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains(p.Version, p.FileName, StringComparison.Ordinal);
        Assert.True(p.Size > 1_000_000);
        Assert.Matches("^[0-9a-fA-F]{64}$", p.Sha256);
        Assert.False(string.IsNullOrWhiteSpace(p.SilentArgs));
        Assert.Contains("norestart", p.SilentArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_pins_the_versions_from_the_notes()
    {
        Assert.Equal("0.9.8.1", DriverManifest.UsbIp.Version);
        Assert.Equal("0.9.8.1", DriverManifest.UsbIp.RequiredVersion);
        Assert.Equal(26_083_632, DriverManifest.UsbIp.Size);
        Assert.Equal("1.5.230", DriverManifest.HidHide.Version);
        Assert.Equal(8_078_016, DriverManifest.HidHide.Size);
    }

    [Fact]
    public void UsbIp_status_requires_exactly_0981_and_one_interface()
    {
        Assert.Equal(DriverState.Ready, DriverManager.UsbIpStatus("0.9.8.1", 1).State);
        Assert.Equal(DriverState.Unsupported, DriverManager.UsbIpStatus("0.9.8.0", 1).State);
        Assert.Equal(DriverState.Unsupported, DriverManager.UsbIpStatus("0.9.8.2", 1).State);
        Assert.Equal(DriverState.Unsupported, DriverManager.UsbIpStatus(null, 1).State);
        Assert.Equal(DriverState.Missing, DriverManager.UsbIpStatus(null, 0).State);
        Assert.Equal(DriverState.Error, DriverManager.UsbIpStatus("0.9.8.1", 0).State);
        Assert.Equal(DriverState.Error, DriverManager.UsbIpStatus("0.9.8.1", 2).State);
    }

    [Fact]
    public void HidHide_status_from_probe()
    {
        Assert.Equal(DriverState.Ready, DriverManager.HidHideStatus(null, HidHideProbe.Ok).State);
        Assert.Equal("1.5.230", DriverManager.HidHideStatus("1.5.230", HidHideProbe.Ok).Version);
        Assert.Equal(DriverState.Missing, DriverManager.HidHideStatus(null, HidHideProbe.NotFound).State);
        Assert.Equal(DriverState.Error, DriverManager.HidHideStatus("1.5.230", HidHideProbe.NotFound).State);
        Assert.Equal(DriverState.Error, DriverManager.HidHideStatus("1.5.230", HidHideProbe.Busy).State);
    }

    [Fact]
    public void Cached_driver_detail_follows_the_language()
    {
        using var language = new TestLanguage(Language.En);
        DriverStatus status = DriverManager.UsbIpStatus(DriverManifest.UsbIp.RequiredVersion, 0);
        Assert.Contains("inactive", status.Detail, StringComparison.Ordinal);

        Text.Current = Language.Tr;
        Assert.Contains("etkin değil", status.Detail, StringComparison.Ordinal);
        Assert.Contains("inactive", status.EnglishDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_hash_fails_and_deletes_the_file()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "setup.exe");
        byte[] content = Encoding.ASCII.GetBytes("not the installer");
        File.WriteAllBytes(file, content);
        DriverPackage pkg = DriverManifest.UsbIp with { Size = content.Length };

        string? error = DriverManager.VerifyOrDelete(file, pkg)?.Value;

        Assert.NotNull(error);
        Assert.Contains("SHA-256", error, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Wrong_size_fails_and_deletes_the_file()
    {
        using var language = new TestLanguage(Language.Tr);
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "setup.exe");
        File.WriteAllBytes(file, [1, 2, 3]);

        Assert.Contains("boyut", DriverManager.VerifyOrDelete(file, DriverManifest.HidHide)?.Value, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Matching_hash_but_unsigned_fails_authenticode_and_deletes()
    {
        using var language = new TestLanguage(Language.Tr);
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "setup.exe");
        byte[] content = Encoding.ASCII.GetBytes("unsigned payload");
        File.WriteAllBytes(file, content);
        DriverPackage pkg = DriverManifest.HidHide with { Size = content.Length, Sha256 = Convert.ToHexString(SHA256.HashData(content)) };

        string? error = DriverManager.VerifyOrDelete(file, pkg)?.Value;

        Assert.NotNull(error);
        Assert.Contains("imza", error, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Signer_check_rejects_unsigned_file()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "x.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 0, 0]);
        Assert.False(Authenticode.SignerMatches(file, "Anyone"));
    }

    [Fact]
    public void Multi_sz_round_trip()
    {
        string[] items = [@"HID\VID_054C&PID_0CE6&MI_03\7&ABC&0&0000", @"\Device\HarddiskVolume3\Program Files\Tactile\Tactile.exe", "ü"];
        byte[] b = MultiSz.Encode(items);

        Assert.Equal(0, b.Length % 2);
        Assert.Equal([0, 0, 0, 0], b[^4..]);
        Assert.Equal(items, MultiSz.Decode(b));
    }

    [Fact]
    public void Multi_sz_empty_and_odd_buffers()
    {
        Assert.Equal([0, 0, 0, 0], MultiSz.Encode([]));
        Assert.Empty(MultiSz.Decode(MultiSz.Encode([])));
        Assert.Empty(MultiSz.Decode([]));
        Assert.Equal(["a"], MultiSz.Decode([(byte)'a', 0, 0, 0, 0]));
        Assert.Throws<ArgumentException>(() => MultiSz.Encode([""]));
    }

    [Fact]
    public void Multi_sz_decode_keeps_entries_after_a_stray_empty_string()
    {
        byte[] b = Encoding.Unicode.GetBytes("a\0\0b\0\0");
        Assert.Equal(["a", "b"], MultiSz.Decode(b));
    }

    [Fact]
    public void Nt_path_conversion()
    {
        static string? Dos(string d) => d switch
        {
            "C:" => @"\Device\HarddiskVolume3",
            "S:" => @"\??\C:\Games",
            _ => null,
        };

        Assert.Equal(@"\Device\HarddiskVolume3\Program Files\Tactile\Tactile.exe", NtPath.FromWin32(@"C:\Program Files\Tactile\Tactile.exe", Dos));
        Assert.Equal(@"\Device\HarddiskVolume3\Games\Tactile.exe", NtPath.FromWin32(@"S:\Tactile.exe", Dos));
        Assert.Null(NtPath.FromWin32(@"Z:\x.exe", Dos));
        Assert.Null(NtPath.FromWin32(@"\\server\share\x.exe", Dos));
    }

    [Fact]
    public void Instance_id_fallback_from_hid_path()
    {
        Assert.Equal(@"HID\VID_054C&PID_0CE6&MI_03\7&abc&0&0000",
            DeviceTree.InstanceIdFromPath(@"\\?\HID#VID_054C&PID_0CE6&MI_03#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.Null(DeviceTree.InstanceIdFromPath("nonsense"));
    }

    [Fact]
    public void Vhci_node_matches_by_instance_prefix_or_hardware_id()
    {
        Assert.True(DeviceTree.IsVhciNode(@"ROOT\USB\0000", [@"ROOT\USBIP_WIN2\UDE"]));
        Assert.True(DeviceTree.IsVhciNode(@"ROOT\USBIP_WIN2\0000", []));
        Assert.False(DeviceTree.IsVhciNode(@"USB\ROOT_HUB30\4&1", [@"USB\ROOT_HUB30"]));
        Assert.False(DeviceTree.IsVhciNode(null, []));
    }
}
