using System.ComponentModel;
using System.Diagnostics;

namespace Tactile.Input.Windows;

/// <summary>Starts programs through the shell, so shortcuts and documents work as well as .exe files.</summary>
public sealed class WindowsLauncher : ILauncher
{
    public void Run(string path, string arguments)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"Program bulunamadı: {path}");
        }

        var info = new ProcessStartInfo(path, arguments ?? string.Empty)
        {
            UseShellExecute = true,
            // Many programs look for their files relative to the working folder.
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        };

        try
        {
            using Process? _ = Process.Start(info);
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"Program başlatılamadı: {Path.GetFileName(path)} ({e.Message})", e);
        }
    }
}
