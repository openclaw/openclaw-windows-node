using OpenClawTray.Automations;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class AutomationsViewportStateTests
{
    [Fact]
    public void AllocationBeforeSubscriptionIsRetainedAndInsetOnlyChangesNotify()
    {
        var viewport = new AutomationsViewportState();
        viewport.ApplyTopInset(56);
        Assert.Equal(56, viewport.TopInset);
        var changes = new List<double>();
        EventHandler<double> changed = (_, inset) => changes.Add(inset);
        viewport.Changed += changed;
        viewport.ApplyTopInset(56);
        Assert.Empty(changes);
        viewport.ApplyTopInset(0);
        viewport.ApplyTopInset(56);
        Assert.Equal(new double[] { 0, 56 }, changes);
        viewport.Changed -= changed;
        viewport.ApplyTopInset(0);
        Assert.Equal(0, viewport.TopInset);
        Assert.Equal(new double[] { 0, 56 }, changes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidAllocationDoesNotChangeRetainedState(double inset)
    {
        var viewport = new AutomationsViewportState();
        viewport.ApplyTopInset(56);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.ApplyTopInset(inset));
        Assert.Equal(56, viewport.TopInset);
    }

    [Fact]
    public void InsetAppliesOnlyToScrollWithoutRecreatingTheStoreOrRoot()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));
        var apply = page[page.IndexOf("void ISettingsPageContentInset.ApplyTopInset(", StringComparison.Ordinal)..
            page.IndexOf("/// <summary>Agent", StringComparison.Ordinal)];
        Assert.Contains("_viewport.ApplyTopInset(topInset)", apply);
        Assert.DoesNotContain("Mount(", apply);
        Assert.DoesNotContain("Refresh", apply);
        Assert.Contains("Viewport = _viewport", page);
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Automations", "AutomationsRoot.cs"));
        Assert.Contains("viewport.Changed += changed", source);
        Assert.Contains("setTopInset(viewport.TopInset)", source);
        Assert.Contains("return () => viewport.Changed -= changed", source);
        Assert.Contains("PageHero.Header(", source);
        Assert.DoesNotContain("PageBackdrop(", source);
        var scroll = source[source.IndexOf("ScrollView(VStack(24,", StringComparison.Ordinal)..
            source.IndexOf(".WithKey(\"page-scroll\")", StringComparison.Ordinal)];
        Assert.Contains(".Margin(0, topInset, 0, 0)", scroll);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"\.Margin\(0, topInset, 0, 0\)"));
    }
}
