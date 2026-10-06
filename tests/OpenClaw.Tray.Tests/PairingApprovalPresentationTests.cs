namespace OpenClaw.Tray.Tests;

public sealed class PairingApprovalPresentationTests
{
    private static string Source => File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
        "src", "OpenClaw.Tray.WinUI", "Dialogs", "PairingApprovalDialog.cs"));

    [Fact]
    public void CompactApproval_PreservesOriginalSpacingAndIconActions()
    {
        var source = Source;
        Assert.Contains("Glyph = \"\\uE72E\"", source);
        Assert.Contains("Padding = new Thickness(28, 8, 28, 24), RowSpacing = 14", source);
        Assert.Contains("var stack = new StackPanel { Spacing = 10 }", source);
        Assert.DoesNotContain("MinWidth = 100", source);
        Assert.Equal(3, source.Split("buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto })").Length - 1);
        Assert.Contains("Grid.SetColumn(_approveButton, 3)", source);
        Assert.Contains("BuildButtonContent(\"\\uE711\", \"SystemFillColorCriticalBrush\"", source);
        Assert.Contains("BuildButtonContent(\"\\uE73E\", null", source);
        Assert.Contains("private static StackPanel BuildButtonContent(string glyph, string? glyphBrushKey, string label)", source);
    }

    [Fact]
    public void ScopeRows_ConstrainLongAccessDescriptionsToTheCard()
    {
        var source = Source;
        Assert.Contains("var row = new Grid { ColumnSpacing = 8 }", source);
        Assert.Contains("row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) })", source);
        Assert.Contains("Grid.SetColumn(scopeText, 1)", source);
        Assert.Contains("Text = label,\n                    TextWrapping = TextWrapping.Wrap",
            source.Replace("\r\n", "\n"));
    }

    [Fact]
    public void VisualRefresh_PreservesApprovalGuardAndKindSpecificActions()
    {
        var source = Source;
        Assert.Contains("TimeSpan.FromMilliseconds(1500)", source);
        Assert.Contains("_approveButton.IsEnabled = false", source);
        Assert.Contains("_armTimer = new DispatcherTimer { Interval = ApproveArmDelay }", source);
        Assert.Contains("if (_busy || _currentKey == null) return", source);
        Assert.Contains("if (IsClosed || !string.Equals(_currentKey, decisionKey, StringComparison.Ordinal))", source);
        Assert.Contains("PairingApproval_ApproveNode", source);
        Assert.Contains("PairingApproval_ApproveDevice", source);
        Assert.Contains("_laterButton.Click += (_, _) => Close()", source);
        Assert.Contains("_coordinator.ApproveAsync(decisionKey)", source);
        Assert.Contains("_coordinator.RejectAsync(decisionKey)", source);
    }
}
