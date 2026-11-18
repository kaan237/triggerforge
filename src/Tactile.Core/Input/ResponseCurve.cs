namespace Tactile.Core;

/// <summary>Maps 0-1 to 0-1 for sticks and analog triggers.</summary>
public static class ResponseCurve
{
    public const double MinExponent = 0.3;
    public const double MaxExponent = 4.0;
    public const int PointCount = 5;

    public static double Apply(CurveKind kind, double exponent, double[][]? points, double x)
    {
        x = Math.Clamp(x, 0, 1);
        return kind switch
        {
            CurveKind.Exponential => Math.Pow(x, Math.Clamp(exponent, MinExponent, MaxExponent)),
            CurveKind.Custom when IsValid(points) => Piecewise(points!, x),
            _ => x,
        };
    }

    /// <summary>5 pairs in 0-1, x ascending, starting at [0,0] and ending at [1,1].</summary>
    public static bool IsValid(double[][]? points)
    {
        if (points is not { Length: PointCount })
        {
            return false;
        }

        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] is not { Length: 2 } p || !InUnit(p[0]) || !InUnit(p[1]))
            {
                return false;
            }

            if (i > 0 && p[0] <= points[i - 1][0])
            {
                return false;
            }
        }

        return points[0][0] == 0 && points[0][1] == 0 && points[^1][0] == 1 && points[^1][1] == 1;
    }

    private static bool InUnit(double v) => v >= 0 && v <= 1;

    private static double Piecewise(double[][] points, double x)
    {
        for (int i = 1; i < points.Length; i++)
        {
            double[] a = points[i - 1];
            double[] b = points[i];
            if (x <= b[0])
            {
                return a[1] + (b[1] - a[1]) * (x - a[0]) / (b[0] - a[0]);
            }
        }

        return 1;
    }
}
