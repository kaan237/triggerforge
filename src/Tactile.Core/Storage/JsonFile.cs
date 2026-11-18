using System.Text.Json;

namespace Tactile.Core;

internal static class JsonFile
{
    public const string DataDirVariable = "TACTILE_DATA_DIR";

    public static string ResolveDataDir(string? dataDir)
    {
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return dataDir;
        }

        string? env = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tactile");
    }

    /// <summary>Writes next to the target first so a crash never leaves a half-written file.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, TactileJson.Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads and parses a file. A file that does not parse is renamed out of the way and
    /// its new path returned in <paramref name="brokenPath"/>.
    /// </summary>
    public static T? Read<T>(string path, out string? brokenPath) where T : class
    {
        brokenPath = null;
        try
        {
            string text = File.ReadAllText(path);
            T? value = JsonSerializer.Deserialize<T>(text, TactileJson.Options);
            if (value is not null)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable right now (sync client, antivirus): skip it this run, keep the file.
            return null;
        }

        brokenPath = MoveAside(path);
        return null;
    }

    private static string MoveAside(string path)
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string target = $"{path}.broken-{stamp}";
        for (int i = 2; File.Exists(target); i++)
        {
            target = $"{path}.broken-{stamp}-{i}";
        }

        File.Move(path, target);
        return target;
    }
}
