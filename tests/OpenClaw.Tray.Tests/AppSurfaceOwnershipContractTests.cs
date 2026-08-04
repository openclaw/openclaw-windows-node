namespace OpenClaw.Tray.Tests;

public sealed class AppSurfaceOwnershipContractTests
{
    [Fact]
    public void App_DelegatesConcreteTrayAndWindowOwnership()
    {
        var app = Read("App.xaml.cs");

        Assert.Contains("private ITrayController? _trayController;", app);
        Assert.Contains("private IWindowManager? _windowManager;", app);
        Assert.Contains("_windowManager?.InitializeRuntimeAnchor()", app);
        Assert.Contains("_windowManager?.ShowHub(navigateTo, activate)", app);
        Assert.Contains("_windowManager?.ShowChat(new ChatWindowRequest(url, token))", app);
        Assert.Contains("new CanvasWindowRequest(", app);
        Assert.Contains("() => _windowManager?.DialogXamlRoot", app);
        Assert.DoesNotContain("() => _windowManager.DialogXamlRoot", app);
        Assert.Contains("windowManager.CloseForShutdownAsync", app);

        Assert.DoesNotContain("private HubWindow?", app);
        Assert.DoesNotContain("private ChatWindow?", app);
        Assert.DoesNotContain("private ConnectionStatusWindow?", app);
        Assert.DoesNotContain("private SetupWindow?", app);
        Assert.DoesNotContain("private Window? _keepAliveWindow", app);
        Assert.DoesNotContain("private TrayIcon?", app);
        Assert.DoesNotContain("private TrayMenuWindow?", app);
        Assert.DoesNotContain("new HubWindow(", app);
        Assert.DoesNotContain("new ChatWindow(", app);
        Assert.DoesNotContain("new ConnectionStatusWindow(", app);
        Assert.DoesNotContain("new SetupWindow(", app);
        Assert.DoesNotContain("new TrayIcon(", app);
        Assert.DoesNotContain("new TrayMenuWindow(", app);
        Assert.DoesNotContain("AddSingleton<ITrayController>", app);
        Assert.DoesNotContain("AddSingleton<IWindowManager>", app);

        AssertInOrder(
            app,
            "_windowManager?.InitializeRuntimeAnchor()",
            "_trayController.Initialize()");
        AssertInOrder(
            app,
            "\"window manager\"",
            "\"tray menu window\"",
            "var services = _services;",
            "_services = null;",
            "\"tray icon\"");
    }

    [Fact]
    public void WindowManager_OwnsConcreteWindowLifetimes()
    {
        var manager = Read("Services", "WindowManager.cs");

        Assert.Contains("private Window? _keepAliveWindow;", manager);
        Assert.Contains("private HubWindow? _hubWindow;", manager);
        Assert.Contains("private ChatWindow? _chatWindow;", manager);
        Assert.Contains("private ConnectionStatusWindow? _connectionStatusWindow;", manager);
        Assert.Contains("private SetupWindow? _setupWindow;", manager);
        Assert.Contains("new HubWindow()", manager);
        Assert.Contains("new ChatWindow(", manager);
        Assert.Contains("new ConnectionStatusWindow(", manager);
        Assert.Contains("new SetupWindow(", manager);
        Assert.Contains("Show(activateWindow: false)", manager);
        Assert.Contains("DispatcherQueuePriority.Low", manager);
        Assert.Contains("WaitForInitialContentReadyAsync()", manager);
        Assert.Contains("await existingSetupWindow.CleanupCompleted", manager);
        Assert.Contains("ResetNavigationScope()", manager);
        Assert.Contains("SettingsSaved -= _callbacks.SettingsSaved", manager);
        Assert.Contains("AdvancedSetupRequested -= _callbacks.AdvancedSetupRequested", manager);
        Assert.Contains("SetupCompleted -= _callbacks.SetupCompleted", manager);
        Assert.Contains("Closed -= OnSetupClosed", manager);
        Assert.Contains("public void BeginShutdown() => _isShuttingDown = true;", manager);
        Assert.Contains("return _closeForShutdownTask ??= CloseOwnedWindowsAsync();", manager);
        Assert.Contains("throw new AggregateException", manager);

        Assert.DoesNotContain("IActivationRouter", manager);
        Assert.DoesNotContain("ISettingsChangeCoordinator", manager);
        Assert.DoesNotContain("AppBootstrapper", manager);
        Assert.DoesNotContain("SettingsChangeImpact", manager);
        Assert.DoesNotContain("DisposeByUserAsync", manager);
        Assert.DoesNotContain("NodeService.Dispose", manager);
    }

    [Fact]
    public void TrayController_ConsumesA1PresentationAndLeavesPopupMechanicsInWindow()
    {
        var controller = Read("Services", "TrayController.cs");
        var window = Read("Windows", "TrayMenuWindow.xaml.cs");

        Assert.Contains("new TrayMenuPresenter(snapshot).Present()", controller);
        Assert.Contains("new TrayMenuRenderer(presentation, callbacks)", controller);
        Assert.Contains("ConnectionTogglePresenter.Present(status, overallState)", controller);
        Assert.Contains("icon.Selected -= OnTrayIconSelected", controller);
        Assert.Contains("icon.ContextMenu -= OnTrayContextMenu", controller);
        Assert.Contains("menu.MenuItemClicked -= OnTrayMenuItemClicked", controller);
        Assert.Contains("menu.CloseCascadeForShutdown()", controller);
        Assert.Contains("public void BeginShutdown() => _isClosing = true;", controller);
        Assert.Contains("if (_disposed || _isClosing)", controller);
        Assert.DoesNotContain("case \"", controller);
        Assert.DoesNotContain("GetWindowLong", controller);
        Assert.DoesNotContain("SetWindowPos", controller);
        Assert.DoesNotContain("MonitorFromPoint", controller);

        Assert.Contains("MonitorFromPoint", window);
        Assert.Contains("SetWindowPos", window);
        Assert.Contains("HideActiveFlyout()", window);
        Assert.Contains("CloseCascadeForShutdown()", window);
    }

    private static string Read(params string[] path)
    {
        var segments = new[]
        {
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
        }.Concat(path).ToArray();
        return File.ReadAllText(Path.Combine(segments));
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Expected to find '{fragment}'.");
            previous = current;
        }
    }
}
