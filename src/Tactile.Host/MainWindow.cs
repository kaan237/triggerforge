using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tactile.Core;

namespace Tactile.Host;

/// <summary>The WebView2 window. The view is created on first show, so a tray start stays light.</summary>
internal sealed class MainWindow : Form
{
    private const string AppHost = "app.tactile";
    private static readonly Color Background = Color.FromArgb(0x16, 0x17, 0x19);

    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };

    public MainWindow(AppCore core, Icon icon)
    {
        Text = "Tactile";
        Icon = icon;
        BackColor = Background;
        MinimumSize = new Size(960, 620);
        Size = new Size(1240, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_view);
        Bridge = new Bridge(core, PostRaw);
        Load += async (_, _) => await InitializeViewAsync();
    }

    public Bridge Bridge { get; }

    /// <summary>Visible and not minimized.</summary>
    public bool OnScreen => Visible && WindowState != FormWindowState.Minimized;

    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    private static bool IsDev => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TACTILE_UI_DEV"));

    private void PostRaw(string json)
    {
        try
        {
            _view.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // View torn down while closing.
        }
    }

    private async Task InitializeViewAsync()
    {
        try
        {
            string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tactile", "WebView2");
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await _view.EnsureCoreWebView2Async(env);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, "WebView2 başlatılamadı. Microsoft Edge WebView2 Runtime kurulu olmalı.", "Tactile",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = IsDev;

        core.NavigationStarting += (_, _) => Bridge.Detach();
        core.WebMessageReceived += (_, e) => Bridge.Handle(e.WebMessageAsJson);

        string? devUrl = Environment.GetEnvironmentVariable("TACTILE_UI_DEV");
        if (!string.IsNullOrEmpty(devUrl))
        {
            core.Navigate(devUrl);
            return;
        }

        string uiFolder = Path.Combine(AppContext.BaseDirectory, "ui");
        core.SetVirtualHostNameToFolderMapping(AppHost, uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate($"https://{AppHost}/index.html");
    }
}
