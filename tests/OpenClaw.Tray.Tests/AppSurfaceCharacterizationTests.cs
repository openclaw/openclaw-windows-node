namespace OpenClaw.Tray.Tests;

public sealed class AppSurfaceCharacterizationTests
{
    [Fact]
    public void TraySurface_PreservesCreationClickMenuRefreshAndDisposalBehavior()
    {
        var source = ReadSurfaceSources();

        Assert.Contains("new TrayIcon(1, iconPath, BuildTrayTooltip())", source);
        Assert.Contains(".Selected += OnTrayIconSelected", source);
        Assert.Contains(".ContextMenu += OnTrayContextMenu", source);
        Assert.Contains("CurrentSnapshot.OperatorState == RoleConnectionState.Connected", source);
        Assert.Contains("ShowChatWindow();", source);
        Assert.Contains("ShowHub(\"connection\")", source);
        Assert.Contains("new TrayMenuWindow()", source);
        Assert.Contains(".MenuItemClicked += OnTrayMenuItemClicked", source);
        Assert.Contains("ClearItems()", source);
        Assert.Contains("new TrayMenuPresenter(snapshot).Present()", source);
        Assert.Contains("new TrayMenuRenderer(presentation, callbacks)", source);
        Assert.Contains("ShowAtCursor()", source);
        Assert.Contains("new TrayTooltipBuilder(_callbacks.CaptureIconSnapshot()).Build()", source);
        Assert.Contains("ConnectionTogglePresenter.Present(status, overallState)", source);
        Assert.Contains("HideCascade()", source);
        Assert.Contains(".Dispose()", source);
    }

    [Fact]
    public void HubSurface_PreservesReuseRoutesFocusThemeAndNavigationReset()
    {
        var source = ReadSurfaceSources();

        Assert.Contains("if (_hubWindow is null || _hubWindow.IsClosed)", source);
        Assert.Contains("new HubWindow()", source);
        Assert.Contains("_callbacks.ApplyTheme(_hubWindow)", source);
        Assert.Contains("_hubWindow.NavigateTo(navigateTo)", source);
        Assert.Contains("WaitForCurrentContentReadyAsync()", source);
        Assert.Contains("hub.Activate()", source);
        Assert.Contains("Show(activateWindow: false)", source);
        Assert.Contains("_callbacks.GetPageActivator()?.Reset()", source);
        Assert.Contains("hub.SettingsSaved -= _callbacks.SettingsSaved", source);
    }

    [Fact]
    public void ChatCanvasAndStatusSurfaces_PreserveVisibleFallbackAndReuseBehavior()
    {
        var source = ReadSurfaceSources();

        Assert.Contains("TryResolveChatCredentials", source);
        Assert.Contains("ShowConnectionSettingsForPairingIssue", source);
        Assert.Contains("RefreshCredentials(request.GatewayUrl, request.GatewayToken)", source);
        Assert.Contains("HideNearTray()", source);
        Assert.Contains("ShowNearTrayAnimated()", source);
        Assert.Contains("DispatcherQueuePriority.Low", source);

        Assert.Contains("_settings?.NodeCanvasEnabled == false", source);
        Assert.Contains("showHub(\"capabilities\")", source);
        Assert.Contains("_nodeService.IsPendingApproval || !_nodeService.IsPaired", source);
        Assert.Contains("CanvasSurfaceDestination.Capabilities", source);
        Assert.Contains("CanvasSurfaceDestination.Connection", source);
        Assert.Contains("CanvasSurfaceDestination.Canvas", source);
        Assert.Contains("_nodeService.ShowCanvasWindow", source);

        Assert.Contains("_connectionStatusWindow is { IsClosed: false }", source);
        Assert.Contains("new ConnectionStatusWindow(", source);
        Assert.Contains("_connectionStatusWindow.Activate()", source);
    }

    [Fact]
    public void SetupSurface_PreservesReadinessCleanupReuseAndDirectMilestoneBehavior()
    {
        var source = ReadSurfaceSources();

        Assert.Contains("EnsureSetupWindowAsync", source);
        Assert.Contains("WaitForInitialContentReadyAsync()", source);
        Assert.Contains("BringToFrontForSetupLaunch()", source);
        Assert.Contains("await existingSetupWindow.CleanupCompleted", source);
        Assert.Contains("new SetupWindow(", source);
        Assert.Contains("SetupWindowArgumentProjection.Project(", source);
        Assert.Contains("AdvancedSetupRequested += _callbacks.AdvancedSetupRequested", source);
        Assert.Contains("SetupCompleted += _callbacks.SetupCompleted", source);
        Assert.Contains("TryNavigateToGatewayInstalledMilestone()", source);
        Assert.Contains("AdvancedSetupRequested -= _callbacks.AdvancedSetupRequested", source);
        Assert.Contains("SetupCompleted -= _callbacks.SetupCompleted", source);
    }

    [Fact]
    public void Shutdown_PreservesSurfaceProviderAndTrayOrdering()
    {
        var source = ReadAppSource();

        AssertInOrder(
            source,
            "\"app state observers\"",
            "var windowManager = _windowManager;",
            "_windowManager = null;",
            "\"window manager\"",
            "\"tray menu window\"",
            "var services = _services;",
            "_services = null;",
            "await services.DisposeAsync()",
            "\"tray icon\"",
            "Exit();");
    }

    private static string ReadAppSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs"));
    }

    private static string ReadSurfaceSources()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        var paths = new[]
        {
            Path.Combine(appDirectory, "App.xaml.cs"),
            Path.Combine(appDirectory, "Services", "TrayController.cs"),
            Path.Combine(appDirectory, "Services", "WindowManager.cs"),
            Path.Combine(appDirectory, "Services", "WindowSurfaceRequests.cs"),
        };

        return string.Join(
            Environment.NewLine,
            paths.Where(File.Exists).Select(File.ReadAllText));
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Expected to find '{fragment}'.");
            Assert.True(current > previous, $"Expected '{fragment}' after the prior fragment.");
            previous = current;
        }
    }
}
