using System.Globalization;

namespace Tactile.Drivers;

internal sealed class Vhci(IVhciControl control, RecoveryJournal journal) : IVhci
{
    public int Attach(int tcpPort, string busId)
    {
        if (tcpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        if (control.InterfaceCount() != 1)
            throw new DriverException("usbip-win2 sürücüsü hazır değil.");

        string service = tcpPort.ToString(CultureInfo.InvariantCulture);
        var entry = new AttachEntry { Id = Guid.NewGuid().ToString("N"), TcpPort = tcpPort, BusId = busId };
        using (RecoveryJournal.Transaction tx = journal.Begin())
        {
            tx.Data.Attaches.Add(entry);
            tx.Save();
        }

        int hubPort;
        try
        {
            hubPort = control.PlugIn(VhciLayout.Host, service, busId);
        }
        catch
        {
            // A failed PLUGIN_HARDWARE leaves background retries queued; cancel them before forgetting the entry.
            try
            {
                control.StopAttachAttempts(VhciLayout.Host, service, busId);
                Remove(entry.Id);
            }
            catch (Exception)
            {
            }

            throw;
        }

        using (RecoveryJournal.Transaction tx = journal.Begin())
        {
            // Upsert: a concurrent --restore may have removed the pending entry meanwhile.
            AttachEntry? e = tx.Data.Attaches.Find(a => a.Id == entry.Id);
            if (e is null)
                tx.Data.Attaches.Add(e = entry);
            e.HubPort = hubPort;
            tx.Save();
        }

        return hubPort;
    }

    public void Detach(int hubPort)
    {
        AttachEntry? entry;
        using (RecoveryJournal.Transaction tx = journal.Begin())
            entry = tx.Data.Attaches.Find(a => a.HubPort == hubPort);

        try
        {
            control.PlugOut(hubPort);
        }
        finally
        {
            // Even when the device is already gone, the driver may be queuing reconnects to our port.
            if (entry is not null)
                control.StopAttachAttempts(VhciLayout.Host, entry.Service, entry.BusId);
        }

        if (entry is not null)
            Remove(entry.Id);
    }

    private void Remove(string id)
    {
        using RecoveryJournal.Transaction tx = journal.Begin();
        if (tx.Data.Attaches.RemoveAll(a => a.Id == id) > 0)
            tx.Save();
    }

    /// <summary>
    /// Stops retries and plugs out only devices whose location matches a journaled attach
    /// (host 127.0.0.1, our port, our busid). Entries go only after their undo succeeded.
    /// </summary>
    internal void Restore(RecoveryJournal.Transaction tx)
    {
        List<AttachEntry> entries = tx.Data.Attaches;
        if (entries.Count == 0 || control.InterfaceCount() != 1)
            return;

        var stopped = new List<AttachEntry>();
        foreach (AttachEntry e in entries)
        {
            try
            {
                control.StopAttachAttempts(VhciLayout.Host, e.Service, e.BusId);
                stopped.Add(e);
            }
            catch (Exception)
            {
            }
        }

        IReadOnlyList<ImportedDevice> imported;
        try
        {
            imported = control.GetImported();
        }
        catch (Exception)
        {
            return;
        }

        foreach (AttachEntry e in stopped)
        {
            bool ok = true;
            foreach (ImportedDevice d in imported)
            {
                if (d.Host != VhciLayout.Host || d.Service != e.Service || d.BusId != e.BusId)
                    continue;
                try
                {
                    control.PlugOut(d.Port);
                }
                catch (Exception)
                {
                    ok = false;
                }
            }

            if (ok)
                entries.Remove(e);
        }

        tx.Save();
    }
}
