using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Tactile.Core;
using Tactile.Core.Logging;
using Tactile.Drivers;

namespace Tactile.Host;

internal static partial class Program
{
    private const string MutexName = @"Local\TriggerForge.Instance";
    private const string ShowEventName = @"Local\TriggerForge.Show";
    private const string TrayFlag = "--tray";
    private const string AfterFlag = "--after";
    private const string RestoreFlag = "--restore";
    private const int AfterWaitMs = 5000;
    private const int RestoreWaitMs = 10_000;
    internal const string RestoreEventName = @"Local\TriggerForge.Restore";
    internal const string RestoredEventName = @"Local\TriggerForge.Restored";

    private static bool s_tray;

    [STAThread]
    private static int Main(string[] args)
    {
        AppPaths paths = AppPaths.Resolve();
        Log.Initialize(paths.LogDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled application exception.", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception.", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("Unobserved task exception.", e.Exception);
        Log.Info($"TriggerForge {Application.ProductVersion} starting. Windows: {Environment.OSVersion.VersionString}; portable: {(paths.Portable ? "yes" : "no")}.");

        // Before the mutex: this must work while another copy runs (or hangs).
        // No data migration here: the uninstaller runs this and must not create new data folders.
        if (args.Any(a => string.Equals(a, RestoreFlag, StringComparison.OrdinalIgnoreCase)))
        {
            return Restore(paths, args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)));
        }

        paths.Migrate();

        // A restarted copy must not see the old one's mutex and hand over to it.
        WaitForPrevious(args);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // We are in front (just launched), so let the running copy take focus.
            AllowSetForegroundWindow(AsfwAny);
            show.Set();
            return 0;
        }

        s_tray = args.Any(a => string.Equals(a, TrayFlag, StringComparison.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();
        TactileApp app;
        try
        {
            app = new TactileApp(s_tray, show, paths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Startup could not access the data folder.", e);
            MessageBox.Show(Text.Pick($"TriggerForge could not start. The settings folder is inaccessible.{Environment.NewLine}{Environment.NewLine}{e.Message}", $"TriggerForge başlatılamadı: ayar klasörüne erişilemiyor.{Environment.NewLine}{Environment.NewLine}{e.Message}"), "TriggerForge",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using (app)
        {
            Application.Run(app);
        }

        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>
    /// Starts this exe as administrator, told to wait for this process. Throws
    /// <see cref="System.ComponentModel.Win32Exception"/> (1223 when the UAC prompt is cancelled).
    /// </summary>
    internal static void StartElevatedCopy()
    {
        string exe = Environment.ProcessPath ?? throw new System.ComponentModel.Win32Exception(2);
        string arguments = $"{AfterFlag} {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}" + (s_tray ? " " + TrayFlag : string.Empty);
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        using Process? _ = Process.Start(info);
    }

    /// <summary>
    /// <c>--restore</c>: a running copy is asked to undo first (it owns the pad), then the
    /// journal is replayed here too in case that copy is stuck, and emulation is switched off.
    /// </summary>
    private static int Restore(AppPaths paths, bool quiet)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(RestoreEventName, out EventWaitHandle? restore))
            {
                using (restore)
                {
                    EventWaitHandle.TryOpenExisting(RestoredEventName, out EventWaitHandle? restored);
                    using (restored)
                    {
                        restore.Set();
                        if (restored is not null && !restored.WaitOne(RestoreWaitMs))
                        {
                            Log.Warn("Running instance did not acknowledge the restore request.");
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn("Could not signal the running instance for restore.", e);
        }

        Func<string>? failure = null;
        string dataDirectory = paths.RestoreDirectory;
        try
        {
            DriverPlatform.CreateWindows(dataDirectory).Recovery.RestoreAll();
            // The driver layer retains journal entries whose undo did not succeed.
            if (File.Exists(Path.Combine(dataDirectory, "recovery.json")))
            {
                failure = () => Text.Pick("Some driver changes could not be undone.", "Bazı sürücü değişiklikleri geri alınamadı.");
                Log.Warn("Driver changes remain in the recovery journal.");
            }
        }
        catch (Exception e)
        {
            Log.Error("Recovery journal replay failed.", e);
            failure = () => e.Message;
        }

        try
        {
            var settings = new SettingsStore(dataDirectory, paths.Portable);
            Text.Current = settings.Current.Language;
            AppSettings s = settings.Current;
            if (s.Emulation.Enabled)
            {
                settings.Save(s with { Emulation = s.Emulation with { Enabled = false } });
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not turn off emulation during restore.", e);
            failure ??= () => Text.Pick("Could not write the settings file.", "Ayar dosyası yazılamadı.");
        }

        if (failure is null)
        {
            Log.Info("--restore succeeded.");
        }
        else
        {
            Log.Error("--restore failed.");
        }

        if (quiet)
        {
            return failure is null ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        if (failure is null)
        {
            MessageBox.Show(Text.Pick("Controller restored. The virtual controller was removed, the physical controller is visible again, and emulation is off.", "Kontrolcü geri getirildi. Sanal kontrolcü kaldırıldı, gerçek kontrolcü yeniden görünür ve sanal kontrolcü ayarı kapalı."),
                "TriggerForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(Text.Pick($"Could not fully restore the controller: {failure()}", $"Kontrolcü tam olarak geri getirilemedi: {failure()}"), "TriggerForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return failure is null ? 0 : 1;
    }

    private static void WaitForPrevious(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, AfterFlag, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length
            || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || pid == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using Process previous = Process.GetProcessById(pid);
            previous.WaitForExit(AfterWaitMs);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Info("Previous process is unavailable to wait for.", e);
        }
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
