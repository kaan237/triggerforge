using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tactile.Core;

namespace Tactile.Host;

/// <summary>The monitor an overlay opens on: its work area in pixels and its effective DPI.</summary>
internal readonly record struct MonitorArea(Rectangle WorkArea, int Dpi);

/// <summary>Cell geometry of the on-screen keyboard, all in device pixels.</summary>
internal readonly record struct KeyboardMetrics(int Cell, int Gap, int Pad, int Hint, Size Size);

/// <summary>Placement and sizing maths for the overlay windows. Pure, so it is tested without a window.</summary>
internal static class OverlayLayout
{
    public const int BaseDpi = 96;
    public const int OsdMargin = 24;
    public const int KeyboardBottomMargin = 48;

    public static int Scale(int value, int dpi) =>
        (int)Math.Round(value * (double)Math.Max(dpi, 1) / BaseDpi, MidpointRounding.AwayFromZero);

    /// <summary>Card size at 96 DPI: Small 240x56, Medium 300x68, Large 380x84; scaled to the monitor.</summary>
    public static Size OsdCardSize(OsdSize size, int dpi)
    {
        (int w, int h) = size switch
        {
            OsdSize.Small => (240, 56),
            OsdSize.Large => (380, 84),
            _ => (300, 68),
        };
        return new Size(Scale(w, dpi), Scale(h, dpi));
    }

    /// <summary>Detail line size in points: 10, 12 or 14.</summary>
    public static int OsdPoints(OsdSize size) => size switch
    {
        OsdSize.Small => 10,
        OsdSize.Large => 14,
        _ => 12,
    };

    /// <summary>A font size in points as pixels on a monitor with this DPI.</summary>
    public static int FontPixels(double points, int dpi) =>
        Math.Max(1, (int)Math.Round(points * Math.Max(dpi, 1) / 72.0, MidpointRounding.AwayFromZero));

    /// <summary>Opacity percent (30..100) as the layered window alpha (77..255).</summary>
    public static byte Alpha(int opacityPercent) =>
        (byte)Math.Round(255 * Math.Clamp(opacityPercent, 30, 100) / 100.0, MidpointRounding.AwayFromZero);

    public static Rectangle OsdBounds(Rectangle work, OsdCorner corner, Size size, int dpi)
    {
        int margin = Scale(OsdMargin, dpi);
        bool right = corner is OsdCorner.TopRight or OsdCorner.BottomRight;
        bool bottom = corner is OsdCorner.BottomLeft or OsdCorner.BottomRight;
        int x = right ? work.Right - margin - size.Width : work.Left + margin;
        int y = bottom ? work.Bottom - margin - size.Height : work.Top + margin;
        x = Math.Max(work.Left, Math.Min(x, work.Right - size.Width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - size.Height));
        return new Rectangle(new Point(x, y), size);
    }

    /// <summary>Cell and bar sizes for a grid; the cell shrinks when the work area is too small for 52 px.</summary>
    public static KeyboardMetrics Keyboard(Rectangle work, int rows, int columns, int dpi)
    {
        rows = Math.Max(rows, 1);
        columns = Math.Max(columns, 1);
        int margin = Scale(OsdMargin, dpi);
        int pad = Scale(16, dpi);
        int gap = Scale(6, dpi);
        int hint = Scale(36, dpi);
        int availableWidth = work.Width - 2 * margin - 2 * pad - (columns - 1) * gap;
        int availableHeight = work.Height - margin - Scale(KeyboardBottomMargin, dpi) - 2 * pad - hint - (rows - 1) * gap;
        int cell = Math.Max(1, Math.Min(Scale(52, dpi), Math.Min(availableWidth / columns, availableHeight / rows)));
        var size = new Size(columns * cell + (columns - 1) * gap + 2 * pad, rows * cell + (rows - 1) * gap + 2 * pad + hint);
        return new KeyboardMetrics(cell, gap, pad, hint, size);
    }

    /// <summary>Centred horizontally, 48 px (scaled) above the bottom of the work area.</summary>
    public static Rectangle KeyboardBounds(Rectangle work, Size size, int dpi)
    {
        int x = work.Left + (work.Width - size.Width) / 2;
        int y = work.Bottom - Scale(KeyboardBottomMargin, dpi) - size.Height;
        x = Math.Max(work.Left, Math.Min(x, work.Right - size.Width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - size.Height));
        return new Rectangle(new Point(x, y), size);
    }

    /// <summary>Cell rectangle inside the keyboard; a short row is centred under the longest one.</summary>
    public static Rectangle KeyboardCell(KeyboardMetrics m, int columns, int rowLength, int row, int column)
    {
        int step = m.Cell + m.Gap;
        int gridWidth = columns * m.Cell + (columns - 1) * m.Gap;
        int rowWidth = rowLength * m.Cell + (rowLength - 1) * m.Gap;
        return new Rectangle(m.Pad + (gridWidth - rowWidth) / 2 + column * step, m.Pad + row * step, m.Cell, m.Cell);
    }

