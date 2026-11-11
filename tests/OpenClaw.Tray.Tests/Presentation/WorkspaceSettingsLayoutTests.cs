using OpenClawTray.Presentation;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceSettingsLayoutTests
{
    [Fact]
    public void PinnedPaneToggle_IsAboveSettingsWithoutRaisingTheChatSurface()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var settings = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "SettingsSection");
        var navigation = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "NavigationSection");
        var toggle = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "PaneToggleChrome");
        Assert.Same(settings.Parent, toggle.Parent);
        Assert.Same(settings.Parent, navigation.Parent);
        var siblings = settings.Parent!.Elements().ToList();
        Assert.True(siblings.IndexOf(navigation) < siblings.IndexOf(settings));
        Assert.True(siblings.IndexOf(settings) < siblings.IndexOf(toggle));
        Assert.Equal("1", (string?)toggle.Attribute("Grid.Row"));
        Assert.Equal("{Binding Visibility, ElementName=NavigationSection}",
            (string?)toggle.Attribute("Visibility"));
        Assert.Equal("48", (string?)toggle.Attribute("Width"));
        Assert.Equal("56", (string?)toggle.Attribute("Height"));
        Assert.Null(toggle.Attribute("Background"));
        Assert.Equal("0", (string?)toggle.Attribute("TabIndex"));
        var history = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "SettingsHistoryBar");
        Assert.Equal("1", (string?)history.Attribute("TabIndex"));
        Assert.True(siblings.IndexOf(toggle) < siblings.IndexOf(history));
        foreach (var name in new[] { "CollapsePaneButton", "ReopenPaneButton" })
        {
            var button = document.Descendants().Single(element =>
                (string?)element.Attribute(x + "Name") == name);
            Assert.Same(toggle, button.Parent);
            Assert.Equal("OnTogglePane", (string?)button.Attribute("Click"));
            Assert.NotEqual("False", (string?)button.Attribute("IsHitTestVisible"));
        }
    }

    [Theory]
    [InlineData(false, false, false, 0, 0)]
    [InlineData(false, false, true, 0, 0)]
    [InlineData(false, true, false, 0, 0)]
    [InlineData(false, true, true, 0, 0)]
    [InlineData(true, true, false, 0, 0)]
    [InlineData(true, true, true, 0, 0)]
    [InlineData(true, false, false, 56, 0)]
    [InlineData(true, false, true, 0, 56)]
    public void EmbeddedHistory_IsAllocatedExactlyOnce(
        bool embedded, bool paneOpen, bool supportsInset, double margin, double inset)
    {
        var result = WorkspaceSettingsLayout.EmbeddedPageTopAllocation(embedded, paneOpen, supportsInset);
        Assert.Equal(margin, result.HostMarginTop);
        Assert.Equal(inset, result.PageTopInset);
    }

    [Theory]
    [InlineData(false, false, 56)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    public void FullSettingsHistory_DoesNotDuplicateEmbeddedAllocation(bool contentOnly, bool paneOpen, double expected) =>
        Assert.Equal(expected, WorkspaceSettingsLayout.PageTopInset(contentOnly, paneOpen));

    [Fact]
    public void OptedInPageAllocation_PreservesOrdinaryPagePaddingAndNavigationIdentity()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs"));
        var chrome = shell[shell.IndexOf("private void UpdatePaneChrome()", StringComparison.Ordinal)..
            shell.IndexOf("internal bool ContentOnly", StringComparison.Ordinal)];
        Assert.Contains("UpdatePageAllocation();", chrome);
        var contentOnly = shell[shell.IndexOf("internal bool ContentOnly", StringComparison.Ordinal)..
            shell.IndexOf("private void UpdatePageAllocation()", StringComparison.Ordinal)];
        Assert.Contains("ApplyNavPaneState(settings);", contentOnly);
        Assert.Contains("UpdatePageAllocation();", contentOnly);
        var allocation = shell[shell.IndexOf("private void UpdatePageAllocation()", StringComparison.Ordinal)..
            shell.IndexOf("public object? CurrentPage", StringComparison.Ordinal)];
        Assert.Contains("ContentFrame.Content is ISettingsPageContentInset page", allocation);
        Assert.Contains("page.ApplyTopInset(ContentOnly ? _hostContentTopInset", allocation);
        Assert.Contains("WorkspaceSettingsLayout.PageTopInset(false, NavView.IsPaneOpen)", allocation);
        Assert.Contains("SetHostContentTopInset(double inset)", allocation);
        Assert.DoesNotContain("ContentFrame.Margin", allocation);
        Assert.DoesNotContain(".Navigate(", allocation);
        var navigated = shell[shell.IndexOf("private void OnContentFrameNavigated(", StringComparison.Ordinal)..
            shell.IndexOf("private void OnContentFrameNavigationFailed(", StringComparison.Ordinal)];
        Assert.True(navigated.IndexOf("InitializeCurrentPage();", StringComparison.Ordinal) <
            navigated.IndexOf("PageContentChanged?.Invoke();", StringComparison.Ordinal));
        Assert.True(navigated.IndexOf("PageContentChanged?.Invoke();", StringComparison.Ordinal) <
            navigated.IndexOf("UpdatePageAllocation();", StringComparison.Ordinal));
        Assert.True(navigated.IndexOf("UpdatePageAllocation();", StringComparison.Ordinal) <
            navigated.IndexOf("ArmContentReady(", StringComparison.Ordinal));
        var window = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        var hostAllocation = window[window.IndexOf("private void UpdateEmbeddedPageTopAllocation(", StringComparison.Ordinal)..
            window.IndexOf("private void UpdateTitleBarActions()", StringComparison.Ordinal)];
        Assert.Contains("view.SetHostContentTopInset(allocation.PageTopInset);", hostAllocation);
        Assert.Contains("allocation.HostMarginTop", hostAllocation);
        Assert.Contains("EmbeddedOperationInfo.Margin = new Thickness(0, allocation.PageTopInset, 0, 0);", hostAllocation);
        var leavingSettings = window[window.IndexOf("private void LeaveSettings()", StringComparison.Ordinal)..
            window.IndexOf("private void OnSettingsWorkspaceRoute(", StringComparison.Ordinal)];
        Assert.Contains("SetHostContentTopInset(0)", leavingSettings);
    }

    [Theory]
    [InlineData("cron", "ms-appx:///Assets/SidebarIcons/Cron.svg")]
    [InlineData("instances", "ms-appx:///Assets/SidebarIcons/Instances.svg")]
    [InlineData("usage", "ms-appx:///Assets/SidebarIcons/Usage.svg")]
    public void HeroIcon_ReusesExistingSidebarAsset(string tag, string expected) =>
        Assert.Equal(expected, PinnedPages.Find(tag)!.IconUri);

    [Fact]
    public void DashboardRoot_ReconcilesStateAfterSubscriptionAndFencesQueuedUnmountedRefresh()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Dashboards", "ReactorDashboardsRoot.cs"));
        var subscription = source[source.IndexOf("var current = true;", StringComparison.Ordinal)..
            source.IndexOf("}), state);", StringComparison.Ordinal)];
        Assert.Contains("state.Changed += OnChanged;\n            OnChanged();", subscription.Replace("\r\n", "\n"));
        Assert.Contains("if (current)", subscription);
        Assert.Contains("current = false;", subscription);
        Assert.Contains("state.Changed -= OnChanged;", subscription);
        Assert.DoesNotContain("LoadAsync", subscription);
        Assert.DoesNotContain("Mount(", subscription);
    }

    [Theory]
    [InlineData(false, 56)]
    [InlineData(true, 0)]
    public void DashboardSessionRoute_RetainsStandaloneIdentityAcrossHistoryAllocation(bool paneOpen, double expectedInset)
    {
        var route = DashboardsRoute.ForSession("agent:main:report with spaces");
        var destination = WorkspaceDestination.Settings(route);
        var history = new WorkspaceNavigationHistory();
        history.Navigate(destination);
        var allocation = WorkspaceSettingsLayout.EmbeddedPageTopAllocation(destination.Embedded, paneOpen, supportsInset: true);
        Assert.Equal(0, allocation.HostMarginTop);
        Assert.Equal(expectedInset, allocation.PageTopInset);
        Assert.True(destination.Embedded);
        Assert.Equal(route, history.Current.CompanionTag);
        Assert.Same(destination, history.Current);
    }
}
