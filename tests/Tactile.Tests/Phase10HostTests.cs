using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Updates;
using Tactile.Host;
using Tactile.Input;

namespace Tactile.Tests;

[Collection("Localization")]
public class Phase10HostTests
{
    [Theory]
    [InlineData(Key.Up, 0x26)]
    [InlineData(Key.Down, 0x28)]
    [InlineData(Key.Left, 0x25)]
    [InlineData(Key.Right, 0x27)]
    [InlineData(Key.Home, 0x24)]
    [InlineData(Key.End, 0x23)]
    [InlineData(Key.PageUp, 0x21)]
    [InlineData(Key.PageDown, 0x22)]
    [InlineData(Key.Insert, 0x2D)]
    [InlineData(Key.Delete, 0x2E)]
    [InlineData(Key.PrintScreen, 0x2C)]
    [InlineData(Key.Menu, 0x5D)]
    [InlineData(Key.NumpadDivide, 0x6F)]
    [InlineData(Key.NumpadEnter, 0x0D)]
    [InlineData(Key.MediaPrevious, 0xB1)]
    [InlineData(Key.MediaNext, 0xB0)]
    [InlineData(Key.MediaStop, 0xB2)]
    [InlineData(Key.MediaPlayPause, 0xB3)]
    [InlineData(Key.VolumeMute, 0xAD)]
    [InlineData(Key.VolumeDown, 0xAE)]
    [InlineData(Key.VolumeUp, 0xAF)]
    public void Extended_hotkeys_use_fixed_virtual_key_codes(Key key, int expected) =>
        Assert.Equal((uint)expected, TactileApp.VirtualKey(key));

    [Theory]
    [InlineData(Language.En, "Corrupt backup file:", "Backup not found.")]
    [InlineData(Language.Tr, "Bozuk yedek dosyası:", "Yedek bulunamadı.")]
    public void Bridge_reports_localized_restore_errors_and_refreshes_backups(Language language, string corrupt, string missing)
    {
        using var dir = new TempDir();
        using AppCore core = CreateCore(dir);
        core.Settings.Save(core.Settings.Current with { Language = language });
        AppPaths paths = Paths(dir);
        using var updates = new UpdateService(paths, new Version(1, 1, 0));
        var messages = new List<string>();
        var bridge = new Bridge(core, null!, messages.Add, paths, updates);
        bridge.Handle("""{"type":"hello"}""", appSource: true);
        string snapshot = Path.Combine(dir.Path, "backups", "2025-01-01");
        Directory.CreateDirectory(snapshot);
        string file = core.ActiveProfile.Id + ".json";
        File.WriteAllText(Path.Combine(snapshot, file), "{ broken");

        messages.Clear();
        bridge.Handle("""{"type":"backups.restore","id":"2025-01-01"}""", appSource: true);
        Assert.Collection(messages,
            raw =>
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
                Assert.Equal($"{corrupt} {file}", doc.RootElement.GetProperty("message").GetString());
            },
            raw =>
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                Assert.Equal("backups", doc.RootElement.GetProperty("type").GetString());
                Assert.Contains(doc.RootElement.GetProperty("list").EnumerateArray(), entry =>
                    entry.GetProperty("id").GetString()!.StartsWith("pre-restore-", StringComparison.Ordinal) &&
                    entry.GetProperty("profileCount").GetInt32() == 1);
            });

        messages.Clear();
        bridge.Handle("""{"type":"backups.restore","id":"2025-01-02"}""", appSource: true);
        using JsonDocument error = JsonDocument.Parse(messages[0]);
        Assert.Equal(missing, error.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Ui_capture_clears_hotkey_keys_and_restores_latest_setting_on_resume_or_detach()
    {
        using var dir = new TempDir();
        using AppCore core = CreateCore(dir);
        core.Settings.Save(core.Settings.Current with { PauseHotkey = [Key.A] });
        AppPaths paths = Paths(dir);
        using var updates = new UpdateService(paths, new Version(1, 1, 0));
        var bridge = new Bridge(core, null!, _ => { }, paths, updates);
        var transitions = new List<Key[]>();
        core.UiMappingPausedChanged += () => transitions.Add(TactileApp.PauseHotkeyKeys(core).ToArray());
        Assert.Equal([Key.A], TactileApp.PauseHotkeyKeys(core));

        bridge.Handle("""{"type":"mapping.pause","paused":true}""", appSource: true);
        core.Settings.Save(core.Settings.Current with { PauseHotkey = [Key.B] });
        Assert.Empty(TactileApp.PauseHotkeyKeys(core));
        bridge.Handle("""{"type":"mapping.pause","paused":false}""", appSource: true);
        bridge.Handle("""{"type":"mapping.pause","paused":true}""", appSource: true);
        bridge.Detach();

        Assert.Collection(transitions, keys => Assert.Empty(keys), keys => Assert.Equal([Key.B], keys),
            keys => Assert.Empty(keys), keys => Assert.Equal([Key.B], keys));
        Assert.False(core.UserMappingPaused);
    }

    private static AppCore CreateCore(TempDir dir) =>
        new(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            new ManualTime(), runTimer: false, new FakePlatform().Platform);

    private static AppPaths Paths(TempDir dir) => new(dir.Path, dir.Path, dir.Path, dir.Path);
}