    /// <summary>Shortens <paramref name="text"/> with an ellipsis until <paramref name="measure"/> fits it in the width.</summary>
    public static string Ellipsize(string text, int maxWidth, Func<string, int> measure)
    {
        if (text.Length == 0 || measure(text) <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        int low = 0;
        int high = text.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (measure(Cut(text, mid) + ellipsis) <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Cut(text, low) + ellipsis;
    }

    private static string Cut(string text, int length)
    {
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length].TrimEnd();
    }
}

/// <summary>
/// Base of the on-screen overlays: a layered, click-through, no-activate, topmost tool window that
/// never takes focus. Shown with <c>SWP_NOACTIVATE</c> and <c>HWND_TOPMOST</c>; opacity is always set
/// explicitly. UI thread only.
/// </summary>
internal abstract partial class OverlayWindow : Form
{
    internal const int WsExLayered = 0x00080000;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExTopmost = 0x00000008;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmDpiChanged = 0x02E0;
    internal const int MaNoActivate = 3;
    internal const int HtTransparent = -1;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint LwaAlpha = 0x2;
    private const int SwHide = 0;
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToPrimary = 1;
    private const int EffectiveDpi = 0;
    private static readonly nint HwndTopmost = -1;

    protected static readonly Color Surface = Color.FromArgb(0x1b, 0x1c, 0x1f);
    protected static readonly Color SurfaceRaised = Color.FromArgb(0x21, 0x22, 0x26);
    protected static readonly Color LineStrong = Color.FromArgb(0x38, 0x3a, 0x3f);
    protected static readonly Color Ink = Color.FromArgb(0xe6, 0xe4, 0xe0);
    protected static readonly Color Muted = Color.FromArgb(0x8a, 0x8c, 0x91);
    protected static readonly Color Accent = Color.FromArgb(0x9a, 0x9c, 0xa1);
    protected static readonly Color Graphite = Color.FromArgb(0x16, 0x17, 0x19);
    protected const TextFormatFlags LineFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private bool _shown;

    protected OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Surface;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost;
            return cp;
        }
    }

    /// <summary>Test access to the extended style without creating a handle.</summary>
    internal int ExStyle => CreateParams.ExStyle;

    internal bool WithoutActivation => ShowWithoutActivation;

    internal bool IsShown => _shown;

    /// <summary>The pure part of <see cref="WndProc"/>: messages that keep the overlay passive.</summary>
    internal static bool TryHandleMessage(int message, out nint result)
    {
        switch (message)
        {
            case WmMouseActivate:
                result = MaNoActivate;
                return true;
            case WmNcHitTest:
                result = HtTransparent;
                return true;
            case WmDpiChanged:
                // Sizes are computed per monitor on every show; the suggested rectangle would fight that.
                result = 0;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (TryHandleMessage(m.Msg, out nint result))
        {
            m.Result = result;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Shows or moves the window without activating it, with an explicit alpha and a rounded shape.</summary>
    protected void ShowUnactivated(Rectangle bounds, byte alpha, int cornerRadius)
    {
        nint handle = Handle;
        _ = SetLayeredWindowAttributes(handle, 0, alpha, LwaAlpha);

        // The window owns the region once it is set.
        nint region = CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, cornerRadius * 2, cornerRadius * 2);
        if (SetWindowRgn(handle, region, true) == 0)
        {
            _ = DeleteObject(region);
        }

        _ = SetWindowPos(handle, HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpShowWindow);
        _shown = true;
    }

    protected void HideOverlay()
    {
        if (_shown && IsHandleCreated)
        {
            _ = ShowWindow(Handle, SwHide);
        }

        _shown = false;
    }

    /// <summary>The monitor of the foreground window; the primary monitor when there is none.</summary>
    internal static MonitorArea ForegroundMonitor()
    {
        nint monitor = MonitorFromWindow(GetForegroundWindow(), MonitorDefaultToNull);
        if (monitor == 0 || !TryWorkArea(monitor, out Rectangle work))
        {
            monitor = MonitorFromPoint(default(NativePoint), MonitorDefaultToPrimary);
            if (monitor == 0 || !TryWorkArea(monitor, out work))
            {
                return new MonitorArea(Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720), OverlayLayout.BaseDpi);
            }
        }

        int dpi = GetDpiForMonitor(monitor, EffectiveDpi, out uint x, out _) == 0 && x > 0 ? (int)x : OverlayLayout.BaseDpi;
        return new MonitorArea(work, dpi);
    }

    private static bool TryWorkArea(nint monitor, out Rectangle work)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            work = Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            return work.Width > 0 && work.Height > 0;
        }

        work = default;
        return false;
    }

    protected static GraphicsPath RoundedRectangle(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected static int Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), LineFlags).Width;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
