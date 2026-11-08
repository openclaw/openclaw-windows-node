using System.Xml.Linq;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubPresentationContractTests
{
    [Fact]
    public void SearchPopup_HasNoFooterOrVisibleStatusBlock_AndKeepsActionsBesideInput()
    {
        var document = XDocument.Parse(ReadSource("Controls", "SearchHost.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var input = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SearchBox");
        var list = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ResultsList");

        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName.EndsWith(".Footer", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "SearchStatus");
        foreach (var name in new[] { "MoreButton", "RetryButton" })
        {
            var action = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Same(input.Parent, action.Parent);
            Assert.DoesNotContain(action, list.Descendants());
        }
    }

    [Fact]
    public void HubPageRegistry_OwnsMappingsCommandsAndGatewayClassification()
    {
        var registry = ReadSource("Presentation", "HubPageRegistry.cs");
        var hub = ReadSource("Controls", "SettingsShellView.xaml.cs");
        var gatewayPolicy = ReadSource("Services", "GatewayNavVisibilityDebouncePolicy.cs");

        Assert.Contains("public static Type? ResolvePageType", registry);
        Assert.Contains("HubPageKind.Permissions => typeof(PermissionsPage)", registry);
        Assert.Contains("public static ImmutableArray<HubCommand> BuildCommands", registry);
        Assert.DoesNotContain("SearchCommands(", registry);
        Assert.Contains("public static bool IsGatewayPageTag", registry);
        Assert.Contains("HubPageRegistry.ResolvePageType(tag)", hub);
        Assert.DoesNotContain("HubPageRegistry.SearchCommands", hub);
        Assert.Contains("SettingsSearchHost.Begin", hub);
        var catalog = ReadSource("Services", "HubCommandCatalog.cs");
        Assert.Contains("HubPageRegistry.BuildCommands", catalog);
        Assert.DoesNotContain("ActiveHubWindow", catalog);
        var handlers = ReadSource("", "App.CapabilityHandlers.cs");
        Assert.Contains("HubCommandCatalog.BuildSearch(_appState,", handlers);
        Assert.Contains("HubSearch.Find", handlers);
        Assert.DoesNotContain("if (ActiveHubWindow is not OpenClawTray.Windows.HubWindow", handlers);

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
        var hub = ReadSource("Controls", "SettingsShellView.xaml.cs");

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
