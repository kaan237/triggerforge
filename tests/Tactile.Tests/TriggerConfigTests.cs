using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class TriggerConfigTests
{
    private static TriggerConfig Cfg(TriggerMode mode, params (string Key, double Value)[] p) =>
        new() { Mode = mode, Params = p.ToDictionary(x => x.Key, x => x.Value) };

    public static TheoryData<TriggerConfig, byte[]> Cases()
    {
        int[] zones = [0, 1, 2, 3, 4, 5, 6, 7, 8, 8];
        return new TheoryData<TriggerConfig, byte[]>
        {
            { Cfg(TriggerMode.Off), TriggerEffect.Off.Bytes.ToArray() },
            { Cfg(TriggerMode.Feedback, ("start", 2), ("strength", 5)), TriggerEffect.Feedback(2, 5).Bytes.ToArray() },
            { new TriggerConfig { Mode = TriggerMode.Zones, Zones = zones }, TriggerEffect.Feedback(zones).Bytes.ToArray() },
            { Cfg(TriggerMode.Slope, ("start", 1), ("end", 7), ("startStrength", 1), ("endStrength", 8)), TriggerEffect.Slope(1, 7, 1, 8).Bytes.ToArray() },
            { Cfg(TriggerMode.Weapon, ("start", 3), ("end", 6), ("strength", 8)), TriggerEffect.Weapon(3, 6, 8).Bytes.ToArray() },
            { Cfg(TriggerMode.Vibration, ("start", 4), ("amplitude", 6), ("frequency", 30)), TriggerEffect.Vibration(4, 6, 30).Bytes.ToArray() },
            { new TriggerConfig { Mode = TriggerMode.ZonesVibration, Zones = zones, Frequency = 25 }, TriggerEffect.Vibration(zones, 25).Bytes.ToArray() },
            { Cfg(TriggerMode.Bow, ("start", 1), ("end", 4), ("strength", 6), ("snap", 3)), TriggerEffect.Bow(1, 4, 6, 3).Bytes.ToArray() },
            { Cfg(TriggerMode.Galloping, ("start", 0), ("end", 9), ("firstFoot", 2), ("secondFoot", 5), ("frequency", 20)), TriggerEffect.Galloping(0, 9, 2, 5, 20).Bytes.ToArray() },
            { Cfg(TriggerMode.Machine, ("start", 1), ("end", 9), ("amplitudeA", 3), ("amplitudeB", 7), ("frequency", 5), ("period", 10)), TriggerEffect.Machine(1, 9, 3, 7, 5, 10).Bytes.ToArray() },
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_mode_matches_the_factory(TriggerConfig config, byte[] expected)
    {
        Assert.Equal(expected, Triggers.ToEffect(config).Bytes.ToArray());
    }

    [Fact]
    public void Fractional_params_are_rounded()
    {
        TriggerConfig c = Cfg(TriggerMode.Weapon, ("start", 3.2), ("end", 5.6), ("strength", 7.9));
        Assert.Equal(TriggerEffect.Weapon(3, 6, 8), Triggers.ToEffect(c));
    }

    [Fact]
    public void Invalid_params_fail_with_a_message()
    {
        Assert.False(Triggers.TryToEffect(Cfg(TriggerMode.Weapon, ("start", 3)), out TriggerEffect e, out string? error));
        Assert.Equal(TriggerEffect.Off, e);
        Assert.Contains("end", error);

        Assert.False(Triggers.TryToEffect(Cfg(TriggerMode.Weapon, ("start", 9), ("end", 6), ("strength", 1)), out _, out _));
        Assert.False(Triggers.TryToEffect(new TriggerConfig { Mode = TriggerMode.Zones, Zones = [9, 0, 0, 0, 0, 0, 0, 0, 0, 0] }, out _, out _));
        Assert.False(Triggers.TryToEffect(new TriggerConfig { Mode = TriggerMode.ZonesVibration }, out _, out _));
    }
}
