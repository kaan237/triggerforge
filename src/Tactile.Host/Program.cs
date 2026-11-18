using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Tactile.Core;
using Tactile.Drivers;

namespace Tactile.Host;

internal static partial class Program
{
    private const string MutexName = @"Local\Tactile.Instance";
    private const string ShowEventName = @"Local\Tactile.Show";
    private const string TrayFlag = "--tray";
    private const string AfterFlag = "--after";
    private const string RestoreFlag = "--restore";
    private const int AfterWaitMs = 5000;
    private const int RestoreWaitMs = 10_000;
    internal const string RestoreEventName = @"Local\Tactile.Restore";
    internal const string RestoredEventName = @"Local\Tactile.Restored";

    private static bool s_tray;

    [STAThread]
    private static void Main(string[] args)
    {
        // Before the mutex: this must work while another copy runs (or hangs).
        if (args.Any(a => string.Equals(a, RestoreFlag, StringComparison.OrdinalIgnoreCase)))
        {
            Restore();
            return;
        }

        // A restarted copy must not see the old one's mutex and hand over to it.
        WaitForPrevious(args);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // We are in front (just launched), so let the running copy take focus.
            AllowSetForegroundWindow(AsfwAny);
            show.Set();
            return;
        }

        s_tray = args.Any(a => string.Equals(a, TrayFlag, StringComparison.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();
        TactileApp app;
        try
        {
            app = new TactileApp(s_tray, show);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show($"Tactile başlatılamadı: ayar klasörüne erişilemiyor.{Environment.NewLine}{Environment.NewLine}{e.Message}", "Tactile",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (app)
        {
            Application.Run(app);
        }

        GC.KeepAlive(mutex);
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
    private static void Restore()
    {
        ApplicationConfiguration.Initialize();
        if (EventWaitHandle.TryOpenExisting(RestoreEventName, out EventWaitHandle? restore))
        {
            using (restore)
            {
                EventWaitHandle.TryOpenExisting(RestoredEventName, out EventWaitHandle? restored);
                using (restored)
                {
                    restore.Set();
                    restored?.WaitOne(RestoreWaitMs);
                }
            }
        }

        string? failure = null;
        try
        {
            DriverPlatform.CreateWindows(AppCore.DataDirectory()).Recovery.RestoreAll();
        }
        catch (Exception e)
        {
            failure = e.Message;
        }

        try
        {
            var settings = new SettingsStore();
            AppSettings s = settings.Current;
            if (s.Emulation.Enabled)
            {
                settings.Save(s with { Emulation = s.Emulation with { Enabled = false } });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failure ??= "Ayar dosyası yazılamadı.";
        }

        if (failure is null)
        {
            MessageBox.Show("Kontrolcü geri getirildi. Sanal kontrolcü kaldırıldı, gerçek kontrolcü yeniden görünür ve sanal kontrolcü ayarı kapalı.",
                "Tactile", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show($"Kontrolcü tam olarak geri getirilemedi: {failure}", "Tactile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
            // Already gone, or not ours to wait on.
        }
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
