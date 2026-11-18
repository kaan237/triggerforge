using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tactile.Core;

namespace Tactile.Host;

/// <summary>Notification area icon: tooltip, profile menu, balloons. UI thread only.</summary>
internal sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripSeparator _profilesEnd = new();
    private readonly List<ToolStripMenuItem> _profileItems = [];
    private readonly Action<string> _activate;
    private string _text = "";

    public Tray(Icon icon, Action show, Action<string> activate, Action exit)
    {
        _activate = activate;
        var showItem = new ToolStripMenuItem("Göster", null, (_, _) => show());
        showItem.Font = new Font(showItem.Font, FontStyle.Bold);
        _menu.Items.Add(showItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_profilesEnd);
        _menu.Items.Add(new ToolStripMenuItem("Çıkış", null, (_, _) => exit()));

        _icon = new NotifyIcon { Icon = icon, Text = "Tactile", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => show();
    }

    public void SetProfiles(IReadOnlyList<Profile> profiles, string activeId)
    {
        foreach (ToolStripMenuItem item in _profileItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }

        _profileItems.Clear();
        int at = _menu.Items.IndexOf(_profilesEnd);
        foreach (Profile p in profiles)
        {
            string id = p.Id;
            var item = new ToolStripMenuItem(p.Name.Replace("&", "&&", StringComparison.Ordinal), null, (_, _) => _activate(id))
            {
                Checked = id == activeId,
            };
            _menu.Items.Insert(at++, item);
            _profileItems.Add(item);
        }
    }

    public void SetStatus(string? controller, int? battery, bool charging)
    {
        string text = controller is null
            ? "Tactile\nKontrolcü bağlı değil"
            : battery is int b
                ? $"Tactile\n{controller} · %{b}{(charging ? " (şarjda)" : "")}"
                : $"Tactile\n{controller}";
        if (text.Length > 127)
        {
            text = text[..127];
        }

        if (text != _text)
        {
            _text = text;
            _icon.Text = text;
        }
    }

    public void Balloon(string text, ToolTipIcon kind = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(5000, "Tactile", text, kind);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

/// <summary>The app glyph, drawn in code: a blue pad with a white light strip and two buttons.</summary>
internal sealed partial class Glyph : IDisposable
{
    private readonly IntPtr _handle;

    public Glyph(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 16f;
            using var body = new SolidBrush(Color.FromArgb(0x30, 0x60, 0xFF));
            using var white = new SolidBrush(Color.White);
            using (GraphicsPath pad = RoundedRect(new RectangleF(0.5f * s, 3.5f * s, 15 * s, 7 * s), 3 * s))
            {
                g.FillPath(body, pad);
            }

            // Grips.
            g.FillEllipse(body, 0.5f * s, 6.5f * s, 5.5f * s, 8.5f * s);
            g.FillEllipse(body, 10 * s, 6.5f * s, 5.5f * s, 8.5f * s);
            // Light strip and buttons.
            g.FillRectangle(white, 5.5f * s, 4.5f * s, 5 * s, 1.2f * s);
            g.FillEllipse(white, 2.5f * s, 6.5f * s, 2.5f * s, 2.5f * s);
            g.FillEllipse(white, 11 * s, 6.5f * s, 2.5f * s, 2.5f * s);
        }

        _handle = bmp.GetHicon();
        Icon = Icon.FromHandle(_handle);
    }

    public Icon Icon { get; }

    public void Dispose()
    {
        Icon.Dispose();
        DestroyIcon(_handle);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
