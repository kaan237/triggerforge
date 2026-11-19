using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Updates;

namespace Tactile.Tests.Updates;

public sealed class UpdateTests
{
    [Fact]
    public async Task AcceptsSignedManifestAndCleansOldVersionDirectories()
    {
        using var fixture = new Fixture();
        string oldVersion = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "0.7.0")).FullName;
        string unrelated = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "unrelated")).FullName;
        using UpdateService service = fixture.Create();
        var states = new List<UpdateState>();
        service.Changed += () => states.Add(service.Status.State);

        await service.CheckAsync();

        Assert.Equal(new[] { UpdateState.Checking, UpdateState.Available }, states);
        Assert.Equal("0.8.0", service.Status.Current);
        Assert.Equal("0.9.0", service.Status.Latest);
        Assert.Equal("2026-12-01", service.Status.Released);
        Assert.NotNull(service.Status.Notes);
        Assert.EndsWith("Z", service.Status.LastCheck);
        Assert.Null(service.Status.Error);
        Assert.False(Directory.Exists(oldVersion));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal(new[] { UpdateKey.ManifestUrl, UpdateKey.ManifestUrl + ".sig" }, fixture.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOneFlippedByteBeforeShowingManifestDetails(bool flipManifest)
    {
        using var fixture = new Fixture();
        if (flipManifest)
        {
            fixture.Manifest[0] ^= 1;
        }
        else
        {
            fixture.Signature[0] ^= 1;
        }

        using UpdateService service = fixture.Create();
        await service.CheckAsync();

        AssertFailure(service, UpdateError.BadSignature);
        Assert.Null(service.Status.Latest);
        Assert.Null(service.Status.Notes);
        Assert.Null(service.Status.Released);
    }

    [Fact]
    public async Task RejectsMissingSignature()
    {
        using var fixture = new Fixture();
        fixture.OverrideResponse = uri => uri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        AssertFailure(service, UpdateError.BadSignature);
    }

    [Theory]
    [InlineData("https://example.com/manifest.json")]
    [InlineData("https://github.com.example.com/manifest.json")]
    [InlineData("http://github.com/manifest.json")]
    [InlineData("https://github.com:444/manifest.json")]
    [InlineData("https://user:password@github.com/manifest.json")]
    public async Task DoesNotFollowUntrustedRedirect(string target)
    {
        using var fixture = new Fixture();
        fixture.OverrideResponse = _ => Redirect(target);
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        AssertFailure(service, UpdateError.UntrustedHost);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task FollowsTrustedRedirectButStopsAfterFive()
    {
        using var fixture = new Fixture();
        const string target = "https://release-assets.githubusercontent.com/asset?token=value";
        fixture.OverrideResponse = _ => Redirect(target);
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        AssertFailure(service, UpdateError.Network);
        Assert.Equal(6, fixture.Requests.Count);
        Assert.All(fixture.Requests.Skip(1), request => Assert.Equal(target, request));
    }

    [Fact]
    public async Task ReportsConnectionFailureWithoutShowingReleaseDetails()
    {
        using var fixture = new Fixture();
        fixture.OverrideResponse = _ => throw new HttpRequestException("Connection failed.");
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        AssertFailure(service, UpdateError.Network);
        Assert.Null(service.Status.Latest);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RejectsOversizedResponses(bool signature, bool knownLength)
    {
        using var fixture = new Fixture();
        fixture.OverrideResponse = uri =>
        {
            if (signature != uri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal))
            {
                return null;
            }

            byte[] data = new byte[(signature ? 1024 : 64 * 1024) + 1];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = knownLength ? new ByteArrayContent(data) : new StreamContent(new UnseekableStream(data)),
            };
        };
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        AssertFailure(service, UpdateError.TooLarge);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("sha256")]
    public async Task RejectsInvalidAssetFieldsEvenWhenSigned(string invalidField)
    {
        using var fixture = new Fixture(invalidField: invalidField);
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        AssertFailure(service, UpdateError.BadManifest);
    }

    [Theory]
    [InlineData("0.7.9")]
    [InlineData("0.8.0")]
    [InlineData("0.8.0.9")]
    public async Task DoesNotOfferOlderOrEqualVersions(string version)
    {
        using var fixture = new Fixture(version);
        using UpdateService service = fixture.Create();

        await service.CheckAsync();

        Assert.Equal(UpdateState.UpToDate, service.Status.State);
    }

    [Fact]
    public async Task PortableOffersReleaseButNeverDownloadsOrInstalls()
    {
        using var fixture = new Fixture();
        using UpdateService service = fixture.Create(portable: true);

        await service.CheckAsync();
        await service.DownloadAsync();
        bool started = await service.InstallAsync(_ => throw new InvalidOperationException("Launch was called."));

        Assert.True(service.Status.Portable);
        Assert.False(started);
        Assert.Equal(UpdateState.Available, service.Status.State);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Theory]
    [InlineData(false, true, UpdateError.BadFile)]
    [InlineData(false, false, UpdateError.BadFile)]
    [InlineData(true, false, UpdateError.BadSigner)]
    public async Task RemovesPartialInstallerWhenVerificationFails(bool correctHash, bool wrongSize, UpdateError expected)
    {
        using var fixture = new Fixture();
        fixture.InstallerResponse = wrongSize ? [1, 2] : correctHash ? fixture.Installer : [3, 2, 1];
        using UpdateService service = fixture.Create(signerMatches: (_, _) => false);

        await service.CheckAsync();
        await service.DownloadAsync();

        AssertFailure(service, expected);
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RejectsInstallerOverTheSizeCapAndClearsPartialFile()
    {
        using var fixture = new Fixture();
        fixture.OverrideResponse = uri => uri.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
                {
                    Headers = { ContentLength = (300 * 1024 * 1024) + 1L },
                },
            } : null;
        using UpdateService service = fixture.Create();

        await service.CheckAsync();
        await service.DownloadAsync();

        AssertFailure(service, UpdateError.TooLarge);
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadsToFinalPathAndKeepsReadyAfterUacCancellation()
    {
        using var fixture = new Fixture();
        using UpdateService service = fixture.Create();
        Notice? notice = null;
        service.Notice += value => notice = value;

        await service.CheckAsync();
        await service.DownloadAsync();

        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.Equal(fixture.Installer, File.ReadAllBytes(fixture.InstallerPath));
        Assert.False(File.Exists(fixture.InstallerPath + ".partial"));
        Assert.False(await service.InstallAsync(_ => throw new Win32Exception(1223)));
        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.NotNull(notice);
        Assert.Equal("Update was cancelled.", notice.EnglishMessage);
        Assert.Contains(notice.Message, new[] { "Update was cancelled.", "Güncelleme iptal edildi." });
        Assert.True(await service.InstallAsync(path => Assert.Equal(fixture.InstallerPath, path)));
        Assert.False(await service.InstallAsync(_ => throw new InvalidOperationException("Launch was called twice.")));
    }

    [Fact]
    public async Task RejectsInstallerModifiedAfterDownload()
    {
        using var fixture = new Fixture();
        using UpdateService service = fixture.Create();
        await service.CheckAsync();
        await service.DownloadAsync();
        File.WriteAllBytes(fixture.InstallerPath, [3, 2, 1]);

        bool started = await service.InstallAsync(_ => throw new InvalidOperationException("Launch was called."));

        Assert.False(started);
        AssertFailure(service, UpdateError.BadFile);
        Assert.False(File.Exists(fixture.InstallerPath));
    }

    [Fact]
    public async Task CheckAndDownloadMessagesDoNotOverlapAnActiveCheck()
    {
        using var fixture = new Fixture();
        using UpdateService service = fixture.Create();
        var receivedChecking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (service.Status.State == UpdateState.Checking)
            {
                receivedChecking.SetResult();
            }
        };
        var unblockHttp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.HoldResponses = unblockHttp.Task;
        Task check = service.CheckAsync();
        try
        {
            await receivedChecking.Task;
            await service.CheckAsync();
            await service.DownloadAsync();
            Assert.Equal(UpdateState.Checking, service.Status.State);
        }
        finally
        {
            unblockHttp.SetResult();
            await check;
        }

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(UpdateState.Available, service.Status.State);
    }

    [Fact]
    public void SettingsKeepDefaultsForOldFilesAndRoundTripBothFields()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "settings.json"), """{"version":1,"updates":null}""");
        var settings = new SettingsStore(fixture.DirectoryPath);
        Assert.True(settings.Current.Updates.AutoCheck);
        Assert.False(settings.Current.FirstRunDone);

        settings.Save(settings.Current with { Updates = new UpdateSettings { AutoCheck = false }, FirstRunDone = true });
        var loaded = new SettingsStore(fixture.DirectoryPath);

        Assert.False(loaded.Current.Updates.AutoCheck);
        Assert.True(loaded.Current.FirstRunDone);
    }

    private static void AssertFailure(UpdateService service, UpdateError expected)
    {
        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Equal(expected, service.Status.Error);
        Assert.False(string.IsNullOrWhiteSpace(service.Status.Message));
    }

    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(target) },
    };

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _version;
        private string InstallerName => $"TriggerForge-{_version}-setup.exe";
        public string DirectoryPath { get; } =
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"update-test-{Guid.NewGuid():N}")).FullName;
        public string InstallerPath => Path.Combine(DirectoryPath, _version, InstallerName);
        public byte[] Installer { get; } = [1, 2, 3];
        public byte[] InstallerResponse { get; set; }
        public byte[] Manifest { get; }
        public byte[] Signature { get; }
        public List<string> Requests { get; } = [];
        public Func<Uri, HttpResponseMessage?>? OverrideResponse { get; set; }
        public Task HoldResponses { get; set; } = Task.CompletedTask;

        public Fixture(string version = "0.9.0", string? invalidField = null)
        {
            _version = version;
            InstallerResponse = Installer;
            string prefix = $"{UpdateKey.AssetPrefix}v{version}/";
            string hash = Convert.ToHexStringLower(SHA256.HashData(Installer));
            string portableName = $"TriggerForge-{version}-portable.zip";
            Manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                version,
                released = "2026-12-01",
                notes = new { en = "Release notes", tr = "Sürüm notları" },
                installer = new
                {
                    name = invalidField == "name" ? "other.exe" : InstallerName,
                    url = invalidField == "url" ? $"https://example.com/{InstallerName}" : prefix + InstallerName,
                    size = invalidField == "size" ? 0 : Installer.Length,
                    sha256 = invalidField == "sha256" ? "bad hash" : hash,
                },
                portable = new { name = portableName, url = prefix + portableName, size = 3, sha256 = hash },
            });
            Signature = _key.SignData(Manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        public UpdateService Create(bool portable = false, Func<FileStream, string, bool>? signerMatches = null) =>
            new(DirectoryPath, portable, new Version(0, 8, 0, 42),
                new UpdateClient("0.8.0", new FakeHttp(this), _key.ExportSubjectPublicKeyInfo()),
                signerMatches ?? ((_, _) => true));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("TriggerForge/0.8.0", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Uri url = request.RequestUri!;
            Requests.Add(url.AbsoluteUri);
            return OverrideResponse?.Invoke(url) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = url.AbsoluteUri switch
                {
                    UpdateKey.ManifestUrl => new ByteArrayContent(Manifest),
                    UpdateKey.ManifestUrl + ".sig" => new StringContent(Convert.ToBase64String(Signature)),
                    _ when url.AbsolutePath.EndsWith("/" + InstallerName, StringComparison.Ordinal) =>
                        new ByteArrayContent(InstallerResponse),
                    _ => throw new InvalidOperationException($"Unexpected update URL: {url}"),
                },
            };
        }

        private sealed class FakeHttp(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await fixture.HoldResponses.WaitAsync(cancellationToken);
                return fixture.Respond(request);
            }
        }

        public void Dispose()
        {
            _key.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
