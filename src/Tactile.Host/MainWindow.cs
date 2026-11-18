using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Tactile.Host;

internal sealed class MainWindow : Form
{
    private const string AppHost = "app.tactile";
    private static readonly Color Background = Color.FromArgb(0x16, 0x17, 0x19);

    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };

    public MainWindow()
    {
        Text = "Tactile";
        BackColor = Background;
        MinimumSize = new Size(960, 620);
        Size = new Size(1240, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_view);
        Load += async (_, _) => await InitializeViewAsync();
    }

    private async Task InitializeViewAsync()
    {
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tactile", "WebView2");
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await _view.EnsureCoreWebView2Async(env);

        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = IsDev;
        core.WebMessageReceived += OnWebMessage;

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

    private static bool IsDev => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TACTILE_UI_DEV"));

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
        if (message.RootElement.TryGetProperty("type", out JsonElement type) && type.GetString() == "hello")
        {
            Post(new { type = "host", version = Application.ProductVersion, controllers = 0 });
        }
    }

    private void Post(object message) => _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
}
