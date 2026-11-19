using Tactile.Core;
using System.Runtime.InteropServices;
using Tactile.Core.Logging;

namespace Tactile.Host;

/// <summary>Start menu entry that runs <c>TriggerForge.exe --restore</c>, for when the app itself will not open.</summary>
internal static class StartMenu
{
    private static readonly LocalizedMessage RestoreShortcut = new("TriggerForge — Restore controller.lnk", "TriggerForge — Kontrolcüyü geri getir.lnk");

    /// <summary>Creates the shortcut if it is missing. Any failure is ignored.</summary>
    public static void EnsureRestoreShortcut()
    {
        object? shell = null;
        object? link = null;
        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string path = Path.Combine(programs, RestoreShortcut.Value);
            string oldPath = Path.Combine(programs, "Tactile — Kontrolcüyü geri getir.lnk");
            // The installer adds this shortcut for all users; a second per-user copy would be a duplicate.
            string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            bool installed = File.Exists(Path.Combine(commonPrograms, RestoreShortcut.En)) || File.Exists(Path.Combine(commonPrograms, RestoreShortcut.Tr));
            if (installed || File.Exists(Path.Combine(programs, RestoreShortcut.En)) || File.Exists(Path.Combine(programs, RestoreShortcut.Tr)))
            {
                File.Delete(oldPath);
                return;
            }

            string? exe = Environment.ProcessPath;
            if (exe is null || Type.GetTypeFromProgID("WScript.Shell") is not { } type)
            {
                return;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return;
            }

            link = ((dynamic)shell).CreateShortcut(path);
            dynamic l = link!;
            l.TargetPath = exe;
            l.Arguments = "--restore";
            l.WorkingDirectory = AppContext.BaseDirectory;
            l.Description = Text.Pick("Removes the virtual controller and makes the physical controller visible again.", "Sanal kontrolcüyü kaldırır ve gerçek kontrolcüyü yeniden görünür yapar.");
            l.IconLocation = exe + ",0";
            l.Save();
            File.Delete(oldPath);
        }
        catch (Exception e)
        {
            Log.Warn("Could not update the restore shortcut.", e);
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
