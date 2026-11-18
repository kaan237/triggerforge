using Tactile.Drivers;

namespace Tactile.Tests.Drivers;

public class RecoveryTests
{
    private const string Pad = @"HID\VID_054C&PID_0CE6\7&1&0&0000";

    [Fact]
    public void Attach_is_journaled_before_the_ioctl_and_updated_after()
    {
        using var rig = new DriverRig();
        JournalData? during = null;
        rig.VhciControl.DuringPlugIn = () => during = rig.Read();

        int hub = rig.Vhci.Attach(50123, "1-1");

        Assert.NotNull(during);
        AttachEntry pending = Assert.Single(during.Attaches);
        Assert.Equal((50123, "1-1", 0), (pending.TcpPort, pending.BusId, pending.HubPort));
        Assert.Equal(hub, Assert.Single(rig.Read().Attaches).HubPort);
    }

    [Fact]
    public void Failed_attach_stops_retries_and_drops_the_entry()
    {
        using var rig = new DriverRig();
        rig.VhciControl.FailPlugIn = true;

        Assert.Throws<DriverException>(() => rig.Vhci.Attach(50123, "1-1"));

        Assert.Contains("stop 127.0.0.1:50123/1-1", rig.Log);
        Assert.Empty(rig.Read().Attaches);
    }

    [Fact]
    public void Detach_plugs_out_and_removes_the_entry()
    {
        using var rig = new DriverRig();
        int hub = rig.Vhci.Attach(50123, "1-1");

        rig.Vhci.Detach(hub);

        Assert.Contains($"plugout {hub}", rig.Log);
        Assert.False(File.Exists(rig.JournalPath));
    }

    [Fact]
    public void Hide_journals_first_and_keeps_foreign_entries()
    {
        using var rig = new DriverRig();
        rig.HidHide.Blacklist.Add("OTHER\\DEVICE");
        rig.HidHide.Whitelist.Add(@"\Device\HarddiskVolume1\steam.exe");

        Assert.True(rig.Hider.Hide(Pad));

        Assert.Equal(["OTHER\\DEVICE", Pad], rig.HidHide.Blacklist);
        Assert.Equal([@"\Device\HarddiskVolume1\steam.exe", DriverRig.Exe], rig.HidHide.Whitelist);
        Assert.True(rig.HidHide.Active);
        HidHideEntry h = rig.Read().HidHide!;
        Assert.True(Assert.Single(h.Devices).AddedToBlacklist);
        Assert.Equal(DriverRig.Exe, h.AddedWhitelistPath);
        Assert.True(h.TurnedActiveOn);
        // Whitelist before blacklist, one handle opened and closed.
        Assert.True(rig.Log.FindIndex(l => l.StartsWith("hh white", StringComparison.Ordinal)) < rig.Log.FindIndex(l => l.StartsWith("hh black", StringComparison.Ordinal)));
        Assert.Equal(0, rig.HidHide.OpenHandles);
    }

    [Fact]
    public void Unhide_removes_only_our_additions_and_restores_active()
    {
        using var rig = new DriverRig();
        rig.HidHide.Blacklist.Add("OTHER\\DEVICE");
        Assert.True(rig.Hider.Hide(Pad));
        rig.HidHide.Blacklist.Add("ADDED\\LATER");

        rig.Hider.Unhide(Pad);

        Assert.Equal(["OTHER\\DEVICE", "ADDED\\LATER"], rig.HidHide.Blacklist);
        Assert.Empty(rig.HidHide.Whitelist);
        Assert.False(rig.HidHide.Active);
        Assert.False(File.Exists(rig.JournalPath));
    }

    [Fact]
    public void Unhide_leaves_preexisting_state_alone()
    {
        using var rig = new DriverRig();
        rig.HidHide.Blacklist.Add(Pad.ToLowerInvariant());
        rig.HidHide.Whitelist.Add(DriverRig.Exe);
        rig.HidHide.Active = true;
        Assert.True(rig.Hider.Hide(Pad));
        Assert.DoesNotContain(rig.Log, l => l.StartsWith("hh black", StringComparison.Ordinal) || l.StartsWith("hh white", StringComparison.Ordinal) || l.StartsWith("hh active", StringComparison.Ordinal));

        rig.Hider.Unhide(Pad);

        Assert.Equal([Pad.ToLowerInvariant()], rig.HidHide.Blacklist);
        Assert.Equal([DriverRig.Exe], rig.HidHide.Whitelist);
        Assert.True(rig.HidHide.Active);
    }

    [Fact]
    public void Failed_hide_keeps_the_journal_for_restore()
    {
        using var rig = new DriverRig();
        rig.HidHide.FailSetBlacklist = true;

        Assert.False(rig.Hider.Hide(Pad));

        Assert.Single(rig.Read().HidHide!.Devices);
        Assert.True(rig.NewRecovery().UncleanShutdown);
    }

