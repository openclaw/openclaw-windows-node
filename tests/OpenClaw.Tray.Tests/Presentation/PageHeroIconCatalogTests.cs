using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class PageHeroIconCatalogTests
{
    [Theory]
    [InlineData("cron", "Cron")]
    [InlineData("instances", "Instances")]
    [InlineData("usage", "Usage")]
    public void HeroAndSidebarShareTheCatalogIconUri(string tag, string asset)
    {
        var page = Assert.IsType<PinnablePage>(PinnedPages.Find(tag));
        Assert.Equal($"ms-appx:///Assets/SidebarIcons/{asset}.svg", page.IconUri);
    }

    [Fact]
    public void WorkspacePinsUsesTheCatalogIconUri()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.Pins.cs"));

        Assert.Contains("new SvgImageSource(new Uri(page.IconUri))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("page.IconAsset", source, StringComparison.Ordinal);
    }
}
