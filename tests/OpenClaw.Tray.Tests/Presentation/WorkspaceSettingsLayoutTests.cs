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
}
