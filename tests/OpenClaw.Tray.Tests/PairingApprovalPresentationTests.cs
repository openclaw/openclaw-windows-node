namespace OpenClaw.Tray.Tests;

public sealed class PairingApprovalPresentationTests
{
    private static string Source => File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
        "src", "OpenClaw.Tray.WinUI", "Dialogs", "PairingApprovalDialog.cs"));

    [Fact]
    public void CompactApproval_UsesSetupSpacingAndContentSizedTextActions()
    {
        var source = Source;
        Assert.Contains("Glyph = \"\\uE72E\"", source);
        Assert.Contains("Padding = new Thickness(24, 8, 24, 24), RowSpacing = 24", source);
        Assert.Contains("var stack = new StackPanel { Spacing = 12 }", source);
        Assert.Equal(3, source.Split("MinWidth = 100").Length - 1);
        Assert.Equal(2, source.Split("HorizontalAlignment = HorizontalAlignment.Left").Length - 1);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right", source);
        Assert.Contains("private static TextBlock BuildButtonContent(string label)", source);
        Assert.DoesNotContain("HorizontalAlignment = HorizontalAlignment.Stretch", source);
        Assert.DoesNotContain("BuildButtonContent(string glyph", source);
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
