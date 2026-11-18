namespace Tactile.Input.Windows;

public static class WindowsInputPlatform
{
    public static InputPlatform Create() =>
        new(new WindowsInputInjector(), new WindowsMicrophone(), new WindowsLauncher(), new WindowsElevationProbe());

    /// <summary>Whether this process runs as administrator (elevated token).</summary>
    public static bool IsElevated => WindowsElevationProbe.SelfElevated;
}
