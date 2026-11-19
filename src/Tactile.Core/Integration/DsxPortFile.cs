using System.Globalization;

namespace Tactile.Core.Integration;

internal static class DsxPortFile
{
    public const string DefaultPath = @"C:\Temp\DualSenseX\DualSenseX_PortNumber.txt";

    public static void Write(string path, int port)
    {
        string target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, port.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
