using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Host;

internal sealed class MainWindow : Form
{
    private const string AppHost = "app.tactile";
    private static readonly Color Background = Color.FromArgb(0x16, 0x17, 0x19);

    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private readonly ControllerService _service = new();
    private readonly System.Windows.Forms.Timer _inputTimer = new() { Interval = 33 };
    private Bridge? _bridge;
    private volatile InputState? _pendingInput;

    public MainWindow()
    {
        Text = "Tactile";
        BackColor = Background;
        MinimumSize = new Size(960, 620);
        Size = new Size(1240, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_view);
        Load += async (_, _) => await InitializeViewAsync();
        FormClosed += (_, _) =>
        {
            _inputTimer.Dispose();
            _service.Dispose();
        };

        _service.Input += s => _pendingInput = s;
        _service.ConnectionChanged += _ => BeginInvokeSafe(() => _bridge?.PostStatus());
        _inputTimer.Tick += (_, _) => FlushInput();
    }

    private static bool IsDev => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TACTILE_UI_DEV"));

    private async Task InitializeViewAsync()
    {
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tactile", "WebView2");
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await _view.EnsureCoreWebView2Async(env);

        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = IsDev;

        _bridge = new Bridge(_service, json => BeginInvokeSafe(() => core.PostWebMessageAsJson(json)));
        core.WebMessageReceived += (_, e) => _bridge.Handle(e.WebMessageAsJson);
        _inputTimer.Start();

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

    private void FlushInput()
    {
        InputState? s = Interlocked.Exchange(ref _pendingInput, null);
        DualSenseController? c = _service.Controller;
        if (s is not null && c is not null)
        {
            _bridge?.PostInput(s, c.Stats, c.Details.Calibration);
        }
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
