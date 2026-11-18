using System.Runtime.InteropServices;

namespace Tactile.Host;

/// <summary>Start menu entry that runs <c>Tactile.exe --restore</c>, for when the app itself will not open.</summary>
internal static class StartMenu
{
    private const string RestoreShortcut = "Tactile — Kontrolcüyü geri getir.lnk";

    /// <summary>Creates the shortcut if it is missing. Any failure is ignored.</summary>
    public static void EnsureRestoreShortcut()
    {
        object? shell = null;
        object? link = null;
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), RestoreShortcut);
            string? exe = Environment.ProcessPath;
            if (File.Exists(path) || exe is null || Type.GetTypeFromProgID("WScript.Shell") is not { } type)
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
            l.Description = "Sanal kontrolcüyü kaldırır ve gerçek kontrolcüyü yeniden görünür yapar.";
            l.IconLocation = exe + ",0";
            l.Save();
        }
        catch (Exception)
        {
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
