using Tactile.Core;

namespace Tactile.Tests.Hosting;

public sealed class DataMigrationTests
{
    [Fact]
    public void CopiesNestedDataOnceAndKeepsOriginalFiles()
    {
        using var dir = new TempDir();
        string old = Path.Combine(dir.Path, "Tactile");
        string target = Path.Combine(dir.Path, "TriggerForge");
        string relative = Path.Combine("profiles", "nested", "default.json");
        Directory.CreateDirectory(Path.Combine(old, "profiles", "nested"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "old settings");
        File.WriteAllText(Path.Combine(old, relative), "profile");
        File.WriteAllText(Path.Combine(old, "recovery.json"), "journal");

        Assert.True(DataMigration.Run(old, target));
        Assert.False(DataMigration.Run(old, target));

        foreach (string file in new[] { "settings.json", relative, "recovery.json" })
        {
            Assert.Equal(File.ReadAllText(Path.Combine(old, file)), File.ReadAllText(Path.Combine(target, file)));
        }

        Assert.Empty(Directory.GetDirectories(dir.Path, "TriggerForge.migration-*"));
    }

    [Fact]
    public void ExistingTargetWins()
    {
        using var dir = new TempDir();
        string old = Directory.CreateDirectory(Path.Combine(dir.Path, "Tactile")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(dir.Path, "TriggerForge")).FullName;
        File.WriteAllText(Path.Combine(old, "legacy.json"), "{}");
        File.WriteAllText(Path.Combine(target, "settings.json"), "current settings");

        Assert.False(DataMigration.Run(old, target));

        Assert.Equal("current settings", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.False(File.Exists(Path.Combine(target, "legacy.json")));
    }

    [Fact]
    public void FailureInsideNestedFolderLeavesNoPartialTarget()
    {
        using var dir = new TempDir();
        string old = Path.Combine(dir.Path, "Tactile");
        string nested = Directory.CreateDirectory(Path.Combine(old, "profiles")).FullName;
        string target = Path.Combine(dir.Path, "TriggerForge");
        File.WriteAllText(Path.Combine(old, "settings.json"), "already copied before profiles");
        using (File.Create(Path.Combine(nested, "locked.json")))
        {
            Assert.False(DataMigration.Run(old, target));
        }

        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(dir.Path, "TriggerForge.migration-*"));
        Assert.True(File.Exists(Path.Combine(old, "settings.json")));
        Assert.True(File.Exists(Path.Combine(nested, "locked.json")));
    }

    [Fact]
    public void PortableUsesExeFolderAndKeepsExplicitlySavedEmulationChoice()
    {
        using var dir = new TempDir();
        string exe = Directory.CreateDirectory(Path.Combine(dir.Path, "exe")).FullName;
        string roaming = Path.Combine(dir.Path, "roaming");
        string local = Path.Combine(dir.Path, "local");
        Directory.CreateDirectory(Path.Combine(roaming, "Tactile"));
        File.WriteAllText(Path.Combine(roaming, "Tactile", "recovery.json"), "legacy journal");
        File.WriteAllText(Path.Combine(exe, "portable.txt"), "");
        var paths = new AppPaths(exe, roaming, local, Path.Combine(dir.Path, "override"));

        paths.Migrate();

        Assert.True(paths.Portable);
        Assert.Equal(Path.Combine(exe, "data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(exe, "data", "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(exe, "data", "WebView2"), paths.WebViewDirectory);
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(Directory.Exists(Path.Combine(roaming, "TriggerForge")));
        var settings = new SettingsStore(paths.DataDirectory, paths.Portable);
        Assert.False(settings.Current.Emulation.Enabled);

        settings.Save(settings.Current with { Emulation = settings.Current.Emulation with { Enabled = true } });

        Assert.True(new SettingsStore(paths.DataDirectory, paths.Portable).Current.Emulation.Enabled);
    }

    [Fact]
    public void InstalledModeHonorsDataOverrideAndLeavesLocalStorageSeparate()
    {
        using var dir = new TempDir();
        string roaming = Path.Combine(dir.Path, "roaming");
        string local = Path.Combine(dir.Path, "local");
        string custom = Path.Combine(dir.Path, "custom");
        var paths = new AppPaths(dir.Path, roaming, local, custom);
        Directory.CreateDirectory(Path.Combine(roaming, "Tactile"));

        paths.Migrate();

        Assert.False(paths.Portable);
        Assert.Equal(custom, paths.DataDirectory);
        Assert.Equal(Path.Combine(local, "TriggerForge", "WebView2"), paths.WebViewDirectory);
        Assert.Equal(Path.Combine(local, "TriggerForge", "logs"), paths.LogDirectory);
        Assert.False(Directory.Exists(custom));
        Assert.True(new SettingsStore(custom).Current.Emulation.Enabled);
        Assert.Equal(Path.Combine(roaming, "TriggerForge"), new AppPaths(dir.Path, roaming, local, null).DataDirectory);
    }
}
