using OpenClawTray.Systems;

namespace OpenClaw.Tray.Tests.Systems;

public sealed class SystemsViewportTests
{
    [Fact]
    public void InsetBeforeSubscriptionIsRetained_ChangesPublishWithoutWidthOrDataUpdates()
    {
        var viewport = new SystemsViewport();
        viewport.ApplyTopInset(56);
        Assert.Equal(56, viewport.TopInset);
        var published = new List<double>();
        viewport.Changed += (_, _) => published.Add(viewport.TopInset);
        viewport.ApplyTopInset(56);
        Assert.Empty(published);
        viewport.ApplyTopInset(0);
        viewport.ApplyTopInset(56);
        Assert.Equal(new[] { 0d, 56d }, published);
    }

    [Fact]
    public void PageAllocationDoesNotReinitializeControllerOrMoveRootAndOverlays()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SystemsPage.xaml.cs"));
        var apply = page[page.IndexOf("void Presentation.ISettingsPageContentInset.ApplyTopInset(", StringComparison.Ordinal)..
            page.IndexOf("protected override void OnNavigatedTo(", StringComparison.Ordinal)];
        Assert.Contains("_viewport.ApplyTopInset(topInset)", apply);
        Assert.DoesNotContain("Mount", apply);
        Assert.DoesNotContain("_controller", apply);
        Assert.Contains("Viewport = _viewport", page);
        var view = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Systems", "SystemsPageView.cs"));
        Assert.Contains("viewport.Changed += changed;", view);
        Assert.Contains("setTopInset(viewport.TopInset);", view);
        Assert.Contains("viewport.Changed -= changed", view);
        Assert.DoesNotContain("SystemsPageBackdrop", view);
        Assert.Single(view.Split(".Margin(0, topInset, 0, 0)", StringSplitOptions.None).Skip(1));
        Assert.Contains(".Margin(0, topInset, 0, 0)\n", view.Replace("\r\n", "\n"));
        Assert.Contains(".AutomationId(\"SystemsPageScroll\").WithKey(\"scroll\")", view);
        Assert.DoesNotContain("ReactorHost.Margin", page);
    }
}
