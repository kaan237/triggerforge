using Tactile.Drivers;

namespace Tactile.Tests.Drivers;

internal sealed class FakeVhci(List<string> log) : IVhciControl
{
    public int Interfaces { get; set; } = 1;
    public List<ImportedDevice> Imported { get; } = [];
    public int NextPort { get; set; } = 5;
    public bool FailPlugIn { get; set; }
    public bool FailStop { get; set; }
    public HashSet<int> FailPlugOut { get; } = [];
    public Action? DuringPlugIn { get; set; }

    public int InterfaceCount() => Interfaces;

    public int PlugIn(string host, string service, string busId)
    {
        log.Add($"plugin {host}:{service}/{busId}");
        DuringPlugIn?.Invoke();
        if (FailPlugIn)
            throw new DriverException("fail");
        int port = NextPort++;
        Imported.Add(new ImportedDevice(port, busId, service, host, 0, 0, 0));
        return port;
    }

    public bool PlugOut(int hubPort)
    {
        log.Add($"plugout {hubPort}");
        if (FailPlugOut.Contains(hubPort))
            throw new DriverException("fail");
        return Imported.RemoveAll(d => d.Port == hubPort) > 0;
    }

    public IReadOnlyList<ImportedDevice> GetImported()
    {
        log.Add("list");
        return [.. Imported];
    }

    public int StopAttachAttempts(string host, string service, string busId)
    {
        log.Add($"stop {host}:{service}/{busId}");
        if (FailStop)
            throw new DriverException("fail");
        return 0;
    }
}

internal sealed class FakeHidHide(List<string> log) : IHidHideControl
{
    public List<string> Blacklist { get; } = [];
    public List<string> Whitelist { get; } = [];
    public bool Active { get; set; }
    public bool Available { get; set; } = true;
    public bool FailSetBlacklist { get; set; }
    public int OpenHandles { get; private set; }

    public HidHideProbe Probe() => Available ? HidHideProbe.Ok : HidHideProbe.NotFound;

    public IHidHideSession? Open()
    {
        if (!Available)
            return null;
        if (OpenHandles > 0)
            throw new InvalidOperationException("HidHide allows one handle.");
        OpenHandles++;
        log.Add("hh open");
        return new Session(this, log);
    }

    private sealed class Session(FakeHidHide o, List<string> log) : IHidHideSession
    {
        public List<string> GetBlacklist() => [.. o.Blacklist];

        public void SetBlacklist(IReadOnlyList<string> list)
        {
            log.Add("hh black " + string.Join(",", list));
            if (o.FailSetBlacklist)
                throw new DriverException("fail");
            o.Blacklist.Clear();
            o.Blacklist.AddRange(list);
        }

        public List<string> GetWhitelist() => [.. o.Whitelist];

        public void SetWhitelist(IReadOnlyList<string> list)
        {
            log.Add("hh white " + string.Join(",", list));
            o.Whitelist.Clear();
            o.Whitelist.AddRange(list);
        }

        public bool GetActive() => o.Active;

        public void SetActive(bool active)
        {
            log.Add("hh active " + active);
            o.Active = active;
        }

        public void Dispose()
        {
            o.OpenHandles--;
            log.Add("hh close");
        }
    }
}

internal sealed class DriverRig : IDisposable
{
    public const string Exe = @"\Device\HarddiskVolume3\Tactile\Tactile.exe";

    public DriverRig(TempDir? dir = null)
    {
        _ownsDir = dir is null;
        Dir = dir ?? new TempDir();
        Journal = new RecoveryJournal(JournalPath);
        VhciControl = new FakeVhci(Log);
        HidHide = new FakeHidHide(Log);
        Vhci = new Vhci(VhciControl, Journal);
        Hider = new DeviceHider(HidHide, Journal, () => Exe);
    }

    private readonly bool _ownsDir;

    public TempDir Dir { get; }
    public string JournalPath => Path.Combine(Dir.Path, "recovery.json");
    public List<string> Log { get; } = [];
    public RecoveryJournal Journal { get; }
    public FakeVhci VhciControl { get; }
    public FakeHidHide HidHide { get; }
    public Vhci Vhci { get; }
    public DeviceHider Hider { get; }

    public Recovery NewRecovery() => new(Journal, Vhci, Hider);

    public JournalData Read()
    {
        using RecoveryJournal.Transaction tx = Journal.Begin();
        return tx.Data;
    }

    public void Dispose()
    {
        Journal.Dispose();
        if (_ownsDir)
            Dir.Dispose();
    }
}
