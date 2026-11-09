using OpenClawTray.Presentation;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceSettingsLayoutTests
{
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
