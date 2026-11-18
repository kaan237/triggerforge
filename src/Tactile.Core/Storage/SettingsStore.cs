namespace Tactile.Core;

/// <summary><c>settings.json</c>. Thread-safe.</summary>
public sealed class SettingsStore
{
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly List<string> _broken = [];
    private AppSettings _current;

    public SettingsStore(string? dataDir = null)
    {
        _path = Path.Combine(JsonFile.ResolveDataDir(dataDir), "settings.json");
        AppSettings? loaded = null;
        if (File.Exists(_path))
        {
            loaded = JsonFile.Read<AppSettings>(_path, out string? broken);
            if (broken is not null)
            {
                _broken.Add(broken);
            }
        }

        _current = Validation.Normalize(loaded ?? new AppSettings());
    }

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

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

    public void Save(AppSettings s)
    {
        AppSettings stored = Validation.Normalize(s);
        lock (_lock)
        {
            JsonFile.WriteAtomic(_path, stored);
            _current = stored;
        }

        Changed?.Invoke();
    }
}
