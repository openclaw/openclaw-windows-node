namespace OpenClaw.Tray.Tests;

public sealed class FunctionalUiModifierResetContractTests
{
    [Fact]
    public void ApplyModifiers_ClearsStaleLayoutAndAutomationValues()
    {
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("control.ClearValue(FrameworkElement.WidthProperty);", functionalUi);
        Assert.Contains("control.ClearValue(FrameworkElement.HeightProperty);", functionalUi);
        Assert.Contains("control.ClearValue(FrameworkElement.MaxWidthProperty);", functionalUi);
        Assert.Contains("control.ClearValue(UIElement.OpacityProperty);", functionalUi);
        Assert.Contains("control.ClearValue(AutomationProperties.NameProperty);", functionalUi);
        Assert.Contains("control.ClearValue(AutomationProperties.LiveSettingProperty);", functionalUi);
    }

    [Fact]
    public void ApplyModifiers_ClearsStaleBorderTextAndControlValues()
    {
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("b.ClearValue(Border.BackgroundProperty);", functionalUi);
        Assert.Contains("b.ClearValue(Border.PaddingProperty);", functionalUi);
        Assert.Contains("b.ClearValue(Border.CornerRadiusProperty);", functionalUi);
        Assert.Contains("tb.ClearValue(TextBlock.ForegroundProperty);", functionalUi);
        Assert.Contains("tb.ClearValue(TextBlock.TextTrimmingProperty);", functionalUi);
        Assert.Contains("tb.ClearValue(TextBlock.MaxLinesProperty);", functionalUi);
        Assert.Contains("tb.ClearValue(TextBlock.LineHeightProperty);", functionalUi);
        Assert.Contains("tb.ClearValue(TextBlock.CharacterSpacingProperty);", functionalUi);
        Assert.Contains("c.ClearValue(Control.PaddingProperty);", functionalUi);
        Assert.Contains("c.ClearValue(Control.BorderBrushProperty);", functionalUi);
    }

    [Fact]
    public void UiRenderer_PrunesUnvisitedCachesAfterRender()
    {
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("private readonly HashSet<string> _visitedControlPaths = new();", functionalUi);
        Assert.Contains("private readonly HashSet<string> _visitedComponentKeys = new();", functionalUi);
        Assert.Contains("private readonly HashSet<string> _visitedContentFlyoutPaths = new();", functionalUi);
        Assert.Contains("private readonly HashSet<string> _visitedMenuFlyoutPaths = new();", functionalUi);
        Assert.Contains("PruneUnvisitedPaths();", functionalUi);
        Assert.Contains("component.Context.RunEffectCleanups();", functionalUi);
        Assert.Contains("flyout.Hide();", functionalUi);
        Assert.Contains("flyout.Content = null;", functionalUi);
        Assert.Contains("_mountedPaths.Remove(path);", functionalUi);
        Assert.Contains("DetachChildren(control);", functionalUi);
        Assert.Contains("_controls.Remove(path);", functionalUi);
    }

    [Fact]
    public void UiRenderer_CachesMenuFlyoutsByRenderPath()
    {
        var functionalUi = Read("src", "OpenClawTray.FunctionalUI", "FunctionalUI.cs");

        Assert.Contains("private readonly Dictionary<string, MenuFlyout> _menuFlyouts = new();", functionalUi);
        Assert.Contains("MenuFlyoutContentElement menu => CreateMenuFlyout(menu, path)", functionalUi);
        Assert.Contains("if (!_menuFlyouts.TryGetValue(path, out var flyout))", functionalUi);
        Assert.Contains("flyout.Items.Clear();", functionalUi);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
