using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tactile.Drivers;

internal sealed class JournalData
{
    public int Version { get; set; } = 1;
    public List<AttachEntry> Attaches { get; set; } = [];
    public HidHideEntry? HidHide { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Attaches.Count == 0 && (HidHide is null || HidHide.IsEmpty);
}

internal sealed class AttachEntry
{
    public string Id { get; set; } = "";
    public int TcpPort { get; set; }
    public string BusId { get; set; } = "";

    /// <summary>0 while the attach IOCTL is pending.</summary>
    public int HubPort { get; set; }

    [JsonIgnore]
    public string Service => TcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class HidHideEntry
{
    public List<HiddenDevice> Devices { get; set; } = [];

    /// <summary>Whitelist entry (NT path) we added; null if it was already there.</summary>
    public string? AddedWhitelistPath { get; set; }

    public bool TurnedActiveOn { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Devices.Count == 0 && AddedWhitelistPath is null && !TurnedActiveOn;
}

internal sealed class HiddenDevice
{
    public string InstanceId { get; set; } = "";
    public bool AddedToBlacklist { get; set; }
}

/// <summary>
/// recovery.json with a named mutex around every read-modify-write, so a second process
/// (<c>--restore</c>) can work on it while the app runs.
/// </summary>
internal sealed class RecoveryJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Mutex _mutex;

    public RecoveryJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.ToUpperInvariant()));
        _mutex = new Mutex(false, @"Local\Tactile.Recovery." + Convert.ToHexString(hash, 0, 8));
    }

    public string Path { get; }

    public string BadPath => Path + ".bad";

    public Transaction Begin()
    {
        try
        {
            _mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Previous holder died; we own the mutex now and the file is always replaced atomically.
        }

        try
        {
            (JournalData data, bool corrupt) = Read();
            return new Transaction(this, data, corrupt);
        }
        catch
        {
            _mutex.ReleaseMutex();
            throw;
        }
    }

    private (JournalData Data, bool Corrupt) Read()
    {
        if (!File.Exists(Path))
            return (new JournalData(), false);
        try
        {
            JournalData? d = JsonSerializer.Deserialize<JournalData>(File.ReadAllText(Path), Json);
            if (d is not null)
            {
                d.Attaches ??= [];
                d.Attaches.RemoveAll(a => a is null);
                if (d.HidHide is { } h)
                {
                    h.Devices ??= [];
                    h.Devices.RemoveAll(x => x is null);
                }

                return (d, false);
            }
        }
        catch (JsonException)
        {
        }

        File.Move(Path, BadPath, overwrite: true);
        return (new JournalData(), true);
    }

    private void Write(JournalData data)
    {
        if (data.IsEmpty)
        {
            File.Delete(Path);
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, data, Json);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, Path, overwrite: true);
    }

    public void Dispose() => _mutex.Dispose();

    public sealed class Transaction : IDisposable
    {
        private readonly RecoveryJournal _owner;
        private bool _done;

        internal Transaction(RecoveryJournal owner, JournalData data, bool corrupt)
        {
            _owner = owner;
            Data = data;
            WasCorrupt = corrupt;
        }

        public JournalData Data { get; }

        public bool WasCorrupt { get; }

        public void Save() => _owner.Write(Data);

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            _owner._mutex.ReleaseMutex();
        }
    }
}
