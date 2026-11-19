namespace Tactile.Core;

/// <summary>Resolves the installed and portable layouts before any store opens.</summary>
public sealed class AppPaths
{
    private readonly string _roamingRoot;
    private readonly string? _dataOverride;

    internal AppPaths(string exeDir, string roamingDir, string localDir, string? dataOverride)
    {
        Portable = File.Exists(Path.Combine(exeDir, "portable.txt"));
        _roamingRoot = roamingDir;
        _dataOverride = string.IsNullOrWhiteSpace(dataOverride) ? null : dataOverride;
        DataDirectory = Portable ? Path.Combine(exeDir, "data") : _dataOverride ?? Path.Combine(roamingDir, "TriggerForge");
        string localRoot = Portable ? DataDirectory : Path.Combine(localDir, "TriggerForge");
        LogDirectory = Path.Combine(localRoot, "logs");
        WebViewDirectory = Path.Combine(localRoot, "WebView2");
    }

    public static AppPaths Resolve() => new(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("TACTILE_DATA_DIR"));

    public bool Portable { get; }
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string WebViewDirectory { get; }

    /// <summary>
    /// Where <c>--restore</c> reads the recovery journal: the legacy folder when the new one was
    /// never created, so a journal written by an older build is still undone.
    /// </summary>
    public string RestoreDirectory
    {
        get
        {
            string legacy = Path.Combine(_roamingRoot, "Tactile");
            return !Portable && _dataOverride is null && !Directory.Exists(DataDirectory) && Directory.Exists(legacy)
                ? legacy
                : DataDirectory;
        }
    }

    public void Migrate()
    {
        if (!Portable && _dataOverride is null)
        {
            DataMigration.Run(Path.Combine(_roamingRoot, "Tactile"), DataDirectory);
        }
    }
}
