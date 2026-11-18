namespace Tactile.Drivers;

internal sealed class Recovery : IRecovery
{
    private readonly RecoveryJournal _journal;
    private readonly Vhci _vhci;
    private readonly DeviceHider _hider;

    public Recovery(RecoveryJournal journal, Vhci vhci, DeviceHider hider)
    {
        _journal = journal;
        _vhci = vhci;
        _hider = hider;
        using RecoveryJournal.Transaction tx = journal.Begin();
        UncleanShutdown = tx.WasCorrupt || !tx.Data.IsEmpty;
    }

    public bool UncleanShutdown { get; }

    /// <summary>Virtual pads first so games drop them, then the real pad is shown again.</summary>
    public void RestoreAll()
    {
        using RecoveryJournal.Transaction tx = _journal.Begin();
        try
        {
            _vhci.Restore(tx);
        }
        catch (Exception)
        {
        }

        try
        {
            _hider.Restore(tx);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Undoes whatever is still applied; anything that fails to undo stays journaled for the next start.</summary>
    public void MarkCleanExit() => RestoreAll();
}
