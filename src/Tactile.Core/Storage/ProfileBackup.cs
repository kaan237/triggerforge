using System.Globalization;
using System.Text.RegularExpressions;
using Tactile.Core.Logging;

namespace Tactile.Core;

public sealed record ProfileBackupEntry(string Id, string Date, int ProfileCount);

public static partial class ProfileBackup
{
    private const string PreRestorePrefix = "pre-restore-";

    public static void EnsureDailyBackup(string dataDir, string profilesDir, int backupCount, DateOnly today)
    {
        if (backupCount <= 0)
        {
            return;
        }

        string root = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(root);
        RejectLink(root);
        string id = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string target = Path.Combine(root, id);
        if (!Directory.Exists(target))
        {
            Snapshot(profilesDir, target);
        }

        Prune(root, preRestore: false, keep: Math.Min(backupCount, 30));
    }

    public static IReadOnlyList<ProfileBackupEntry> ListBackups(string dataDir)
    {
        string root = Path.Combine(dataDir, "backups");
        return Folders(root).Select(path =>
        {
            string id = Path.GetFileName(path);
            string date = id.StartsWith(PreRestorePrefix, StringComparison.Ordinal)
                ? DateTime.ParseExact(id[PreRestorePrefix.Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : id;
            return new ProfileBackupEntry(id, date, Files(path).Length);
        }).OrderByDescending(entry => entry.Date, StringComparer.Ordinal)
          .ThenByDescending(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    public static int RestoreBackup(string dataDir, string profilesDir, string id) =>
        RestoreBackup(dataDir, profilesDir, id, () => { });

    internal static int RestoreBackup(string dataDir, string profilesDir, string id, Action reload)
    {
        if (!ValidId(id))
        {
            throw new LocalizedMessage("Invalid backup ID.", "Yedek kimliği geçersiz.")
                .CreateException(message => new ArgumentException(message, nameof(id)));
        }

        string root = Path.GetFullPath(Path.Combine(dataDir, "backups"));
        string source = Path.GetFullPath(Path.Combine(root, id));
        if (!string.Equals(Path.GetDirectoryName(source), root, StringComparison.Ordinal) || !Directory.Exists(source))
        {
            throw new LocalizedMessage("Backup not found.", "Yedek bulunamadı.")
                .CreateException(message => new DirectoryNotFoundException(message));
        }

        RejectLink(root);
        RejectLink(source);
        string[] snapshot = Files(source);
        foreach (string file in snapshot)
        {
            RejectLink(file);
        }

        DateTime stamp = DateTime.Now;
        string before;
        do
        {
            before = Path.Combine(root, PreRestorePrefix + stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            stamp = stamp.AddSeconds(1);
        } while (Directory.Exists(before));

        Snapshot(profilesDir, before);
        var changed = new List<string>();
        try
        {
            var names = new HashSet<string>(snapshot.Select(file => Path.GetFileName(file)), StringComparer.OrdinalIgnoreCase);
            foreach (string file in snapshot)
            {
                string destination = Path.Combine(profilesDir, Path.GetFileName(file));
                CopyAtomic(file, destination);
                changed.Add(destination);
            }

            foreach (string file in Files(profilesDir))
            {
                if (!names.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                    changed.Add(file);
                }
            }

            reload();
        }
        catch (Exception restoreError)
        {
            try
            {
                foreach (string path in changed)
                {
                    string original = Path.Combine(before, Path.GetFileName(path));
                    if (File.Exists(original))
                    {
                        CopyAtomic(original, path);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(Text.Pick(
                    $"Restore and rollback failed. The original files are in {before}.",
                    $"Geri yükleme ve geri alma başarısız oldu. Özgün dosyalar {before} konumunda."),
                    restoreError, rollbackError);
            }

            throw;
        }

        try
        {
            Prune(root, preRestore: true, keep: 2, preserve: before);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not prune pre-restore profile backups.", error);
        }

        return snapshot.Length;
    }

    private static void Snapshot(string source, string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (string file in Files(source))
            {
                RejectLink(file);
                File.Copy(file, Path.Combine(temporary, Path.GetFileName(file)));
            }

            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static void CopyAtomic(string source, string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string[] Files(string path) => Directory.GetFiles(path, "*.json");

    private static IEnumerable<string> Folders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        RejectLink(root);
        return Directory.EnumerateDirectories(root).Where(path => ValidId(Path.GetFileName(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LocalizedMessage("Linked backup paths are unsupported.", "Bağlantılı yedek yolları desteklenmiyor.")
                .CreateException(message => new IOException(message));
        }
    }

    private static void Prune(string root, bool preRestore, int keep, string? preserve = null)
    {
        foreach (string path in Folders(root).Where(path => path != preserve &&
            Path.GetFileName(path).StartsWith(PreRestorePrefix, StringComparison.Ordinal) == preRestore)
            .OrderDescending(StringComparer.Ordinal).Skip(keep).ToArray())
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool ValidId(string? id) => id is not null && BackupId().IsMatch(id) &&
        (id.StartsWith(PreRestorePrefix, StringComparison.Ordinal)
            ? DateTime.TryParseExact(id[PreRestorePrefix.Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)
            : DateOnly.TryParseExact(id, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    [GeneratedRegex(@"\A(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|pre-restore-[0-9]{8}-[0-9]{6})\z")]
    private static partial Regex BackupId();
}
