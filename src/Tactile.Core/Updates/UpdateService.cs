using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tactile.Core.Logging;

namespace Tactile.Core.Updates;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

public sealed record UpdateStatus(
    UpdateState State, string Current, string? Latest, string? Notes, string? Released, double? Progress,
    bool Portable, UpdateError? Error, string? Message, string? LastCheck);

/// <summary>Called on the host UI thread. Only byte counts are reported from the transfer thread.</summary>
public sealed class UpdateService : IDisposable
{
    private readonly UpdateClient _client;
    private readonly string _directory;
    private readonly Version _current;
    private readonly bool _portable;
    private readonly Func<string, bool> _signerMatches;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateState _state;
    private UpdateManifest? _manifest;
    private UpdateError? _error;
    private string? _lastCheck;
    private long _received;
    private bool _installing;
    private bool _disposed;

    public UpdateService(AppPaths paths, Version runningVersion)
        : this(Path.Combine(Path.GetDirectoryName(paths.LogDirectory)!, "updates"), paths.Portable, runningVersion,
            new UpdateClient(ComparableVersion(runningVersion).ToString()), SignerMatches) { }

    internal UpdateService(string directory, bool portable, Version runningVersion, UpdateClient client,
        Func<string, bool> signerMatches)
    {
        _directory = directory;
        _portable = portable;
        _current = ComparableVersion(runningVersion);
        _client = client;
        _signerMatches = signerMatches;
    }

    public event Action? Changed;
    public event Action<Notice>? Notice;
    public bool Busy => _installing || _state is UpdateState.Checking or UpdateState.Downloading;

    public UpdateStatus Status => new(_state, _current.ToString(), _manifest?.Version, _manifest?.Notes.Value,
        _manifest?.Released, _state == UpdateState.Downloading && _manifest is { } manifest
            ? Math.Clamp((double)Interlocked.Read(ref _received) / manifest.Installer.Size, 0, 1) : null,
        _portable, _error, ErrorMessage(_error), _lastCheck);

    public async Task CheckAsync()
    {
        if (_disposed || Busy)
        {
            Log.Info("Ignored update.check while an update operation was in progress or the app was exiting.");
            return;
        }

        CancellationToken token = _lifetime.Token;
        _manifest = null;
        SetState(UpdateState.Checking);
        try
        {
            await Task.Run(CleanVersionDirectories, token);
            UpdateManifest manifest = await _client.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            _manifest = manifest;
            _lastCheck = CheckTime();
            SetState(ComparableVersion(manifest.Number) > _current ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (Exception e) when (IsUpdateFailure(e, token))
        {
            _lastCheck = CheckTime();
            Fail(e, "check");
        }
    }

    public async Task DownloadAsync()
    {
        if (_disposed || _portable || _state != UpdateState.Available || _manifest is not { } manifest)
        {
            Log.Info("Ignored update.download outside the available installed update state.");
            return;
        }

        CancellationToken token = _lifetime.Token;
        Interlocked.Exchange(ref _received, 0);
        SetState(UpdateState.Downloading);
        try
        {
            await SaveInstallerAsync(manifest, token);
            token.ThrowIfCancellationRequested();
            SetState(UpdateState.Ready);
        }
        catch (Exception e) when (IsUpdateFailure(e, token))
        {
            Fail(e, "download");
        }
    }

    public async Task<bool> InstallAsync(Action<string> launch)
    {
        if (_disposed || Busy || _portable || _state != UpdateState.Ready || _manifest is not { } manifest)
        {
            Log.Info("Ignored update.install outside the ready installed update state.");
            return false;
        }

        _installing = true;
        CancellationToken token = _lifetime.Token;
        bool started = false;
        try
        {
            string path = InstallerPath(manifest);
            // Hold the verified file open without write or delete sharing until the shell starts it.
            using FileStream verified = await OpenVerifiedAsync(path, manifest.Installer, token);
            token.ThrowIfCancellationRequested();
            launch(path);
            started = true;
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            Log.Info("Update installation was cancelled at the UAC prompt.");
            SetState(UpdateState.Ready);
            Notice?.Invoke(new Notice(NoticeKinds.Info, "Update was cancelled.", "Güncelleme iptal edildi."));
        }
        catch (Exception e) when (IsUpdateFailure(e, token))
        {
            if (e is UpdateException { Code: UpdateError.BadFile or UpdateError.BadSigner })
            {
                DeleteInstaller(InstallerPath(manifest));
            }

            Fail(e, "install");
        }
        finally
        {
            _installing = started;
        }

        return false;
    }

    private async Task SaveInstallerAsync(UpdateManifest manifest, CancellationToken token)
    {
        string installer = InstallerPath(manifest);
        string directory = Path.GetDirectoryName(installer)!;
        Directory.CreateDirectory(_directory);
        RejectReparsePoint(_directory);
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);

        string partial = installer + ".partial";
        bool created = false;
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                created = true;
                await _client.DownloadAsync(manifest.Installer, file, bytes => Interlocked.Exchange(ref _received, bytes), token)
                    .ConfigureAwait(false);
            }

            using (await OpenVerifiedAsync(partial, manifest.Installer, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
            }

            File.Move(partial, installer);
            created = false;
        }
        finally
        {
            if (created)
            {
                DeleteInstaller(partial);
            }
        }
    }

