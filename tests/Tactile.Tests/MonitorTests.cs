using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class MonitorTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Gyro_calibration_succeeds_when_still()
    {
        var progress = new List<double>();
        var c = new GyroCalibrator(progress: progress.Add);
        GyroCalibrationResult? r = null;
        var rng = new Random(1);
        for (int i = 0; r is null; i++)
        {
            float n() => (float)(rng.NextDouble() - 0.5) * 0.2f;
            r = c.Add(new Vector3(0.5f + n(), -0.3f + n(), 0.1f + n()), S(i * 0.004));
        }

        Assert.True(r.Success, r.Reason);
        Assert.Equal(0.5f, r.Bias.X, 0.02f);
        Assert.Equal(-0.3f, r.Bias.Y, 0.02f);
        Assert.Equal(0.1f, r.Bias.Z, 0.02f);
        Assert.Equal(0, progress[0]);
        Assert.Equal(1, progress[^1]);
        Assert.InRange(progress.Count, 10, 12);

        Vector3 corrected = GyroCalibrationResult.Apply(new Vector3(0.5f, -0.3f, 0.1f), r.Bias);
        Assert.Equal(0f, corrected.X, 0.02f);
    }

    [Fact]
    public void Gyro_calibration_fails_when_moved()
    {
        var c = new GyroCalibrator();
        GyroCalibrationResult? r = null;
        for (int i = 0; r is null; i++)
        {
            float wobble = (float)Math.Sin(i * 0.05) * 30;
            r = c.Add(new Vector3(wobble, 0, 0), S(i * 0.004));
        }

        Assert.False(r.Success);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Gyro_calibration_fails_without_enough_samples()
    {
        var c = new GyroCalibrator();
        Assert.Null(c.Add(default, S(0)));
        GyroCalibrationResult? r = c.Add(default, S(3));
        Assert.NotNull(r);
        Assert.False(r.Success);
    }

    [Fact]
    public void Battery_warns_once_per_threshold_until_charged()
    {
        var m = new BatteryMonitor();
        int[] t = [20, 10];
        Battery B(int p, ChargeState c = ChargeState.Discharging) => new(p, c);

        Assert.Null(m.Update(B(50), t));
        Assert.Equal(20, m.Update(B(20), t));
        Assert.Null(m.Update(B(20), t));
        Assert.Null(m.Update(B(30), t));
        Assert.Null(m.Update(B(15), t));
        Assert.Equal(10, m.Update(B(10), t));
        Assert.Null(m.Update(B(5), t));

        Assert.Null(m.Update(B(10, ChargeState.Charging), t));
        Assert.Equal(10, m.Update(B(5), t));
    }

    [Fact]
    public void Battery_drop_past_two_thresholds_warns_for_the_lowest()
    {
        var m = new BatteryMonitor();
        Assert.Equal(10, m.Update(new Battery(5, ChargeState.Discharging), [20, 10]));
        Assert.Null(m.Update(new Battery(5, ChargeState.Discharging), [20, 10]));
    }

    [Fact]
    public void Idle_monitor()
    {
        var m = new IdleMonitor();
        m.Reset(S(0));
        m.Observe(Inputs.Make(), S(0));

        // Small stick drift and gyro motion are not activity.
        m.Observe(Inputs.Make(left: new Stick(132, 126), gyro: new Vector3(500, 0, 0)), S(60));
        m.Observe(Inputs.Make(left: new Stick(134, 124)), S(120));
        Assert.Equal(S(0), m.LastActivity);
        Assert.True(m.ShouldSleep(S(15 * 60), 15, Link.Bluetooth));
        Assert.False(m.ShouldSleep(S(15 * 60), 15, Link.Usb));
        Assert.False(m.ShouldSleep(S(15 * 60), 0, Link.Bluetooth));
        Assert.False(m.ShouldSleep(S(15 * 60), 15, null));

        m.Observe(Inputs.Make(buttons: Buttons.Cross), S(200));
        Assert.Equal(S(200), m.LastActivity);
        m.Observe(Inputs.Make(), S(201));
        m.Observe(Inputs.Make(r2: 40), S(300));
        Assert.Equal(S(300), m.LastActivity);
        m.Observe(Inputs.Make(right: new Stick(200, 128), r2: 40), S(400));
        Assert.Equal(S(400), m.LastActivity);
        Assert.False(m.ShouldSleep(S(400 + 14 * 60), 15, Link.Bluetooth));
    }

    [Fact]
    public void Bluetooth_address_parsing()
    {
        Assert.True(BluetoothLink.TryParseAddress("AA:BB:CC:DD:EE:01", out ulong a));
        Assert.Equal(0xAABBCCDDEE01UL, a);
        Assert.True(BluetoothLink.TryParseAddress("aa-bb-cc-dd-ee-01", out a));
        Assert.Equal(0xAABBCCDDEE01UL, a);
        Assert.False(BluetoothLink.TryParseAddress("AA:BB:CC", out _));
        Assert.False(BluetoothLink.TryParseAddress("GG:BB:CC:DD:EE:01", out _));
        Assert.False(BluetoothLink.TryParseAddress(null, out _));
    }
}
