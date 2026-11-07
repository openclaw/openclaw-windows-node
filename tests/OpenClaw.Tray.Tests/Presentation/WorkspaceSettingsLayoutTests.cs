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
        var source = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        var focus = source[source.IndexOf("private bool HasChatNavigationFocus()", StringComparison.Ordinal)..
            source.IndexOf("private void OnWorkspaceSizeChanged(", StringComparison.Ordinal)];
        Assert.Contains("element == NavigationSection || element == PaneToggleChrome", focus);
    }

    [Theory]
    [InlineData(false, false, 56)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    public void PageAllocation_ReservesHistoryOnlyForFullSettingsWithCollapsedRail(
        bool contentOnly, bool paneOpen, double expectedInset)
    {
        Assert.Equal(expectedInset, WorkspaceSettingsLayout.PageTopInset(contentOnly, paneOpen));
    }

    [Theory]
    [InlineData(false, false, false, 0, 0)]
    [InlineData(false, false, true, 0, 0)]
    [InlineData(true, true, false, 0, 0)]
    [InlineData(true, true, true, 0, 0)]
    [InlineData(true, false, false, 56, 0)]
    [InlineData(true, false, true, 0, 56)]
    public void EmbeddedPageTopAllocation_AllocatesHistoryBandOnce(
        bool embedded, bool workspacePaneOpen, bool supportsInset,
        double expectedHostMargin, double expectedPageInset)
    {
        Assert.Equal((expectedHostMargin, expectedPageInset),
            WorkspaceSettingsLayout.EmbeddedPageTopAllocation(embedded, workspacePaneOpen, supportsInset));
    }

    [Fact]
    public void CollapsedSettingsHistory_HasAllocatedContentSpaceRatherThanPageSpecificPadding()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs"));
        var chrome = shell[shell.IndexOf("private void UpdatePaneChrome()", StringComparison.Ordinal)..
            shell.IndexOf("private void UpdatePageAllocation()", StringComparison.Ordinal)];
        Assert.Contains("UpdatePageAllocation();", chrome);
        var contentOnly = shell[shell.IndexOf("internal bool ContentOnly", StringComparison.Ordinal)..
            shell.IndexOf("public object? CurrentPage", StringComparison.Ordinal)];
        Assert.Contains("UpdatePageAllocation();", contentOnly);
        var allocation = shell[shell.IndexOf("private void UpdatePageAllocation()", StringComparison.Ordinal)..
            shell.IndexOf("internal bool ContentOnly", StringComparison.Ordinal)];
        Assert.Contains("_hostContentTopInset", allocation);
        Assert.Contains("WorkspaceSettingsLayout.PageTopInset(false, NavView.IsPaneOpen)", allocation);
        Assert.Contains("SetHostContentTopInset(double inset)", allocation);
        Assert.Contains("ContentFrame.Content is ISettingsPageContentInset page", allocation);
        Assert.Contains("ContentFrame.Margin = new Thickness(0);", allocation);
        Assert.Contains("page.ApplyTopInset(inset);", allocation);
        Assert.Contains("ContentFrame.Margin = new Thickness(0, inset, 0, 0);", allocation);
        Assert.DoesNotContain(".Navigate(", allocation);
        Assert.DoesNotContain("UsagePage", allocation);
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
        var hostAllocation = window[window.IndexOf("private void UpdateEmbeddedPageTopAllocation(", StringComparison.Ordinal)..];
        Assert.Contains("view.SetHostContentTopInset(allocation.PageTopInset);", hostAllocation);
        Assert.Contains("SettingsSection.Margin", hostAllocation);
        Assert.Contains("EmbeddedOperationInfo.Margin = new Thickness(0, allocation.PageTopInset, 0, 0);", hostAllocation);
        var leavingSettings = window[window.IndexOf("private void LeaveSettings()", StringComparison.Ordinal)..
            window.IndexOf("private void OnSettingsWorkspaceRoute(", StringComparison.Ordinal)];
        Assert.Contains("SetHostContentTopInset(0)", leavingSettings);
    }

    [Theory]
    [InlineData(0, true, false, true)]
    [InlineData(719, true, true, false)]
    [InlineData(720, true, false, true)]
    [InlineData(800, true, false, true)]
    [InlineData(719, false, true, false)]
    [InlineData(720, false, false, false)]
    public void HostWidth_SelectsChromeWithoutChangingRequestedDestination(
        double width, bool requestedEmbedded, bool narrow, bool effectiveEmbedded)
    {
        var destination = WorkspaceDestination.Settings("usage", requestedEmbedded);
        var layout = WorkspaceSettingsLayout.Project(width, destination.Embedded);
        Assert.Equal(narrow, layout.Narrow);
        Assert.Equal(effectiveEmbedded, layout.Embedded);
        Assert.Equal("usage", destination.CompanionTag);
        Assert.Equal(requestedEmbedded, destination.Embedded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NarrowExpansionNeverOverridesTheWidePreference(bool preferredOpen)
    {
        var wide = WorkspaceSettingsLayout.Project(800, embedded: false);
        var narrow = WorkspaceSettingsLayout.Project(719, embedded: false);
        Assert.Equal(preferredOpen, wide.PaneOpen(preferredOpen, narrowExpanded: false));
        Assert.False(narrow.PaneOpen(preferredOpen, narrowExpanded: false));
        Assert.True(narrow.PaneOpen(preferredOpen, narrowExpanded: true));
        Assert.Equal(preferredOpen, wide.PaneOpen(preferredOpen, narrowExpanded: true));
    }

    [Theory]
    [InlineData(800)]
    [InlineData(1024)]
    [InlineData(1280)]
    public void ReachableWindowSizesPreserveExistingEmbeddingAndPanePreference(double width)
    {
        foreach (var embedded in new[] { false, true })
        foreach (var preferredOpen in new[] { false, true })
        {
            var layout = WorkspaceSettingsLayout.Project(width, embedded);
            Assert.False(layout.Narrow);
            Assert.Equal(embedded, layout.Embedded);
            Assert.Equal(preferredOpen, layout.PaneOpen(preferredOpen, narrowExpanded: false));
        }
    }

    [Fact]
    public void ResizeDoesNotReplaceDeferredUserNavigationOrRemountThePage()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        var resize = source[source.IndexOf("private void ApplySettingsLayoutAfterResize()", StringComparison.Ordinal)..
            source.IndexOf("private void UpdateTitleBarActions()", StringComparison.Ordinal)];
        Assert.DoesNotContain("DeferWhileModalOpen", resize);
        Assert.DoesNotContain("_deferred.", resize);
        Assert.DoesNotContain("RenderDestination", resize);
        Assert.DoesNotContain("ShowSection", resize);
        var apply = source[source.IndexOf("private void ApplySettingsPresentation(", StringComparison.Ordinal)..
            source.IndexOf("private bool HasChatNavigationFocus()", StringComparison.Ordinal)];
        Assert.DoesNotContain("_navigation.", apply);
        Assert.DoesNotContain("ShowSection", apply);
        Assert.DoesNotContain(".Navigate(", apply);
        var shell = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs"));
        Assert.DoesNotContain("NavigationViewPaneDisplayMode.LeftCompact", shell);
        Assert.DoesNotContain("NavigationViewPaneDisplayMode.LeftMinimal", shell);
    }
}
