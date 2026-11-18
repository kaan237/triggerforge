using System.Runtime.InteropServices;

namespace Tactile.Host;

internal static partial class Program
{
    private const string MutexName = @"Local\Tactile.Instance";
    private const string ShowEventName = @"Local\Tactile.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // We are in front (just launched), so let the running copy take focus.
            AllowSetForegroundWindow(AsfwAny);
            show.Set();
            return;
        }

        bool tray = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();
        TactileApp app;
        try
        {
            app = new TactileApp(tray, show);
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

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
