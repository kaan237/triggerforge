using System.Globalization;
using System.Text;
using System.Text.Json;
using Tactile.Core.Logging;

namespace Tactile.Core.Catalog;

public enum CatalogState { Idle, Loading, Ready, Importing, Error }

public sealed record CatalogStatus(
    CatalogState State, string? Updated, IReadOnlyList<CatalogEntry> Profiles, string? ImportingId,
    CatalogError? Error, string? Message);

public sealed record CatalogImport(Profile Profile, int RemovedRunActions);

/// <summary>
/// The community catalog. Nothing touches the network until a fetch or an import is asked for,
/// and only one of them runs at a time.
/// </summary>
public sealed class CatalogService : IDisposable
{
    private readonly CatalogClient _client;
    private readonly string _revisionFile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CatalogState _state = CatalogState.Idle;
    private string? _updated;
    private IReadOnlyList<CatalogEntry> _profiles = [];
    private string? _importingId;
    private CatalogError? _error;
    private bool _busy;
    private bool _disposed;

    public CatalogService(AppPaths paths, Version runningVersion)
        : this(new CatalogClient($"{runningVersion.Major}.{runningVersion.Minor}.{Math.Max(runningVersion.Build, 0)}"),
            Path.Combine(paths.DataDirectory, "catalog-revision.txt")) { }

    internal CatalogService(CatalogClient client, string revisionFile)
    {
        _client = client;
        _revisionFile = revisionFile;
    }

    public event Action? Changed;

    public CatalogStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new CatalogStatus(_state, _updated, _profiles, _importingId, _error, ErrorMessage(_error));
            }
        }
    }

    /// <summary>Loads the signed index. Does nothing when it is already loaded, unless forced.</summary>
    public async Task FetchAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_disposed || _busy || (_state == CatalogState.Ready && !force))
            {
                return;
            }

            _busy = true;
            _state = CatalogState.Loading;
            _importingId = null;
            _error = null;
        }

        Changed?.Invoke();
        CancellationToken token = _lifetime.Token;
        try
        {
            CatalogManifest manifest = await _client.FetchIndexAsync(token);
            long seen = ReadRevision();
            if (manifest.Revision < seen)
            {
                throw new CatalogException(CatalogError.Outdated, "The catalog is older than one already seen.");
            }

            if (manifest.Revision > seen)
            {
                WriteRevision(manifest.Revision);
            }

            lock (_gate)
            {
                _state = CatalogState.Ready;
                _updated = manifest.Updated;
                _profiles = manifest.Profiles;
            }
        }
        catch (CatalogException e)
        {
            Log.Info($"The community catalog could not be loaded ({e.Code}).");
            lock (_gate)
            {
                _state = CatalogState.Error;
                _updated = null;
                _profiles = [];
                _error = e.Code;
            }
        }
        catch (Exception e) when (token.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception e)
        {
            Log.Warn("The community catalog failed unexpectedly.", e);
            lock (_gate)
            {
                _state = CatalogState.Error;
                _updated = null;
                _profiles = [];
                _error = CatalogError.Failed;
            }
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Downloads one entry, checks it and saves it as a new profile. Null when it failed or the entry is unknown.</summary>
    public async Task<CatalogImport?> ImportAsync(string entryId, ProfileStore store)
    {
        CatalogEntry? entry;
        lock (_gate)
        {
            entry = _profiles.FirstOrDefault(p => p.Id == entryId);
            if (_disposed || _busy || entry is null || _state is not (CatalogState.Ready or CatalogState.Error))
            {
                return null;
            }

            _busy = true;
            _state = CatalogState.Importing;
            _importingId = entryId;
            _error = null;
        }

        Changed?.Invoke();
        CancellationToken token = _lifetime.Token;
        CatalogImport? result = null;
        try
        {
            byte[] bytes = await _client.FetchProfileAsync(entry, token);
            result = SaveProfile(bytes, store);
        }
        catch (CatalogException e)
        {
            Log.Info($"A community profile could not be imported ({e.Code}).");
            lock (_gate)
            {
                _error = e.Code;
            }
        }
        catch (Exception e) when (token.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
        catch (Exception e)
        {
            Log.Warn("A community profile could not be saved.", e);
            lock (_gate)
            {
                _error = e is IOException or UnauthorizedAccessException ? CatalogError.Storage : CatalogError.Failed;
            }
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
                _importingId = null;
                _state = _error is null ? CatalogState.Ready : CatalogState.Error;
            }
        }

        Changed?.Invoke();
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        _client.Dispose();
    }

    private static CatalogImport SaveProfile(byte[] bytes, ProfileStore store)
    {
        string json;
        try
        {
            if (CatalogProfileFile.ContainsRunAction(bytes))
            {
                throw new CatalogException(CatalogError.BadFile, "The profile file contains a run action.");
            }

            json = CatalogProfileFile.DecodeStrict(bytes);
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        {
            throw new CatalogException(CatalogError.BadFile, "The profile file is not valid.", e);
        }

        try
        {
            Profile profile = store.Import(json, out int removed);
            return new CatalogImport(profile, removed);
        }
        catch (ArgumentException e)
        {
            throw new CatalogException(CatalogError.BadFile, "The profile file is not a profile.", e);
        }
    }

    private long ReadRevision()
    {
        try
        {
            return long.TryParse(File.ReadAllText(_revisionFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n)
                ? n : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void WriteRevision(long revision)
    {
        string temp = _revisionFile + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_revisionFile)!);
            File.WriteAllText(temp, revision.ToString(CultureInfo.InvariantCulture));
            File.Move(temp, _revisionFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info("The last seen catalog revision could not be saved.");
        }
    }

    private static string? ErrorMessage(CatalogError? error) => error switch
    {
        CatalogError.Network => Text.Pick("Could not reach the community catalog.", "Topluluk kataloğuna ulaşılamadı."),
        CatalogError.UntrustedHost => Text.Pick("The catalog address is not trusted.", "Katalog adresi güvenilir değil."),
        CatalogError.TooLarge => Text.Pick("The catalog response is too large.", "Katalog yanıtı çok büyük."),
        CatalogError.BadSignature => Text.Pick("The catalog signature could not be verified.", "Katalog imzası doğrulanamadı."),
        CatalogError.BadManifest => Text.Pick("The catalog is not valid.", "Katalog geçerli değil."),
        CatalogError.BadFile => Text.Pick("The profile file does not match the catalog.", "Profil dosyası katalogla uyuşmuyor."),
        CatalogError.Outdated => Text.Pick("The catalog is older than one already seen.", "Katalog, daha önce görülenden eski."),
        CatalogError.Storage => Text.Pick("The profile could not be saved.", "Profil kaydedilemedi."),
        CatalogError.Failed => Text.Pick("The community catalog failed. See the log.", "Topluluk kataloğu başarısız oldu; günlüğe bakın."),
        _ => null,
    };
}
