using System.Drawing;
using Tactile.Core;
using Tactile.Host;

namespace Tactile.Tests;

public sealed class RadialMenuOverlayTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Radial_layout_centers_on_work_area_scales_with_dpi_and_clamps_small_monitors(int dpi)
    {
        var work = new Rectangle(100, 50, 1920, 1080);
        RadialMetrics m = OverlayLayout.Radial(work, dpi);
        Assert.Equal(OverlayLayout.Scale(360, dpi), m.Diameter);
        Rectangle bounds = OverlayLayout.RadialBounds(work, m.Diameter);
        Assert.Equal(work.Left + (work.Width - m.Diameter) / 2, bounds.X);
        Assert.Equal(work.Top + (work.Height - m.Diameter) / 2, bounds.Y);
        Assert.Equal(new Size(m.Diameter, m.Diameter), bounds.Size);

        // Labels sit on the label ring: slice 0 straight up, slice 1 of 4 to the right, slice 2 straight down.
        Point top = OverlayLayout.RadialLabelCenter(m, 4, 0);
        Point right = OverlayLayout.RadialLabelCenter(m, 4, 1);
        Point bottom = OverlayLayout.RadialLabelCenter(m, 4, 2);
        Assert.Equal(m.Diameter / 2, top.X);
        Assert.Equal(m.Diameter / 2 - m.LabelRadius, top.Y);
        Assert.Equal(m.Diameter / 2 + m.LabelRadius, right.X);
        Assert.Equal(m.Diameter / 2 + m.LabelRadius, bottom.Y);

        // A 640x480 work area shrinks the disc and keeps it inside.
        var small = new Rectangle(0, 0, 640, 480);
        RadialMetrics sm = OverlayLayout.Radial(small, dpi);
        Assert.True(sm.Diameter <= 480);
        Assert.True(small.Contains(OverlayLayout.RadialBounds(small, sm.Diameter)));
        Assert.True(sm.LabelRadius + sm.LabelWidth / 2 <= sm.Diameter / 2);
    }

    [Fact]
    public void Radial_label_shows_the_slice_number_when_the_label_is_empty()
    {
        Assert.Equal("Copy", OverlayLayout.RadialLabel("  Copy ", 0));
        Assert.Equal("1", OverlayLayout.RadialLabel("", 0));
        Assert.Equal("3", OverlayLayout.RadialLabel("   ", 2));
        Assert.Equal("8", OverlayLayout.RadialLabel(null, 7));
    }

    [Fact]
    public void Radial_slice_angles_match_core_selection()
    {
        foreach (int count in new[] { 2, 3, 4, 8 })
        {
            for (int i = 0; i < count; i++)
            {
                (float start, float sweep) = OverlayLayout.RadialSlice(count, i);
                double middle = (start + sweep / 2) * Math.PI / 180;
                // GDI+ angle (clockwise from 3 o'clock, y down) to stick coordinates (x right, y up).
                float x = (float)Math.Cos(middle);
                float y = -(float)Math.Sin(middle);
                Assert.Equal(i, RadialMenuController.Select(x, y, count, null));
            }
        }

        Assert.Equal(-135f, OverlayLayout.RadialSlice(4, 0).Start);
    }

    [Fact]
    public void Radial_window_preserves_passive_overlay_flags_and_coalesces_renders()
    {
        using var window = new RadialMenuWindow();
        const int flags = OverlayWindow.WsExLayered | OverlayWindow.WsExTransparent | OverlayWindow.WsExToolWindow
            | OverlayWindow.WsExNoActivate | OverlayWindow.WsExTopmost;
        Assert.Equal(flags, window.ExStyle & flags);
        Assert.True(window.WithoutActivation);
        Assert.False(window.IsHandleCreated);
        Assert.True(OverlayWindow.TryHandleMessage(OverlayWindow.WmMouseActivate, out nint activate));
        Assert.Equal(OverlayWindow.MaNoActivate, (int)activate);
        Assert.True(OverlayWindow.TryHandleMessage(OverlayWindow.WmNcHitTest, out nint hit));
        Assert.Equal(OverlayWindow.HtTransparent, (int)hit);

        var posted = new List<Action>();
        var drawn = new List<RadialMenuState>();
        var queue = new CoalescedRender<RadialMenuState>(posted.Add, drawn.Add);
        RadialMenuState Of(int? selected) => new(true, "m", "M", RadialStick.Left, ["a", "b"], selected, [true, false]);
        queue.Submit(Of(null));
        queue.Submit(Of(0));
        queue.Submit(Of(1));
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(1, Assert.Single(drawn).SelectedIndex);
    }
}