    private async Task<FileStream> OpenVerifiedAsync(string path, UpdateAsset asset, CancellationToken token)
    {
        RejectReparsePoint(_directory);
        RejectReparsePoint(Path.GetDirectoryName(path)!);
        RejectReparsePoint(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (file.Length != asset.Size ||
                !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token).ConfigureAwait(false), asset.Sha256))
            {
                throw new UpdateException(UpdateError.BadFile, "The installer size or hash did not match the manifest.");
            }

            if (!_signerMatches(path))
            {
                throw new UpdateException(UpdateError.BadSigner, "The installer certificate did not match the pinned signer.");
            }

            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private string InstallerPath(UpdateManifest manifest) =>
        Path.Combine(_directory, manifest.Version, manifest.Installer.Name);

    private void CleanVersionDirectories()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        RejectReparsePoint(_directory);
        foreach (DirectoryInfo directory in new DirectoryInfo(_directory).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) == 0 && Version.TryParse(directory.Name, out _))
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("An update path points to another location.");
        }
    }

    private static void DeleteInstaller(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Update cleanup failed (badFile, {e.GetType().Name}).");
        }
    }

    private static bool SignerMatches(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Reading the signer of a PE file requires this API.
            using X509Certificate signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return string.Equals(signer.GetCertHashString(), UpdateKey.InstallerSignerThumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static Version ComparableVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string CheckTime() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static bool IsUpdateFailure(Exception e, CancellationToken token) =>
        e is UpdateException or IOException or UnauthorizedAccessException or Win32Exception ||
        (token.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException);

    private void Fail(Exception error, string operation)
    {
        if (_disposed)
        {
            return;
        }

        UpdateError code = error is UpdateException update ? update.Code : UpdateError.BadFile;
        string wireCode = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(code.ToString());
        string detail = error is UpdateException ? error.Message : error.GetType().Name;
        Log.Warn($"Update {operation} failed ({wireCode}). {detail}");
        SetState(UpdateState.Error, code);
    }

    private void SetState(UpdateState state, UpdateError? error = null)
    {
        _state = state;
        _error = error;
        Changed?.Invoke();
    }

    private static string? ErrorMessage(UpdateError? error) => error switch
    {
        UpdateError.Network => Text.Pick("Could not connect to the update server.", "Güncelleme sunucusuna bağlanılamadı."),
        UpdateError.UntrustedHost => Text.Pick("The update address is not trusted.", "Güncelleme adresi güvenilir değil."),
        UpdateError.TooLarge => Text.Pick("The update file is too large.", "Güncelleme dosyası çok büyük."),
        UpdateError.BadSignature => Text.Pick("The update signature could not be verified.", "Güncelleme imzası doğrulanamadı."),
        UpdateError.BadManifest => Text.Pick("The update information is invalid.", "Güncelleme bilgileri geçersiz."),
        UpdateError.BadFile => Text.Pick("The installer could not be verified or started.", "Kurulum dosyası doğrulanamadı veya başlatılamadı."),
        UpdateError.BadSigner => Text.Pick("The installer signer could not be verified.", "Kurulum dosyasını imzalayan yayıncı doğrulanamadı."),
        _ => null,
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _client.Dispose();
            _lifetime.Dispose();
        }
    }
}
