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
        Assert.Contains("ShowHub(\"chat\")", source);
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

        Assert.DoesNotMatch(@"(?<![A-Za-z])HubWindow(?![A-Za-z_])",
            File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Services", "WindowManager.cs")));
        Assert.Contains("if (_workspaceWindow is null || _workspaceWindow.IsClosed)", source);
        Assert.Contains("new WorkspaceWindow(state, notifications, ShowConnectionStatus)", source);
        Assert.Contains("_callbacks.ApplyTheme(_workspaceWindow)", source);
        Assert.Contains("_workspaceWindow.OpenSettings(settingsTag)", source);
        Assert.Contains("_workspaceWindow.Navigate(destination)", source);
        Assert.Contains("_workspaceWindow.Activate()", source);
        Assert.Contains("Show(activateWindow: false)", source);
        Assert.Contains("_callbacks.GetPageActivator()?.Reset()", source);
        Assert.Contains("workspace.SettingsSaved -= OnWorkspaceSettingsSaved", source);
        Assert.Contains("ResetNavigationScope();", source);
    }

    [Fact]
    public void ChatCanvasAndStatusSurfaces_PreserveVisibleFallbackAndReuseBehavior()
    {
        var source = ReadSurfaceSources();

        Assert.Contains("TryResolveChatCredentials", source);
        Assert.Contains("ShowConnectionSettingsForPairingIssue", source);
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
        var source = ReadShutdownCoordinatorSource();

        AssertInOrder(
            source,
            "\"app state observers\"",
            "var windowManager = _windowManager;",
            "\"window manager\"",
            "_windowManager = null;",
            "\"tray menu window\"",
            "var services = _services;",
            "\"service provider\"",
            "_services = null;",
            "await services.DisposeAsync()",
            "\"tray icon\"");

        var coordinatorSource = ReadAppShutdownCoordinatorServiceSource();
        Assert.Contains("plan.BeginShutdown();", coordinatorSource);
        Assert.Contains("Logger.Info(\"Shutdown complete; calling Exit() now\");", coordinatorSource);
        Assert.Contains("plan.ExitApplication();", coordinatorSource);
    }

    private static string ReadShutdownCoordinatorSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "App.AppShutdownCoordinator.cs"));
    }

    private static string ReadAppShutdownCoordinatorServiceSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "AppShutdownCoordinator.cs"));
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
