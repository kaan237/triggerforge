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
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _restoreItem;
    private readonly ToolStripMenuItem _exitItem;
    private string _text = "";

    public Tray(Icon icon, Action show, Action<string> activate, Action restore, Action exit)
    {
        _activate = activate;
        _showItem = new ToolStripMenuItem("", null, (_, _) => show());
        _showItem.Font = new Font(_showItem.Font, FontStyle.Bold);
        _menu.Items.Add(_showItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_profilesEnd);
        _restoreItem = new ToolStripMenuItem("", null, (_, _) => restore());
        _menu.Items.Add(_restoreItem);
        _menu.Items.Add(new ToolStripSeparator());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => exit());
        _menu.Items.Add(_exitItem);
        UpdateLanguage();

        _icon = new NotifyIcon { Icon = icon, Text = "TriggerForge", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => show();
    }

    public void UpdateLanguage()
    {
        _showItem.Text = Text.Pick("Show", "Göster");
        _restoreItem.Text = Text.Pick("Restore controller", "Kontrolcüyü geri getir");
        _exitItem.Text = Text.Pick("Exit", "Çıkış");
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
            ? Text.Pick("TriggerForge\nNo controller connected", "TriggerForge\nKontrolcü bağlı değil")
            : battery is int b
                ? Text.Pick($"TriggerForge\n{controller} · {b}%{(charging ? " (charging)" : "")}",
                    $"TriggerForge\n{controller} · %{b}{(charging ? " (şarjda)" : "")}")
                : $"TriggerForge\n{controller}";
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
        _icon.ShowBalloonTip(5000, "TriggerForge", text, kind);

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

/// <summary>The app logo at one size, taken from the icon embedded in the exe.</summary>
internal sealed class AppIcon : IDisposable
{
    public AppIcon(int size)
    {
        using Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream("TriggerForge.ico")
            ?? throw new InvalidOperationException("The app icon resource is missing.");
        // Icon picks the frame in the file that is closest to the requested size.
        Icon = new Icon(stream, new Size(size, size));
    }

    public Icon Icon { get; }

    public void Dispose() => Icon.Dispose();
}
