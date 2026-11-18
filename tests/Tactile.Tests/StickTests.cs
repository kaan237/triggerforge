using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class StickTests
{
    private const float Eps = 1e-4f;

    private static StickProcessor P(double inner = 0.1, double outer = 0.1, StickShape shape = StickShape.Round,
        CurveKind curve = CurveKind.Linear, double exponent = 1, double[][]? points = null, double anti = 0,
        bool invertX = false, bool invertY = false) =>
        new(new StickConfig
        {
            InnerDeadzone = inner, OuterDeadzone = outer, Shape = shape, Curve = curve, Exponent = exponent,
            Points = points, AntiDeadzone = anti, InvertX = invertX, InvertY = invertY,
        });

    [Fact]
    public void Centre_and_inner_deadzone_read_zero()
    {
        Assert.Equal((0f, 0f), P().Process(new Stick(128, 128)));
        Assert.Equal((0f, 0f), P().Process(0.07f, 0.07f));
    }

    [Fact]
    public void Round_deadzone_rescales_magnitude()
    {
        StickProcessor p = P(inner: 0.1, outer: 0.1);
        // Halfway between inner (0.1) and edge (0.9).
        (float x, float y) = p.Process(0.5f, 0);
        Assert.Equal(0.5f, x, Eps);
        Assert.Equal(0f, y, Eps);

        // Beyond the edge is full, and keeps the direction on the circle.
        (x, y) = p.Process(1, 1);
        Assert.Equal(MathF.Sqrt(0.5f), x, Eps);
        Assert.Equal(MathF.Sqrt(0.5f), y, Eps);
    }

    [Fact]
    public void Square_deadzone_is_per_axis()
    {
        StickProcessor p = P(inner: 0.1, outer: 0.1, shape: StickShape.Square);
        (float x, float y) = p.Process(1, 0.05f);
        Assert.Equal(1f, x, Eps);
        Assert.Equal(0f, y, Eps);

        (x, y) = p.Process(-1, -1);
        Assert.Equal(-1f, x, Eps);
        Assert.Equal(-1f, y, Eps);
    }

    [Fact]
    public void Raw_extremes_map_to_full_range()
    {
        StickProcessor p = P(inner: 0.05, outer: 0.02);
        Assert.Equal(1f, p.Process(new Stick(255, 128)).X, Eps);
        Assert.Equal(-1f, p.Process(new Stick(0, 128)).X, Eps);
        Assert.Equal(1f, p.Process(new Stick(128, 0)).Y, Eps);
    }

    [Fact]
    public void Exponential_and_custom_curves()
    {
        Assert.Equal(0.25f, P(inner: 0, outer: 0, curve: CurveKind.Exponential, exponent: 2).Process(0.5f, 0).X, Eps);

        double[][] points = [[0, 0], [0.25, 0.1], [0.5, 0.2], [0.75, 0.6], [1, 1]];
        StickProcessor custom = P(inner: 0, outer: 0, curve: CurveKind.Custom, points: points);
        Assert.Equal(0.2f, custom.Process(0.5f, 0).X, Eps);
        Assert.Equal(0.4f, custom.Process(0.625f, 0).X, Eps);
    }

    [Fact]
    public void Anti_deadzone_jumps_past_the_game_deadzone()
    {
        StickProcessor p = P(inner: 0.1, outer: 0, anti: 0.2);
        Assert.Equal(0f, p.Process(0.05f, 0).X, Eps);
        Assert.Equal(0.2f, p.Process(0.1001f, 0).X, 1e-3f);
        Assert.Equal(1f, p.Process(1, 0).X, Eps);
    }

    [Fact]
    public void Invert_axes()
    {
        (float x, float y) = P(inner: 0, outer: 0, invertX: true, invertY: true).Process(0.5f, -0.25f);
        Assert.Equal(-0.5f, x, Eps);
        Assert.Equal(0.25f, y, Eps);
    }

    [Fact]
    public void Trigger_response()
    {
        var linear = new TriggerResponseProcessor(new TriggerResponse { Deadzone = 10, Max = 210 });
        Assert.Equal(0, linear.Process(0));
        Assert.Equal(0, linear.Process(10));
        Assert.Equal(128, linear.Process(110));
        Assert.Equal(255, linear.Process(210));
        Assert.Equal(255, linear.Process(255));

        var exp = new TriggerResponseProcessor(new TriggerResponse { Curve = CurveKind.Exponential, Exponent = 2 });
        Assert.Equal(64, exp.Process(128));

        Assert.Equal(200, new TriggerResponseProcessor().Process(200));
    }

    [Fact]
    public void Custom_curve_points_must_be_valid()
    {
        Assert.True(ResponseCurve.IsValid([[0, 0], [0.2, 0.5], [0.4, 0.6], [0.8, 0.9], [1, 1]]));
        Assert.False(ResponseCurve.IsValid([[0, 0], [0.5, 0.5], [0.4, 0.6], [0.8, 0.9], [1, 1]]));
        Assert.False(ResponseCurve.IsValid([[0, 0.1], [0.2, 0.5], [0.4, 0.6], [0.8, 0.9], [1, 1]]));
        Assert.False(ResponseCurve.IsValid([[0, 0], [1, 1]]));
        // Invalid points behave as linear.
        Assert.Equal(0.3, ResponseCurve.Apply(CurveKind.Custom, 1, null, 0.3), 6);
    }
}
