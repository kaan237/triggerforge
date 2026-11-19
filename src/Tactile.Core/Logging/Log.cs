using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Tactile.Core.Logging;

public static class Log
{
    private static volatile LogFile? s_file;

    public static void Initialize(string directory)
    {
        try
        {
            s_file = new LogFile(directory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
        }
        catch (Exception)
        {
            // An unavailable log must not prevent startup.
        }
    }

    public static void Info(string message, Exception? e = null) => s_file?.Write("INFO", message, e);
    public static void Warn(string message, Exception? e = null) => s_file?.Write("WARN", message, e);
    public static void Error(string message, Exception? e = null) => s_file?.Write("ERROR", message, e);

    public static void Notice(Notice notice)
    {
        if (notice.Kind is NoticeKinds.Emulation or NoticeKinds.Mapping or NoticeKinds.BrokenFile)
        {
            Info($"{notice.Kind}: {notice.EnglishMessage}");
        }
    }

    public static void Export(string destination, string dataDirectory, AppSettings settings, string systemInfo) =>
        (s_file ?? throw new InvalidOperationException("Log is unavailable.")).Export(destination, dataDirectory, settings, systemInfo);
}

internal sealed class LogFile(string directory, string userProfile, string userName)
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly string[] Names = ["triggerforge.log", "triggerforge.1.log", "triggerforge.2.log"];
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _lock = new();
    private readonly Regex _sensitive = CreateMask(userProfile, userName);

    internal void Write(string level, string message, Exception? exception = null)
    {
        try
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string entry = $"{timestamp} {level} {SingleLine(message)}";
            if (exception is not null)
            {
                entry += $" | {exception.GetType().Name}: {SingleLine(Text.English(exception))}";
                if (exception.StackTrace is { } stack)
                {
                    foreach (string line in stack.ReplaceLineEndings("\n").Split('\n'))
                    {
                        entry += Environment.NewLine + "    " + line.TrimStart();
                    }
                }
            }

            byte[] bytes = Utf8.GetBytes(Mask(entry) + Environment.NewLine);
            lock (_lock)
            {
                Directory.CreateDirectory(directory);
                string current = Path.Combine(directory, Names[0]);
                var info = new FileInfo(current);
                if (info.Exists && (info.Length >= MaxBytes || info.Length + bytes.Length > MaxBytes))
                {
                    string older = Path.Combine(directory, Names[1]);
                    string oldest = Path.Combine(directory, Names[2]);
                    File.Delete(oldest);
                    if (File.Exists(older))
                    {
                        File.Move(older, oldest);
                    }

                    File.Move(current, older);
                }

                using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
        }
        catch (Exception)
        {
            // Logging must also be safe to call from exception handlers.
        }
    }

    internal void Export(string destination, string dataDirectory, AppSettings settings, string systemInfo)
    {
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            lock (_lock)
            {
                using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
                foreach (string name in Names)
                {
                    string path = Path.Combine(directory, name);
                    if (File.Exists(path))
                    {
                        archive.CreateEntryFromFile(path, name);
                    }
                }

                // Settings hold controller MAC addresses, so they get the same masking as the log.
                const string settingsName = "settings.json";
                string settingsPath = Path.Combine(dataDirectory, settingsName);
                string settingsText = File.Exists(settingsPath)
                    ? File.ReadAllText(settingsPath)
                    : System.Text.Json.JsonSerializer.Serialize(settings, TactileJson.Options);
                using (var entry = new StreamWriter(archive.CreateEntry(settingsName).Open(), Utf8))
                {
                    entry.Write(Mask(settingsText));
                }

                using var system = new StreamWriter(archive.CreateEntry("system.txt").Open(), Utf8);
                system.Write(Mask(systemInfo));
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string SingleLine(string value) => value.ReplaceLineEndings(" ");

    internal string Mask(string value) => _sensitive.Replace(value, match =>
        match.Groups["profile"].Success ? "%USERPROFILE%"
        : match.Groups["mac"].Success ? $"**:**:**:**:{match.Groups["tail"].Value.Replace('-', ':').ToUpperInvariant()}"
        : "<user>");

    private static Regex CreateMask(string profile, string name)
    {
        var patterns = new List<string>();
        if (!string.IsNullOrEmpty(profile))
        {
            patterns.Add($"(?<profile>{Regex.Escape(profile)}|{Regex.Escape(profile.Replace('\\', '/'))})");
        }

        patterns.Add(@"(?<mac>(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){4}(?<tail>[0-9a-f]{2}[:-][0-9a-f]{2})(?![0-9a-f]))");
        if (!string.IsNullOrEmpty(name))
        {
            patterns.Add(Regex.Escape(name));
        }

        return new Regex(string.Join('|', patterns), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