    [Fact]
    public void Restore_detaches_ours_then_unhides_and_never_touches_others()
    {
        using var dir = new TempDir();
        using (var app = new DriverRig(dir))
        {
            app.Vhci.Attach(50123, "1-1");
            app.Hider.Hide(Pad);
            app.Journal.Dispose();
        }

        // Fresh process after a crash: the driver retried and re-imported our pad, plus a foreign device.
        using var rig = new DriverRig(dir);
        rig.VhciControl.Imported.Add(new ImportedDevice(9, "1-1", "50123", "127.0.0.1", 0, 0, 0));
        rig.VhciControl.Imported.Add(new ImportedDevice(10, "1-1", "3240", "127.0.0.1", 0, 0, 0));
        rig.VhciControl.Imported.Add(new ImportedDevice(11, "1-1", "50123", "192.168.1.5", 0, 0, 0));
        rig.HidHide.Blacklist.Add(Pad);
        rig.HidHide.Whitelist.Add(DriverRig.Exe);
        rig.HidHide.Active = true;
        rig.HidHide.Blacklist.Add("USER\\ENTRY");

        Recovery recovery = rig.NewRecovery();
        Assert.True(recovery.UncleanShutdown);
        recovery.RestoreAll();

        Assert.Equal(
        [
            "stop 127.0.0.1:50123/1-1",
            "list",
            "plugout 9",
            "hh open",
            $"hh black USER\\ENTRY",
            "hh active False",
            "hh white ",
            "hh close",
        ], rig.Log);
        Assert.Equal([10, 11], rig.VhciControl.Imported.Select(d => d.Port));
        Assert.Equal(["USER\\ENTRY"], rig.HidHide.Blacklist);
        Assert.False(File.Exists(rig.JournalPath));
        Assert.False(rig.NewRecovery().UncleanShutdown);

        rig.Log.Clear();
        recovery.RestoreAll();
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void Restore_keeps_entries_whose_undo_failed()
    {
        using var rig = new DriverRig();
        rig.Vhci.Attach(50123, "1-1");
        rig.Hider.Hide(Pad);
        rig.VhciControl.FailStop = true;
        rig.HidHide.Available = false;

        rig.NewRecovery().RestoreAll();

        JournalData d = rig.Read();
        Assert.Single(d.Attaches);
        Assert.Single(d.HidHide!.Devices);
    }

    [Fact]
    public void Restore_skips_missing_drivers_safely()
    {
        using var rig = new DriverRig();
        rig.Vhci.Attach(50123, "1-1");
        rig.Log.Clear();
        rig.VhciControl.Interfaces = 0;

        rig.NewRecovery().RestoreAll();

        Assert.Empty(rig.Log);
        Assert.Single(rig.Read().Attaches);
    }

    [Fact]
    public void Restore_from_a_second_instance_while_the_app_runs()
    {
        using var app = new DriverRig();
        int hub = app.Vhci.Attach(50123, "1-1");
        using var second = new RecoveryJournal(app.JournalPath);
        var restorer = new Recovery(second, new Vhci(app.VhciControl, second), new DeviceHider(app.HidHide, second, () => DriverRig.Exe));

        restorer.RestoreAll();
        app.Vhci.Detach(hub); // the app's own later detach finds nothing and stays quiet

        Assert.False(File.Exists(app.JournalPath));
    }

    [Fact]
    public void Mark_clean_exit_clears_the_journal()
    {
        using var rig = new DriverRig();
        rig.Vhci.Attach(50123, "1-1");
        Recovery r = rig.NewRecovery();

        r.MarkCleanExit();

        Assert.False(File.Exists(rig.JournalPath));
        Assert.False(rig.NewRecovery().UncleanShutdown);
    }

    [Fact]
    public void Corrupt_journal_is_unclean_empty_and_kept_as_bad()
    {
        using var rig = new DriverRig();
        File.WriteAllText(rig.JournalPath, "{ not json");

        Recovery r = rig.NewRecovery();

        Assert.True(r.UncleanShutdown);
        Assert.True(rig.Read().IsEmpty);
        Assert.Equal("{ not json", File.ReadAllText(rig.JournalPath + ".bad"));
        r.RestoreAll();
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void Journal_round_trips_and_leaves_no_temp_file()
    {
        using var rig = new DriverRig();
        rig.Vhci.Attach(50123, "2-1");
        rig.Hider.Hide(Pad);

        using var other = new RecoveryJournal(rig.JournalPath);
        using RecoveryJournal.Transaction tx = other.Begin();
        Assert.Equal("2-1", Assert.Single(tx.Data.Attaches).BusId);
        Assert.Equal(Pad, Assert.Single(tx.Data.HidHide!.Devices).InstanceId);
        Assert.False(File.Exists(rig.JournalPath + ".tmp"));
    }
}
