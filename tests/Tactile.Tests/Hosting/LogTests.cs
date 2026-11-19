using System.IO.Compression;
using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Logging;

namespace Tactile.Tests.Hosting;

public sealed class LogTests
{
    [Fact]
    public void RotationKeepsOnlyCurrentAndTwoBackups()
    {
        using var dir = new TempDir();
        var log = new LogFile(dir.Path, "", "");
        string payload = new('a', 600_000);
        for (int i = 1; i <= 4; i++)
        {
            log.Write("INFO", $"entry {i} {payload}");
        }

        Assert.Equal(3, Directory.GetFiles(dir.Path).Length);
        Assert.Contains("entry 4", File.ReadAllText(Path.Combine(dir.Path, "triggerforge.log")));
        Assert.Contains("entry 3", File.ReadAllText(Path.Combine(dir.Path, "triggerforge.1.log")));
        Assert.Contains("entry 2", File.ReadAllText(Path.Combine(dir.Path, "triggerforge.2.log")));
    }

    [Fact]
    public void MasksMessageExceptionAndStackTraceAndKeepsOneHeaderLine()
    {
        using var dir = new TempDir();
        var log = new LogFile(dir.Path, @"C:\Users\Alice", "Alice");
        Exception exception = new InvalidOperationException(@"ALICE AA-BB-CC-DD-AB-CD");
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.SetRemoteStackTrace(exception, @"   at Read in C:\Users\Alice\read.cs:line 1");

        log.Write("ERROR", "c:/users/ALICE/settings.json\r\nAlice aa:bb:cc:dd:ab:cd", exception);

        string[] lines = File.ReadAllLines(Path.Combine(dir.Path, "triggerforge.log"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z ERROR ", lines[0]);
        Assert.Contains("%USERPROFILE%/settings.json <user> **:**:**:**:AB:CD | InvalidOperationException: <user> **:**:**:**:AB:CD", lines[0]);
        Assert.Contains(@"    at Read in %USERPROFILE%\read.cs:line 1", lines);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("    ", line));
    }

    [Fact]
    public void ExportIncludesOnlyLogFilesSettingsAndMaskedSystemInfo()
    {
        using var dir = new TempDir();
        string logs = Directory.CreateDirectory(Path.Combine(dir.Path, "logs")).FullName;
        var log = new LogFile(logs, @"C:\Users\Alice", "Alice");
        const string stored = """{"version":1,"idleSleepMinutes":8,"controllerDefaults":{"AA:BB:CC:DD:EE:01":"p1"}}""";
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), stored);
        Directory.CreateDirectory(dir.Profiles);
        File.WriteAllText(Path.Combine(dir.Profiles, "sample.json"), "{}");
        File.WriteAllText(Path.Combine(logs, "unrelated.log"), "ignored");
        log.Write("INFO", new string('x', 1024 * 1024));
        log.Write("INFO", "next entry");
        string zip = Path.Combine(dir.Path, "export.zip");

        log.Export(zip, dir.Path, new AppSettings(), @"Windows; driver path C:\Users\ALICE\drivers; AA:BB:CC:DD:12:34");

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(new[] { "settings.json", "system.txt", "triggerforge.1.log", "triggerforge.log" },
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        using var settings = new StreamReader(archive.GetEntry("settings.json")!.Open());
        Assert.Equal(stored.Replace("AA:BB:CC:DD:EE:01", "**:**:**:**:EE:01"), settings.ReadToEnd());
        using var system = new StreamReader(archive.GetEntry("system.txt")!.Open());
        Assert.Equal(@"Windows; driver path %USERPROFILE%\drivers; **:**:**:**:12:34", system.ReadToEnd());
    }

    [Fact]
    public void ExportUsesCurrentSettingsBeforeAnySettingsFileHasBeenSaved()
    {
        using var dir = new TempDir();
        string zip = Path.Combine(dir.Path, "export.zip");
        var log = new LogFile(Path.Combine(dir.Path, "logs"), "", "");

        log.Export(zip, dir.Path, new AppSettings { IdleSleepMinutes = 7 }, "Windows");

        using var archive = ZipFile.OpenRead(zip);
        using Stream entry = archive.GetEntry("settings.json")!.Open();
        using JsonDocument json = JsonDocument.Parse(entry);
        Assert.Equal(7, json.RootElement.GetProperty("idleSleepMinutes").GetInt32());
        Assert.False(File.Exists(Path.Combine(dir.Path, "settings.json")));
    }
}
