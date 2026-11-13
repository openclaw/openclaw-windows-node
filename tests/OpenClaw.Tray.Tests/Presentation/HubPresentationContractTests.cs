using System.Xml.Linq;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubPresentationContractTests
{
    [Theory]
    [InlineData("Controls", "InboxView.xaml.cs", "private void OnActionClick",
        "feed.NavigateNotificationAsync(", "commands.TryNavigateAsync")]
    [InlineData("Controls", "SettingsShellView.xaml.cs", "private void OnAppNotificationActionButtonClick",
        "notifications.NavigateActionAsync(", "((IAppCommands)CurrentApp).TryNavigateAsync")]
    public void NotificationActions_UseAdmissionWithoutPublishingPendingChatIntent(
        string folder, string file, string handler, string admissionCall, string navigationCallback)
    {
        var source = ReadSource(folder, file);
        var start = source.IndexOf(handler, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("\n    private ", start + handler.Length, StringComparison.Ordinal);
        var action = end < 0 ? source[start..] : source[start..end];

        Assert.Contains(admissionCall, action);
        Assert.Contains("AsyncEventHandlerGuard.Run(", action);
        Assert.Contains(navigationCallback, action);
        Assert.DoesNotContain("PendingChatSessionKey", action);
        Assert.DoesNotContain(".Dismiss(", action);
    }

    [Fact]
    public void InboxHosts_ForwardAdmissionResultsAndRequestLocalChatKeys()
    {
        // The shared row handler owns admission on both surfaces. Retire this wiring guard
        // when native refusal/deferral proof covers the page and each footer host.
        var page = ReadSource("Pages", "NotificationsPage.xaml.cs");
        var workspace = ReadSource("Windows", "WorkspaceWindow.xaml.cs");
        var footer = ReadSource("Controls", "WorkspacePaneFooter.xaml.cs");
        var view = ReadSource("Controls", "InboxView.xaml.cs");
        Assert.Contains("Func<string, string?, Task<bool>> TryNavigateAsync", view);
        // App reminders cannot regain the retired fire-and-forget route path.
        Assert.DoesNotContain("OpenRoute", view);
        Assert.DoesNotContain("commands.OpenRoute", footer);
        Assert.DoesNotContain("OpenInboxRoute", workspace);
        Assert.DoesNotContain("OpenAppRoute", page);
        Assert.Contains("((IAppCommands)CurrentApp).TryNavigateAsync,", page);
        Assert.Contains("((IAppCommands)CurrentApp).TryNavigateAsync,", workspace);
        Assert.Contains("return commands.TryNavigateAsync(route, sessionKey);", footer);
        Assert.DoesNotContain("PendingChatSessionKey", page);
        Assert.DoesNotContain("OnNotificationActionClick", page);
        Assert.DoesNotContain("NotificationsList", page);
    }

    [Fact]
    public void InboxGatewayRoutes_CannotBypassAppReminderAdmission()
    {
        // This boundary is native-XAML-only. Retire the source guard when mandatory
        // mounted proof can inject an app reminder into the secondary-action handler.
        var view = ReadSource("Controls", "InboxView.xaml.cs");
        var actionStart = view.IndexOf("private void OnActionClick", StringComparison.Ordinal);
        Assert.True(actionStart >= 0);
        var secondaryStart = view.IndexOf("private void OnSecondaryActionClick", actionStart, StringComparison.Ordinal);
        Assert.True(secondaryStart > actionStart);
        var action = view[actionStart..secondaryStart];
        var appStart = action.IndexOf("if (!InboxGatewayItems.IsGatewayItemId(item.Id))", StringComparison.Ordinal);
        var gatewayStart = action.IndexOf("item.OpenAction(commands.OpenGatewayRoute, commands.OpenDashboard);", StringComparison.Ordinal);
        Assert.True(appStart >= 0 && gatewayStart > appStart);
        var appBranch = action[appStart..gatewayStart];
        Assert.Contains("feed.NavigateNotificationAsync(item.Id, commands.TryNavigateAsync)", appBranch);
        Assert.Contains("return;", appBranch);
        Assert.DoesNotContain("OpenGatewayRoute", appBranch);
        Assert.DoesNotContain(".Dismiss(", action);

        var secondary = view[secondaryStart..view.IndexOf("private void OnAskClick", secondaryStart, StringComparison.Ordinal)];
        Assert.Matches(@"&&\s+InboxGatewayItems\.IsGatewayItemId\(item\.Id\)\)\s+_commands\?\.OpenGatewayRoute\(route\);", secondary);
        Assert.Contains("_commands?.OpenGatewayRoute(route);", secondary);
        Assert.DoesNotContain("NavigateNotificationAsync", secondary);
        Assert.DoesNotContain(".Dismiss(", secondary);
    }

    [Fact]
    public void NotificationAdmission_PreservesAboutIdentityAndAwaitsDashboardLaunchResult()
    {
        var view = ReadSource("Controls", "SettingsShellView.xaml.cs");
        var shown = view[view.IndexOf("internal bool IsSectionShown", StringComparison.Ordinal)..
            view.IndexOf("internal string? CurrentSectionTag", StringComparison.Ordinal)];
        Assert.Contains("_currentNavTag == tag", shown);
        Assert.DoesNotContain("\"settings\"", shown);
        var section = view[view.IndexOf("internal void ShowSection(", StringComparison.Ordinal)..
            view.IndexOf("internal void Deactivate()", StringComparison.Ordinal)];
        Assert.Contains("NavigateInternal(tag)", section);
        Assert.DoesNotContain("\"about\"", section);

        var manager = ReadSource("Services", "WindowManager.cs");
        Assert.Contains("return WorkspaceNavigation.DispatchAsync(navigateTo", manager);
        Assert.Contains("path => ((IAppCommands)Application.Current).TryOpenDashboardAsync(path)", manager);
        var app = ReadSource("", "App.xaml.cs");
        Assert.Contains("return launcher.OpenAsync(path)", app);
        Assert.Contains("_windowManager?.TryShowHubAsync(pageTag, chatSessionKey)", app);
    }

    [Fact]
    public void NavigationAdmission_RejectsDeferredAndUnmountedDestinations()
    {
        var workspace = ReadSource("Windows", "WorkspaceWindow.xaml.cs");
        var start = workspace.IndexOf("internal bool TryNavigate(", StringComparison.Ordinal);
        var end = workspace.IndexOf("internal void OpenCompanion(", start, StringComparison.Ordinal);
        var admission = workspace[start..end];
        Assert.Contains("if (IsClosed) return false;", admission);
        Assert.Matches(@"DeferWhileModalOpen\([^\r\n]+\)\)\s+return false;", admission);
        Assert.Contains("Root.XamlRoot is not null && Destination == destination", admission);
        Assert.Contains("_settings?.IsSectionShown(", admission);
        var manager = ReadSource("Services", "WindowManager.cs");
        Assert.Contains("chatSessionKey, usePendingChatSession: false", manager);
        Assert.Contains("admitted = _workspaceWindow.TryNavigate(destination)", manager);
        Assert.Contains("admitted = _workspaceWindow.TryOpenSettings(settingsTag)", manager);
    }

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
