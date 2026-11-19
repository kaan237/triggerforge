using Tactile.Core.Logging;

namespace Tactile.Core;

internal static class DataMigration
{
    internal static bool Run(string source, string target)
    {
        if (Directory.Exists(target) || !Directory.Exists(source))
        {
            Log.Info("Data migration skipped: no legacy folder or target already exists.");
            return false;
        }

        string temporary = $"{target}.migration-{Guid.NewGuid():N}";
        try
        {
            CopyDirectory(source, temporary);
            Directory.Move(temporary, target);
            Log.Info("Legacy data migrated.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Legacy data migration did not complete.", e);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn("Could not remove the temporary migration folder.", e);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Cannot migrate directory link: {source}");
        }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Cannot migrate file link: {file}");
            }

            using FileStream input = File.OpenRead(file);
            using FileStream output = File.Create(Path.Combine(target, Path.GetFileName(file)));
            input.CopyTo(output);
        }

        foreach (string child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }
}
