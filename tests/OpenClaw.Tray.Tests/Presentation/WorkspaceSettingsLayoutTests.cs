using OpenClawTray.Presentation;

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
}
