using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tactile.Core;
using Tactile.Core.Logging;
using Tactile.Core.Updates;

namespace Tactile.Host;

/// <summary>The WebView2 window. The view is created on first show, so a tray start stays light.</summary>
internal sealed class MainWindow : Form
{
    private const string AppHost = "app.tactile";
    private static readonly Color Background = Color.FromArgb(0x16, 0x17, 0x19);

    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private readonly AppPaths _paths;

    public MainWindow(AppCore core, Icon icon, AppPaths paths, UpdateService updates)
    {
        _paths = paths;
        Text = "TriggerForge";
        Icon = icon;
        BackColor = Background;
        MinimumSize = new Size(960, 620);
        Size = new Size(1240, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_view);
        Bridge = new Bridge(core, this, PostRaw, paths, updates);
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
            Log.Warn("WebView2 could not receive a message.", e);
        }
    }

    private async Task InitializeViewAsync()
    {
        try
        {
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebViewDirectory);
            await _view.EnsureCoreWebView2Async(env);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("WebView2 failed to initialize.", e);
            MessageBox.Show(this, Tactile.Core.Text.Pick("Could not start WebView2. Install the Microsoft Edge WebView2 Runtime.", "WebView2 başlatılamadı. Microsoft Edge WebView2 Runtime kurulu olmalı."), "TriggerForge",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = IsDev;

        string? devUrl = Environment.GetEnvironmentVariable("TACTILE_UI_DEV");
        var appUri = new Uri(string.IsNullOrEmpty(devUrl) ? $"https://{AppHost}/index.html" : devUrl);
        core.NavigationStarting += (_, _) => Bridge.Detach();
        core.WebMessageReceived += (_, e) =>
        {
            bool appSource = Uri.TryCreate(e.Source, UriKind.Absolute, out Uri? source) &&
                Uri.Compare(source, appUri, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
            Bridge.Handle(e.WebMessageAsJson, appSource);
        };

        if (!string.IsNullOrEmpty(devUrl))
        {
            core.Navigate(devUrl);
            return;
        }

        string uiFolder = Path.Combine(AppContext.BaseDirectory, "ui");
        core.SetVirtualHostNameToFolderMapping(AppHost, uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(appUri.AbsoluteUri);
    }
}
