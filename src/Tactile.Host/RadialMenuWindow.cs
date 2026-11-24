using System.Drawing.Drawing2D;
using Tactile.Core;

namespace Tactile.Host;

/// <summary>The radial menu: a disc of wedges with the picked one lit. Draws what the core says.</summary>
internal sealed class RadialMenuWindow : OverlayWindow, IRadialMenuView
{
    private const string FontFamily = "Segoe UI";
    private const byte RadialAlpha = 245;

    private RadialMenuState? _state;
    private RadialMetrics _metrics;
    private int _count;
    private string _menuId = "";
    private int _dpi = OverlayLayout.BaseDpi;
    private Font? _labelFont;
    private Font? _selectedFont;
    private Font? _nameFont;
    private Font? _hintFont;
    private int _fontKey;

    public void Render(RadialMenuState state)
    {
        int count = state.Labels.Count;
        if (!state.Visible || count < 2)
        {
            _state = null;
            HideOverlay();
            return;
        }

        _state = state;
        if (!IsShown || count != _count || state.MenuId != _menuId)
        {
            MonitorArea area = ForegroundMonitor();
            _dpi = area.Dpi;
            _count = count;
            _menuId = state.MenuId;
            _metrics = OverlayLayout.Radial(area.WorkArea, _dpi);
            EnsureFonts();
            ShowUnactivated(OverlayLayout.RadialBounds(area.WorkArea, _metrics.Diameter), RadialAlpha, 0, elliptic: true);
        }

        Invalidate();
    }

    void IRadialMenuView.Hide() => HideOverlay();

    private void EnsureFonts()
    {
        int key = _metrics.Diameter * 10000 + _dpi;
        if (_fontKey == key && _labelFont is not null)
        {
            return;
        }

        _labelFont?.Dispose();
        _selectedFont?.Dispose();
        _nameFont?.Dispose();
        _hintFont?.Dispose();
        int pixels = Math.Max(8, _metrics.Diameter * 5 / 100);
        _labelFont = new Font(FontFamily, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        _selectedFont = new Font(FontFamily, pixels, FontStyle.Bold, GraphicsUnit.Pixel);
        _nameFont = new Font(FontFamily, Math.Max(8, pixels * 9 / 10), FontStyle.Bold, GraphicsUnit.Pixel);
        _hintFont = new Font(FontFamily, Math.Max(7, pixels * 7 / 10), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontKey = key;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_state is not { } state || _labelFont is null || _selectedFont is null || _nameFont is null || _hintFont is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surface);
        int d = _metrics.Diameter;
        var disc = new Rectangle(_metrics.Pad, _metrics.Pad, d - 2 * _metrics.Pad - 1, d - 2 * _metrics.Pad - 1);
        int count = state.Labels.Count;

        using var raised = new SolidBrush(SurfaceRaised);
        using var lit = new SolidBrush(Ink);
        using var dimLit = new SolidBrush(LineStrong);
        using var surface = new SolidBrush(Surface);
        using var border = new Pen(LineStrong);
        using var gap = new Pen(Surface, Math.Max(2, _metrics.Pad));
        for (int i = 0; i < count; i++)
        {
            bool assigned = IsAssigned(state, i);
            bool selected = state.SelectedIndex == i;
            (float start, float sweep) = OverlayLayout.RadialSlice(count, i);
            g.FillPie(selected ? (assigned ? lit : dimLit) : raised, disc, start, sweep);
        }

        // Spokes between the wedges, then the ring and the centre disc.
        double middle = d / 2.0;
        for (int i = 0; i < count; i++)
        {
            (float start, _) = OverlayLayout.RadialSlice(count, i);
            double a = start * Math.PI / 180;
            g.DrawLine(gap, (float)middle, (float)middle, (float)(middle + d * Math.Cos(a)), (float)(middle + d * Math.Sin(a)));
        }

        g.DrawEllipse(border, disc);
        int c = d / 2;
        int inner = _metrics.Inner;
        var centre = new Rectangle(c - inner, c - inner, 2 * inner, 2 * inner);
        g.FillEllipse(surface, centre);
        g.DrawEllipse(border, centre);

        for (int i = 0; i < count; i++)
        {
            bool assigned = IsAssigned(state, i);
            bool selected = state.SelectedIndex == i;
            string label = OverlayLayout.RadialLabel(state.Labels[i], i);
            Font font = selected ? _selectedFont : _labelFont;
            label = OverlayLayout.Ellipsize(label, _metrics.LabelWidth, s => Measure(s, font));
            Point at = OverlayLayout.RadialLabelCenter(_metrics, count, i);
            var box = new Rectangle(at.X - _metrics.LabelWidth / 2, at.Y - _metrics.LabelWidth / 4, _metrics.LabelWidth, _metrics.LabelWidth / 2);
            Color ink = !assigned ? Muted : selected ? Graphite : Ink;
            TextRenderer.DrawText(g, label, font, box, ink,
                LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        string name = string.IsNullOrWhiteSpace(state.Name) ? Tactile.Core.Text.Pick("Radial menu", "Daire menü") : state.Name.Trim();
        name = OverlayLayout.Ellipsize(name, 2 * inner - 2 * _metrics.Pad, s => Measure(s, _nameFont));
        TextRenderer.DrawText(g, name, _nameFont, new Rectangle(c - inner, c - inner / 2, 2 * inner, inner / 2), Ink,
            LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        if (state.SelectedIndex is null)
        {
            string hint = OverlayLayout.Ellipsize(Tactile.Core.Text.Pick("Release: cancel", "Bırak: iptal"), 2 * inner - 2 * _metrics.Pad,
                s => Measure(s, _hintFont));
            TextRenderer.DrawText(g, hint, _hintFont, new Rectangle(c - inner, c, 2 * inner, inner / 2), Muted,
                LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }

    private static bool IsAssigned(RadialMenuState state, int index) => index < state.Assigned.Count && state.Assigned[index];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _selectedFont?.Dispose();
            _nameFont?.Dispose();
            _hintFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>What the radial menu window offers the app.</summary>
internal interface IRadialMenuView : IDisposable
{
    void Render(RadialMenuState state);

    void Hide();
}
