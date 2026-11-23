using System.Drawing.Drawing2D;
using Tactile.Core;

namespace Tactile.Host;

/// <summary>
/// Hands the newest value to a render callback, with at most one pending post. The post action runs the
/// callback on the UI thread; values that arrive meanwhile replace each other.
/// </summary>
internal sealed class CoalescedRender<T>(Action<Action> post, Action<T> render) where T : class
{
    private volatile T? _latest;
    private int _pending;

    public void Submit(T value)
    {
        _latest = value;
        if (Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        try
        {
            post(Drain);
        }
        catch
        {
            // A post that never ran must not block the next one.
            Volatile.Write(ref _pending, 0);
            throw;
        }
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_latest is { } value)
        {
            render(value);
        }
    }
}

/// <summary>Controls line of the keyboard window.</summary>
internal static class KeyboardHint
{
    public static string Text() => Tactile.Core.Text.Pick(
        "✕ Type · ○ Backspace · △ Space · □ Shift · Options Enter · PS Close",
        "✕ Yaz · ○ Sil · △ Boşluk · □ Büyük/Küçük · Options Enter · PS Kapat");
}

/// <summary>The on-screen keyboard: grid, highlighted cell and the controls line. Draws what the core says.</summary>
internal sealed class OnScreenKeyboardWindow : OverlayWindow, IOnScreenKeyboardView
{
    private const string FontFamily = "Segoe UI";
    private const string SymbolFamily = "Segoe UI Symbol";
    private const byte KeyboardAlpha = 245;

    private OnScreenKeyboardState? _state;
    private KeyboardMetrics _metrics;
    private int _rows;
    private int _columns;
    private int _dpi = OverlayLayout.BaseDpi;
    private int _radius;
    private string _hintFull = "";
    private string _hint = "";
    private Font? _keyFont;
    private Font? _selectedFont;
    private Font? _hintFont;
    private int _fontKey;

    public void Render(OnScreenKeyboardState state)
    {
        if (!state.Visible || state.Grid.Count == 0)
        {
            _state = null;
            HideOverlay();
            return;
        }

        int rows = state.Grid.Count;
        int columns = state.Grid.Max(r => r.Count);
        _state = state;
        bool relayout = !IsShown || rows != _rows || columns != _columns;
        if (relayout)
        {
            MonitorArea area = ForegroundMonitor();
            _dpi = area.Dpi;
            _rows = rows;
            _columns = columns;
            _metrics = OverlayLayout.Keyboard(area.WorkArea, rows, columns, _dpi);
            _radius = OverlayLayout.Scale(12, _dpi);
            EnsureFonts();
            _hintFull = "";
            ShowUnactivated(OverlayLayout.KeyboardBounds(area.WorkArea, _metrics.Size, _dpi), KeyboardAlpha, _radius);
        }

        string hint = KeyboardHint.Text();
        if (hint != _hintFull)
        {
            _hintFull = hint;
            int width = _metrics.Size.Width - 2 * _metrics.Pad;
            _hint = OverlayLayout.Ellipsize(hint, width, s => Measure(s, _hintFont!));
        }

        Invalidate();
    }

    void IOnScreenKeyboardView.Hide() => HideOverlay();

    private void EnsureFonts()
    {
        int key = _metrics.Cell * 10000 + _dpi;
        if (_fontKey == key && _keyFont is not null)
        {
            return;
        }

        _keyFont?.Dispose();
        _selectedFont?.Dispose();
        _hintFont?.Dispose();
        int keyPixels = Math.Max(8, _metrics.Cell * 42 / 100);
        _keyFont = new Font(FontFamily, keyPixels, FontStyle.Regular, GraphicsUnit.Pixel);
        _selectedFont = new Font(FontFamily, keyPixels, FontStyle.Bold, GraphicsUnit.Pixel);
        _hintFont = new Font(SymbolFamily, OverlayLayout.FontPixels(10, _dpi), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontKey = key;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_state is not { } state || _keyFont is null || _selectedFont is null || _hintFont is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        g.Clear(Surface);
        using (var border = new Pen(LineStrong))
        using (var frame = RoundedRectangle(new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), _radius))
        {
            g.DrawPath(border, frame);
        }

        int cellRadius = OverlayLayout.Scale(8, _dpi);
        using var cellBrush = new SolidBrush(SurfaceRaised);
        using var selectedBrush = new SolidBrush(Ink);
        using var cellBorder = new Pen(LineStrong);
        for (int row = 0; row < state.Grid.Count; row++)
        {
            IReadOnlyList<string> keys = state.Grid[row];
            for (int column = 0; column < keys.Count; column++)
            {
                Rectangle cell = OverlayLayout.KeyboardCell(_metrics, _columns, keys.Count, row, column);
                bool selected = row == state.Row && column == state.Column;
                using GraphicsPath path = RoundedRectangle(cell, cellRadius);
                g.FillPath(selected ? selectedBrush : cellBrush, path);
                if (!selected)
                {
                    g.DrawPath(cellBorder, path);
                }

                TextRenderer.DrawText(g, keys[column], selected ? _selectedFont : _keyFont, cell, selected ? Graphite : Ink,
                    LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        int barTop = bounds.Height - _metrics.Hint - _metrics.Pad / 2;
        g.DrawLine(cellBorder, _metrics.Pad, barTop, bounds.Width - _metrics.Pad, barTop);
        var bar = new Rectangle(_metrics.Pad, barTop, bounds.Width - 2 * _metrics.Pad, _metrics.Hint);
        TextRenderer.DrawText(g, _hint, _hintFont, bar, Muted,
            LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keyFont?.Dispose();
            _selectedFont?.Dispose();
            _hintFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>What the keyboard window offers the app.</summary>
internal interface IOnScreenKeyboardView : IDisposable
{
    void Render(OnScreenKeyboardState state);

    void Hide();
}
