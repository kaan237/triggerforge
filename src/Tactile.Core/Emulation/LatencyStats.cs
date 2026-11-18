namespace Tactile.Core;

public sealed record LatencySummary(double P50Ms, double P95Ms, int Samples)
{
    public static LatencySummary Empty { get; } = new(0, 0, 0);
}

/// <summary>Rolling window of the last <see cref="Window"/> samples. Thread-safe.</summary>
public sealed class LatencyStats
{
    public const int Window = 512;

    private readonly Lock _lock = new();
    private readonly double[] _samples = new double[Window];
    private int _next;
    private int _count;

    public void Add(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0)
        {
            return;
        }

        lock (_lock)
        {
            _samples[_next] = ms;
            _next = (_next + 1) % Window;
            _count = Math.Min(_count + 1, Window);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _next = 0;
            _count = 0;
        }
    }

    public LatencySummary Summary()
    {
        double[] sorted;
        lock (_lock)
        {
            if (_count == 0)
            {
                return LatencySummary.Empty;
            }

            sorted = _samples[.._count];
        }

        Array.Sort(sorted);
        return new LatencySummary(Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted.Length);
    }

    // Nearest rank.
    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}
