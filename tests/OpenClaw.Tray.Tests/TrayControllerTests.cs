namespace OpenClaw.Tray.Tests;

public sealed class TrayControllerTests
{
    [Fact]
    public void Initialize_ClickRefreshReopenAndDispose_PreservesSingleOwnership()
    {
        var controller = ReadController();

        Assert.Contains("if (_initialized || _isClosing)", controller);
        Assert.Contains("_trayIcon.Selected += OnTrayIconSelected", controller);
        Assert.Contains("_trayIcon.ContextMenu += OnTrayContextMenu", controller);
        Assert.Contains("menu.MenuItemClicked += OnTrayMenuItemClicked", controller);
        Assert.Contains("if (_callbacks.IsOperatorConnected())", controller);
        Assert.Contains("_callbacks.ShowChat();", controller);
        Assert.Contains("_callbacks.ShowConnection();", controller);
        Assert.Contains("menu.ClearItems();", controller);
        Assert.Contains("menu.ShowAtCursor();", controller);
        Assert.Contains("() => !_disposed && _trayIcon != null", controller);
        Assert.Contains("_connectionToggleRef = null;", controller);
    }

    [Fact]
    public void Dispose_UnsubscribesAndDisposesEachResourceOnce()
    {
        var controller = ReadController();

        Assert.Contains("public void BeginShutdown() => _isClosing = true;", controller);
        Assert.Contains("if (_disposed)", controller);
        Assert.Contains("menu.MenuItemClicked -= OnTrayMenuItemClicked", controller);
        Assert.Contains("icon.Selected -= OnTrayIconSelected", controller);
        Assert.Contains("icon.ContextMenu -= OnTrayContextMenu", controller);
        Assert.Contains("throw new AggregateException", controller);
        Assert.Equal(1, Count(controller, "menu.CloseCascadeForShutdown();"));
        Assert.Equal(1, Count(controller, "icon.Dispose();"));
        Assert.DoesNotContain("case \"", controller);
    }

    [Fact]
    public void ConnectionRefresh_UsesA1ProjectionAndHidesTerminalMenu()
    {
        var controller = ReadController();

        Assert.Contains("ConnectionTogglePresenter.Present(status, overallState)", controller);
        Assert.Contains("_suspendConnectionToggleEvent = true;", controller);
        Assert.Contains("_suspendConnectionToggleEvent = false;", controller);
        Assert.Contains("ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error", controller);
        Assert.Contains("HideMenu();", controller);
    }

    private static string ReadController() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Services",
        "TrayController.cs"));

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
