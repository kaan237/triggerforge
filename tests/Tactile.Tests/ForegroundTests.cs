using Tactile.Core;

namespace Tactile.Tests;

public class ForegroundTests
{
    [Theory]
    [InlineData("GameWindow", false, 0, 0, true)]
    [InlineData("GameWindow", true, 0, 0, false)]
    [InlineData("GameWindow", false, 0x00C00000, 0, false)]
    [InlineData("GameWindow", false, 0x00040000, 0, false)]
    [InlineData("GameWindow", false, 0, -1, false)]
    [InlineData("GameWindow", false, 0, 1, false)]
    [InlineData("Progman", false, 0, 0, false)]
    [InlineData("WorkerW", false, 0, 0, false)]
    [InlineData("Shell_TrayWnd", false, 0, 0, false)]
    [InlineData("Shell_SecondaryTrayWnd", false, 0, 0, false)]
    [InlineData("XamlExplorerHostIslandWindow", false, 0, 0, false)]
    [InlineData("MultitaskingViewFrame", false, 0, 0, false)]
    [InlineData("Windows.UI.Core.CoreWindow", false, 0, 0, false)]
    public void Fullscreen_requires_a_borderless_exact_match_outside_the_shell(
        string windowClass, bool maximized, int style, int extraRight, bool expected)
    {
        var monitor = new WindowsForegroundSource.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        WindowsForegroundSource.Rect bounds = monitor;
        bounds.Right += extraRight;

        Assert.Equal(expected, WindowsForegroundSource.CoversMonitor(windowClass, maximized, style, bounds, monitor));
    }
}
