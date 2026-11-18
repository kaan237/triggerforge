using System.Text.Json;

namespace Tactile.Core;

/// <summary>Profiles on disk, one file per profile in <c>profiles\&lt;id&gt;.json</c>. Thread-safe.</summary>
public sealed class ProfileStore
{
    public const string DefaultName = "Varsayılan";

    private readonly Lock _lock = new();
    private readonly string _dir;
    private readonly Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);
    private readonly List<string> _broken = [];
    private IReadOnlyList<Profile> _sorted = [];

    public ProfileStore(string? dataDir = null)
    {
        _dir = Path.Combine(JsonFile.ResolveDataDir(dataDir), "profiles");
        Directory.CreateDirectory(_dir);
        Load();
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
            if (_profiles.Count <= 1 || !_profiles.Remove(id))
            {
                return false;
            }

            File.Delete(PathOf(id));
            Resort();
        }

        Changed?.Invoke();
        return true;
    }

    public Profile Duplicate(string id, string newName)
    {
        Profile source = Get(id) ?? throw new KeyNotFoundException($"Profil bulunamadı: {id}");
        return Save(source with { Id = Validation.NewId(), Name = newName });
    }

    /// <exception cref="ArgumentException">The text is not a profile.</exception>
    public Profile Import(string json)
    {
        Profile? p;
        try
        {
            p = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"Geçersiz profil dosyası: {e.Message}", nameof(json), e);
        }

        if (p is null)
        {
            throw new ArgumentException("Geçersiz profil dosyası.", nameof(json));
        }

        return Save(p with { Id = Validation.NewId() });
    }

    public string Export(string id)
    {
        Profile p = Get(id) ?? throw new KeyNotFoundException($"Profil bulunamadı: {id}");
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
            Profile p = Validation.Normalize(new Profile { Id = Validation.NewId(), Name = DefaultName });
            JsonFile.WriteAtomic(PathOf(p.Id), p);
            _profiles[p.Id] = p;
        }

        Resort();
    }

    private void Resort() =>
        _sorted = _profiles.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();
}
