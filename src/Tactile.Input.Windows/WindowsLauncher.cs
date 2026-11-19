using Tactile.Core;
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
            throw new LocalizedMessage($"Program not found: {path}", $"Program bulunamadı: {path}").CreateException(message => new InvalidOperationException(message));
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
            throw new LocalizedMessage($"Could not start {Path.GetFileName(path)} ({e.Message})", $"Program başlatılamadı: {Path.GetFileName(path)} ({e.Message})").CreateException(message => new InvalidOperationException(message, e));
        }
    }
}
