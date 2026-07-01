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

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
