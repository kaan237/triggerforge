using Tactile.Core;
namespace Tactile.Drivers;

/// <summary>
/// HidHide control: journal first, read-modify-write the lists, undo only our own additions.
/// The journal lock is held across each operation so a concurrent <c>--restore</c> cannot interleave.
/// </summary>
internal sealed class DeviceHider(IHidHideControl control, RecoveryJournal journal, Func<string?> ownImagePath) : IDeviceHider
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public bool Hide(string deviceInstanceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId))
            return false;
        string? exe = ownImagePath();
        if (exe is null)
            return false;

        using RecoveryJournal.Transaction tx = journal.Begin();
        using IHidHideSession? s = control.Open();
        if (s is null)
            return false;
        try
        {
            List<string> black = s.GetBlacklist();
            List<string> white = s.GetWhitelist();
            bool active = s.GetActive();

            HidHideEntry h = tx.Data.HidHide ??= new HidHideEntry();
            HiddenDevice? dev = h.Devices.Find(d => Ci.Equals(d.InstanceId, deviceInstanceId));
            if (dev is null)
                h.Devices.Add(dev = new HiddenDevice { InstanceId = deviceInstanceId });
            bool addBlack = !black.Contains(deviceInstanceId, Ci);
            bool addWhite = !white.Contains(exe, Ci);
            dev.AddedToBlacklist |= addBlack;
            if (addWhite && h.AddedWhitelistPath is null)
                h.AddedWhitelistPath = exe;
            h.TurnedActiveOn |= !active;
            tx.Save();

            // Whitelist before blacklisting so Tactile never loses the pad it reads.
            if (addWhite)
                s.SetWhitelist([.. white, exe]);
            if (addBlack)
                s.SetBlacklist([.. black, deviceInstanceId]);
            if (!active)
                s.SetActive(true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Unhide(string deviceInstanceId)
    {
        using RecoveryJournal.Transaction tx = journal.Begin();
        HidHideEntry? h = tx.Data.HidHide;
        HiddenDevice? dev = h?.Devices.Find(d => Ci.Equals(d.InstanceId, deviceInstanceId));
        if (h is null || dev is null)
            return;
        using IHidHideSession? s = control.Open()
            ?? throw new DriverException(new LocalizedMessage("HidHide is busy. Could not make the physical controller visible again.", "HidHide şu an meşgul; gerçek kontrolcü yeniden görünür yapılamadı."));
        try
        {
            Undo(s, h, [dev]);
            tx.Save();
        }
        catch (Exception e) when (e is not DriverException)
        {
            throw new DriverException(new LocalizedMessage("Could not make the physical controller visible again.", "Gerçek kontrolcü yeniden görünür yapılamadı."), e);
        }
    }

    internal void Restore(RecoveryJournal.Transaction tx)
    {
        if (tx.Data.HidHide is not { IsEmpty: false } h)
            return;
        using IHidHideSession? s = control.Open();
        if (s is null)
            return;
        Undo(s, h, [.. h.Devices]);
        if (h.IsEmpty)
            tx.Data.HidHide = null;
        tx.Save();
    }

    /// <summary>Removes the given devices; once none is left, also our whitelist entry and the active flag.</summary>
    private static void Undo(IHidHideSession s, HidHideEntry h, IReadOnlyList<HiddenDevice> devices)
    {
        var ours = devices.Where(d => d.AddedToBlacklist).Select(d => d.InstanceId).ToHashSet(Ci);
        if (ours.Count > 0)
        {
            List<string> black = s.GetBlacklist();
            if (black.RemoveAll(ours.Contains) > 0)
                s.SetBlacklist(black);
        }

        foreach (HiddenDevice d in devices)
            h.Devices.Remove(d);
        if (h.Devices.Count > 0)
            return;

        if (h.TurnedActiveOn)
        {
            s.SetActive(false);
            h.TurnedActiveOn = false;
        }

        if (h.AddedWhitelistPath is { } path)
        {
            List<string> white = s.GetWhitelist();
            if (white.RemoveAll(w => Ci.Equals(w, path)) > 0)
                s.SetWhitelist(white);
            h.AddedWhitelistPath = null;
        }
    }
}
