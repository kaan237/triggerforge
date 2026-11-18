using Tactile.Device.DualSense;

namespace Tactile.Core;

/// <summary>Tracks when the player last touched the controller.</summary>
public sealed class IdleMonitor
{
    /// <summary>Stick travel (normalized) that counts as use. Bigger than resting drift.</summary>
    public const float StickThreshold = 0.12f;

    /// <summary>Analog trigger change (0-255) that counts as use.</summary>
    public const int TriggerThreshold = 20;

    private InputState? _reference;
    private TimeSpan _lastActivity;

    public TimeSpan LastActivity => _lastActivity;

    public void Reset(TimeSpan now)
    {
        _reference = null;
        _lastActivity = now;
    }

    /// <summary>Compares against the state at the last activity, so slow drift does not keep it awake.</summary>
    public void Observe(InputState s, TimeSpan now)
    {
        if (_reference is null || IsActivity(_reference, s))
        {
            _reference = s;
            _lastActivity = now;
        }
    }

    public TimeSpan IdleFor(TimeSpan now) => now - _lastActivity;

    /// <param name="minutes">0 = never.</param>
    public bool ShouldSleep(TimeSpan now, int minutes, Link? link) =>
        minutes > 0 && link == Link.Bluetooth && IdleFor(now) >= TimeSpan.FromMinutes(minutes);

    public static bool IsActivity(InputState before, InputState now) =>
        before.Buttons != now.Buttons ||
        Moved(before.Left, now.Left) || Moved(before.Right, now.Right) ||
        Math.Abs(before.L2 - now.L2) >= TriggerThreshold ||
        Math.Abs(before.R2 - now.R2) >= TriggerThreshold ||
        before.Touch1.Active != now.Touch1.Active ||
        (now.Touch1.Active && (before.Touch1.X != now.Touch1.X || before.Touch1.Y != now.Touch1.Y));

    private static bool Moved(Stick a, Stick b)
    {
        (float ax, float ay) = a.Normalized;
        (float bx, float by) = b.Normalized;
        return Math.Abs(ax - bx) >= StickThreshold || Math.Abs(ay - by) >= StickThreshold;
    }
}

/// <summary>
/// One warning per threshold while discharging; charging re-arms them.
/// A drop past several thresholds at once gives one warning, for the lowest.
/// </summary>
public sealed class BatteryMonitor
{
    private readonly HashSet<int> _warned = [];

    /// <returns>The threshold to warn about, or null.</returns>
    public int? Update(Battery battery, IReadOnlyList<int> thresholds)
    {
        if (battery.Charge != ChargeState.Discharging)
        {
            _warned.Clear();
            return null;
        }

        int? lowest = null;
        foreach (int t in thresholds)
        {
            if (battery.Percent <= t && _warned.Add(t) && (lowest is null || t < lowest))
            {
                lowest = t;
            }
        }

        return lowest;
    }

    public void Reset() => _warned.Clear();
}
