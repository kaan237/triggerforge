using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tactile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Profiles => System.IO.Path.Combine(Path, "profiles");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeForeground : IForegroundSource
{
    public event Action<string>? Changed;

    public string? Current { get; private set; }

    public void Set(string exe)
    {
        Current = exe;
        Changed?.Invoke(exe);
    }
}

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

internal static class Inputs
{
    public static InputState Make(
        Buttons buttons = Buttons.None,
        Stick? left = null,
        Stick? right = null,
        byte l2 = 0,
        byte r2 = 0,
        Vector3 gyro = default,
        int battery = 80,
        ChargeState charge = ChargeState.Discharging,
        TouchPoint touch1 = default,
        TouchPoint touch2 = default) =>
        new(buttons, left ?? new Stick(128, 128), right ?? new Stick(128, 128), l2, r2, gyro, default, 0,
            touch1, touch2, new Battery(battery, charge), false, false, false);
}
