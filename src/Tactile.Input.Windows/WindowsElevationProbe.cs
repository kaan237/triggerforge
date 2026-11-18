using static Tactile.Input.Windows.Native;

namespace Tactile.Input.Windows;

/// <summary>Windows drops synthetic input sent from a normal process to an elevated window (UIPI).</summary>
public sealed class WindowsElevationProbe : IElevationProbe
{
    private static readonly Lazy<bool> Self = new(() => IsProcessElevated(GetCurrentProcess()) ?? false);

    /// <summary>Whether this process runs elevated. Read once.</summary>
    public static bool SelfElevated => Self.Value;

    public bool ForegroundBlocksInput()
    {
        if (SelfElevated)
        {
            return false;
        }

        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0)
        {
            return false;
        }

        nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return false;
        }

        try
        {
            return IsProcessElevated(process) ?? false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Null when the token cannot be read.</summary>
    private static bool? IsProcessElevated(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            return null;
        }

        try
        {
            return GetTokenInformation(token, TokenElevationClass, out uint elevated, sizeof(uint), out _) ? elevated != 0 : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
