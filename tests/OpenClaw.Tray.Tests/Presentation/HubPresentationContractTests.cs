namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubPresentationContractTests
{
    [Fact]
    public void HubPageRegistry_OwnsMappingsCommandsAndGatewayClassification()
    {
        var registry = ReadSource("Presentation", "HubPageRegistry.cs");
        var hub = ReadSource("Windows", "HubWindow.xaml.cs");
        var gatewayPolicy = ReadSource("Services", "GatewayNavVisibilityDebouncePolicy.cs");

        Assert.Contains("public static Type? ResolvePageType", registry);
        Assert.Contains("HubPageKind.Permissions => typeof(PermissionsPage)", registry);
        Assert.Contains("public static ImmutableArray<HubCommand> BuildCommands", registry);
        Assert.Contains("public static ImmutableArray<HubCommand> SearchCommands", registry);
        Assert.Contains("public static bool IsGatewayPageTag", registry);
        Assert.Contains("HubPageRegistry.ResolvePageType(tag)", hub);
        Assert.Contains("HubPageRegistry.SearchCommands", hub);

        Assert.DoesNotContain("TagToPageType", hub);
        Assert.DoesNotContain("ResolveAgentPageType", hub);
        Assert.DoesNotContain("Command_GoToConnection_Title", hub);
        Assert.DoesNotContain("IsGatewayPageTag", gatewayPolicy);
        Assert.DoesNotContain("ShouldKeepCurrentPageVisibleDuringDisconnect", gatewayPolicy);
    }

    [Fact]
    public void HubCommandDescriptors_AreImmutableAndRegistryHasNoRuntimeServices()
    {
        var registry = ReadSource("Presentation", "HubPageRegistry.cs");

        Assert.Contains("internal sealed record HubCommand(", registry);
        Assert.Contains("internal sealed record HubCommandContext(", registry);
        Assert.DoesNotContain("Application.Current", registry);
        Assert.DoesNotContain("SettingsManager", registry);
        Assert.DoesNotContain("Action<", registry);
        Assert.DoesNotContain("Func<", registry);
        Assert.DoesNotContain("Execute =", registry);
    }

    [Fact]
    public void InfoBarPresenter_IsWinUiFreeAndHubOnlyAppliesPresentation()
    {
        var presenter = ReadSource("Presentation", "AppNotificationInfoBarPresenter.cs");
        var hub = ReadSource("Windows", "HubWindow.xaml.cs");

        Assert.DoesNotContain("Microsoft.UI", presenter);
        Assert.DoesNotContain("Application.Current", presenter);
        Assert.Contains("AppNotificationBannerState _bannerState", presenter);
        Assert.Contains("AppNotificationInfoBarActionKind.NotificationRoute", presenter);
        Assert.Contains("AppNotificationInfoBarActionKind.ShowMore", presenter);
        Assert.Contains("_appNotificationInfoBarPresenter.Present(", hub);
        Assert.DoesNotContain("IsBannerSeverity", hub);
        Assert.DoesNotContain("_appNotificationActionShowsMore", hub);
    }

    private static string ReadSource(string folder, string file) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", folder, file));
}
