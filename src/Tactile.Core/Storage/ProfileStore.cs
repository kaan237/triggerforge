using System.Text.Json;

namespace Tactile.Core;

/// <summary>Profiles on disk, one file per profile in <c>profiles\&lt;id&gt;.json</c>. Thread-safe.</summary>
public sealed class ProfileStore
{
    public static string DefaultName => Text.Pick("Default", "Varsayılan");

    private readonly Lock _lock = new();
    private readonly string _dir;
    private readonly string _dataDir;
    private readonly Func<int> _backupCount;
    private readonly Func<DateOnly> _today;
    private readonly Action<Notice>? _notice;
    private (DateOnly Date, int Count)? _lastBackup;
    private readonly Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);
    private readonly List<string> _broken = [];
    private IReadOnlyList<Profile> _sorted = [];

    public ProfileStore(string? dataDir = null) : this(dataDir, () => 7) { }

    internal ProfileStore(string? dataDir, Func<int> backupCount, Func<DateOnly>? today = null, Action<Notice>? notice = null)
    {
        _dataDir = JsonFile.ResolveDataDir(dataDir);
        _dir = Path.Combine(_dataDir, "profiles");
        _backupCount = backupCount;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _notice = notice;
        Directory.CreateDirectory(_dir);
        Load();
        EnsureDailyBackup();
    }

    public string ProfilesDirectory => _dir;

    /// <summary>Sorted by name.</summary>
    public IReadOnlyList<Profile> All
    {
        get
        {
            lock (_lock)
            {
                return _sorted;
            }
        }
    }

    /// <summary>Files that failed to parse at load, after being renamed to <c>*.broken-*</c>.</summary>
    public IReadOnlyList<string> BrokenFiles
    {
        get
        {
            lock (_lock)
            {
                return _broken.ToArray();
            }
        }
    }

    public event Action? Changed;

    public Profile? Get(string id)
    {
        lock (_lock)
        {
            return _profiles.GetValueOrDefault(id);
        }
    }

    /// <summary>Validates and clamps, then writes. A profile without a valid id gets a new one.</summary>
    public Profile Save(Profile p) => Save(p, out _);

    /// <param name="issues">What validation changed, for showing to the user.</param>
    public Profile Save(Profile p, out IReadOnlyList<string> issues)
    {
        var list = new List<string>();
        Profile stored = Validation.Normalize(p, list);
        issues = list;
        lock (_lock)
        {
            EnsureDailyBackup();
            JsonFile.WriteAtomic(PathOf(stored.Id), stored);
            _profiles[stored.Id] = stored;
            Resort();
        }

        Changed?.Invoke();
        return stored;
    }

    /// <summary>False when the id is unknown or it is the last profile.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (_profiles.Count <= 1 || !_profiles.ContainsKey(id))
            {
                return false;
            }

            EnsureDailyBackup(required: true);
            File.Delete(PathOf(id));
            _profiles.Remove(id);
            Resort();
        }

        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<ProfileBackupEntry> ListBackups()
    {
        lock (_lock)
        {
            return ProfileBackup.ListBackups(_dataDir);
        }
    }

    public int RestoreBackup(string id)
    {
        int restored;
        lock (_lock)
        {
            restored = ProfileBackup.RestoreBackup(_dataDir, _dir, id, ReloadRestored);
        }

        Changed?.Invoke();
        return restored;
    }

    private void ReloadRestored()
    {
        var restored = new Dictionary<string, Profile>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(_dir, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            Profile? raw;
            try
            {
                raw = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), TactileJson.Options);
            }
            catch (JsonException e)
            {
                string name = Path.GetFileName(path);
                throw new LocalizedMessage($"Corrupt backup file: {name}", $"Bozuk yedek dosyası: {name}")
                    .CreateException(message => new JsonException(message, e));
            }
            if (!Validation.IsId(id) || raw is null)
            {
                throw new LocalizedMessage($"Invalid profile in backup: {Path.GetFileName(path)}",
                    $"Yedekteki profil geçersiz: {Path.GetFileName(path)}")
                    .CreateException(message => new InvalidDataException(message));
            }

            restored.Add(id, Validation.Normalize(raw with { Id = id }));
        }

        if (restored.Count == 0)
        {
            Profile fallback = CreateDefault();
            restored.Add(fallback.Id, fallback);
        }

        _profiles.Clear();
        foreach ((string id, Profile profile) in restored)
        {
            _profiles.Add(id, profile);
        }

        _broken.Clear();
        Resort();
    }

    private void EnsureDailyBackup(bool required = false)
    {
        var requested = (_today(), _backupCount());
        if (_lastBackup == requested)
        {
            return;
        }

        try
        {
            ProfileBackup.EnsureDailyBackup(_dataDir, _dir, requested.Item2, requested.Item1);
            _lastBackup = requested;
        }
        catch (Exception error) when (!required && _notice is not null && error is (IOException or UnauthorizedAccessException))
        {
            _notice(new Notice(NoticeKinds.Info, $"Profile backup failed: {Text.English(error)}", $"Profil yedeği alınamadı: {error.Message}"));
        }
    }

    public Profile Duplicate(string id, string newName)
    {
        Profile source = Get(id) ?? throw new LocalizedMessage($"Profile not found: {id}", $"Profil bulunamadı: {id}").CreateException(message => new KeyNotFoundException(message));
        return Save(source with { Id = Validation.NewId(), Name = newName });
    }

    /// <exception cref="ArgumentException">The text is not a profile.</exception>
    public Profile Import(string json) => Import(json, out _);

    /// <summary>Like <see cref="Import(string)"/>, but every <c>run</c> action becomes <c>none</c>: a shared profile must not start programs.</summary>
    /// <param name="removedRunActions">How many run actions were replaced.</param>
    /// <exception cref="ArgumentException">The text is not a profile.</exception>
    public Profile Import(string json, out int removedRunActions)
    {
        Profile? p;
        try
        {
            p = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options);
        }
        catch (JsonException e)
        {
            throw new LocalizedMessage($"Invalid profile file: {e.Message}", $"Geçersiz profil dosyası: {e.Message}").CreateException(message => new ArgumentException(message, nameof(json), e));
        }

        if (p is null)
        {
            throw new LocalizedMessage("Invalid profile file.", "Geçersiz profil dosyası.").CreateException(message => new ArgumentException(message, nameof(json)));
        }

        Profile cleaned = Validation.StripRunActions(Validation.Normalize(p with { Id = Validation.NewId() }), out removedRunActions);
        return Save(cleaned);
    }

    public string Export(string id)
    {
        Profile p = Get(id) ?? throw new LocalizedMessage($"Profile not found: {id}", $"Profil bulunamadı: {id}").CreateException(message => new KeyNotFoundException(message));
        return JsonSerializer.Serialize(p, TactileJson.Options);
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    private void Load()
    {
        foreach (string path in Directory.GetFiles(_dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Profile? raw = JsonFile.Read<Profile>(path, out string? broken);
            if (raw is null)
            {
                if (broken is not null)
                {
                    _broken.Add(broken);
                }

                continue;
            }

            // The file name is the id. A file copied in under another name keeps its own id if free.
            string stem = Path.GetFileNameWithoutExtension(path);
            string id = Validation.IsId(stem) ? stem : raw.Id;
            if (!Validation.IsId(id) || _profiles.ContainsKey(id))
            {
                id = Validation.NewId();
            }

            Profile p = Validation.Normalize(raw with { Id = id });
            _profiles[p.Id] = p;
            if (stem != p.Id)
            {
                JsonFile.WriteAtomic(PathOf(p.Id), p);
                File.Delete(path);
            }
        }

        if (_profiles.Count == 0)
        {
            Profile p = CreateDefault();
            _profiles[p.Id] = p;
        }

        Resort();
    }

    private Profile CreateDefault()
    {
        Profile profile = Validation.Normalize(new Profile { Id = Validation.NewId(), Name = DefaultName });
        JsonFile.WriteAtomic(PathOf(profile.Id), profile);
        return profile;
    }

    private void Resort() =>
        _sorted = _profiles.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();
}
