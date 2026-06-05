using OpenClawTray.Chat.Markdown;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using Xunit;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Regression tests for GitHub issue #636 ("Text is sometimes clipped in Chat").
///
/// The bug: list rows used to be built as a horizontal <see cref="StackElement"/>
/// (<c>HStack(marker, content)</c>), which materializes to a horizontal
/// <see cref="Microsoft.UI.Xaml.Controls.StackPanel"/>. A horizontal StackPanel
/// measures children with infinite available width, so the paragraph's
/// <see cref="Microsoft.UI.Xaml.Controls.TextBlock"/> (with
/// <c>TextWrapping = Wrap</c>) never wraps — long bullets render on a single
/// line and get clipped by the chat bubble's max width.
///
/// The fix: each row is now a 2-column <see cref="GridElement"/>
/// (<c>Auto</c> marker + <c>*</c> content). The Star column gives the content
/// a finite measure width, so wrap works correctly.
///
/// These assertions live at the FunctionalUI <see cref="Element"/> record level
/// (no WinUI runtime needed) because that's where the regression would surface:
/// any change of the row container back to a horizontal stack would fail these
/// tests immediately.
/// </summary>
public sealed class MarkdownRendererListTests
{
    [Fact]
    public void UnorderedListRow_IsGridWithAutoMarkerAndStarContent()
    {
        const string longBullet =
            "- This is an unusually long bullet that must wrap across multiple " +
            "lines instead of being clipped at the right edge of the chat bubble.";

        var element = ChatMarkdownRenderer.Render(longBullet);

        var list = Assert.IsType<StackElement>(element);
        Assert.Equal(Microsoft.UI.Xaml.Controls.Orientation.Vertical, list.Orientation);
        Assert.Single(list.Children);

        var row = Assert.IsType<GridElement>(list.Children[0]!);
        AssertListRowShape(row);
    }

    [Fact]
    public void OrderedListRows_EachUseGridWithAutoMarkerAndStarContent()
    {
        const string ordered = "1. first item\n2. second item\n3. third item";

        var element = ChatMarkdownRenderer.Render(ordered);

        var list = Assert.IsType<StackElement>(element);
        Assert.Equal(Microsoft.UI.Xaml.Controls.Orientation.Vertical, list.Orientation);
        Assert.Equal(3, list.Children.Count);

        foreach (var child in list.Children)
        {
            var row = Assert.IsType<GridElement>(child!);
            AssertListRowShape(row);
        }
    }

    [Fact]
    public void ListRow_IsNotAHorizontalStackPanel()
    {
        // Direct guard against the original regression: if anyone reverts the
        // row container to HStack(...), this fails with a clear message.
        const string bullet = "- something";

        var element = ChatMarkdownRenderer.Render(bullet);

        var list = Assert.IsType<StackElement>(element);
        var row = list.Children[0];
        if (row is StackElement stackRow)
            Assert.Fail(
                $"List row regressed to a {stackRow.Orientation} StackElement. " +
                "Issue #636 requires a GridElement so the content column gets " +
                "a finite measure width and TextBlock wrap works.");
    }

    private static void AssertListRowShape(GridElement row)
    {
        // Auto column for the marker, Star column for the wrapping content.
        Assert.Equal(new[] { "Auto", "*" }, row.Columns);
        Assert.Equal(new[] { "Auto" }, row.Rows);
        Assert.Equal(2, row.Children.Count);

        var marker = row.Children[0];
        var content = row.Children[1];
        Assert.NotNull(marker);
        Assert.NotNull(content);

        Assert.NotNull(marker!.GridPosition);
        Assert.Equal(0, marker.GridPosition!.Row);
        Assert.Equal(0, marker.GridPosition.Column);

        Assert.NotNull(content!.GridPosition);
        Assert.Equal(0, content.GridPosition!.Row);
        Assert.Equal(1, content.GridPosition.Column);
    }
}
