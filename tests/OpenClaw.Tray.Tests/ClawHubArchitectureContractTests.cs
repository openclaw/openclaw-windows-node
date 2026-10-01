namespace OpenClaw.Tray.Tests;

public class ClawHubArchitectureContractTests
{
    [Fact]
    public void InstallWorkflow_StaysOutOfAppAndWebViewPage()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "App.ActivationRouter.cs"));
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ClawHubPage.xaml.cs"));
        var coordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "ClawHubInstallCoordinator.cs"));

        Assert.DoesNotContain("\"plugins.inspect\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("\"plugins.install\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("InspectPluginAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallClawHubPluginAsync", page, StringComparison.Ordinal);
        Assert.Contains("InspectPluginAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("InstallClawHubPluginAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("InstallClawHubSkillAsync", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void HubWindow_DoesNotOwnClawHubPageMapping()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "HubWindow.xaml.cs"));
        var registry = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Presentation",
            "HubPageRegistry.cs"));

        Assert.DoesNotContain("typeof(ClawHubPage)", window, StringComparison.Ordinal);
        Assert.Contains("HubPageKind.ClawHub => typeof(ClawHubPage)", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void HubWindow_PlacesClawHubInGatewaySection()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "HubWindow.xaml"));

        var gatewayHeader = window.IndexOf("x:Name=\"NavGatewayHeader\"", StringComparison.Ordinal);
        var clawHub = window.IndexOf("AutomationProperties.AutomationId=\"SettingsNavClawHub\"", StringComparison.Ordinal);
        var gatewaySeparator = window.IndexOf("x:Name=\"NavGatewaySeparator\"", StringComparison.Ordinal);

        Assert.True(gatewayHeader >= 0, "Gateway header was not found.");
        Assert.True(clawHub > gatewayHeader, "ClawHub must appear after the Gateway header.");
        Assert.True(gatewaySeparator > clawHub, "ClawHub must appear before the This Computer separator.");
    }

    [Fact]
    public void ClawHubPage_ReappliesInstallBridgeAfterSuccessfulNavigation()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ClawHubPage.xaml.cs"));

        Assert.Contains("HandleNavigationCompletedAsync", page, StringComparison.Ordinal);
        Assert.Contains(
            "ExecuteScriptAsync(_installBridgeScript)",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClawHubPage_IgnoresCompletionForInterceptedInstallNavigation()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ClawHubPage.xaml.cs"));

        Assert.Contains(
            "_interceptedNavigationIds.Add(args.NavigationId)",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "_interceptedNavigationIds.Remove(args.NavigationId)",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DialogPresenter_SerializesAndFullyReleasesContentDialogs()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var presenter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "ClawHubInstallDialogPresenter.cs"));

        Assert.Contains("SemaphoreSlim _dialogGate", presenter, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", presenter, StringComparison.Ordinal);
        Assert.Contains("WaitForDialogReleaseAsync", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallDialogsUseVisibleHubRoot()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "App.ActivationRouter.cs"));

        Assert.Contains(
            "new ClawHubInstallDialogPresenter(" + Environment.NewLine +
            "                        () => _windowManager?.DialogXamlRoot)",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HubWindow_UsesDistinctClawHubStoreIcon()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "HubWindow.xaml"));

        Assert.Contains(
            "ClawHubStore.svg",
            window,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Assets",
            "SidebarIcons",
            "ClawHubStore.svg")));
    }
}
