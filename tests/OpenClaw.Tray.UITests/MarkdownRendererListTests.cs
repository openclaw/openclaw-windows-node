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
/// Note: as of the RichTextBlock coalescing change, "simple" lists (whose items
/// contain only text) now flow into a <see cref="RichTextBlockElement"/> for
/// continuous selection, and RichTextBlock wraps inherently. The Grid path below
/// is therefore exercised by "complex" lists (an item carrying a code block,
/// table, etc.), which still render as their own island — so these regression
/// guards use complex-list fixtures.
///
/// These assertions live at the FunctionalUI <see cref="Element"/> record level
/// (no WinUI runtime needed) because that's where the regression would surface:
/// any change of the row container back to a horizontal stack would fail these
/// tests immediately.
/// </summary>
public sealed class MarkdownRendererListTests
{
    // A "complex" list forces the Grid island path: item two carries a block
    // quote, so the list is not simple and does not coalesce into a
    // RichTextBlock. A block quote renders without eager WinRT activation
    // (unlike a fenced code block, whose FontFamily throws headless), so these
    // record-level tests stay runtime-free. Item one's long bullet still
    // exercises the wrap guard.
    private const string ComplexUnorderedList =
        "- This is an unusually long bullet that must wrap across multiple " +
        "lines instead of being clipped at the right edge of the chat bubble.\n\n" +
        "- second item\n\n  > side note";

    private const string ComplexOrderedList =
        "1. first item\n\n2. second item\n\n   > side note";

    [Fact]
    public void UnorderedListRow_IsGridWithAutoMarkerAndStarContent()
    {
        var element = ChatMarkdownRenderer.Render(ComplexUnorderedList);

        var list = Assert.IsType<StackElement>(element);
        Assert.Equal(Microsoft.UI.Xaml.Controls.Orientation.Vertical, list.Orientation);
        Assert.Equal(2, list.Children.Count);

        // Row 0 is the long-text bullet — the row whose wrapping issue #636 fixed.
        var row = Assert.IsType<GridElement>(list.Children[0]!);
        AssertListRowShape(row);
    }

    [Fact]
    public void OrderedListRows_EachUseGridWithAutoMarkerAndStarContent()
    {
        var element = ChatMarkdownRenderer.Render(ComplexOrderedList);

        var list = Assert.IsType<StackElement>(element);
        Assert.Equal(Microsoft.UI.Xaml.Controls.Orientation.Vertical, list.Orientation);
        Assert.Equal(2, list.Children.Count);

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
        var element = ChatMarkdownRenderer.Render(ComplexUnorderedList);

        var list = Assert.IsType<StackElement>(element);
        var row = list.Children[0];
        if (row is StackElement stackRow)
            Assert.Fail(
                $"List row regressed to a {stackRow.Orientation} StackElement. " +
                "Issue #636 requires a GridElement so the content column gets " +
                "a finite measure width and TextBlock wrap works.");
    }

    [Fact]
    public void SimpleList_RendersAsRichTextBlockForContinuousSelection()
    {
        // A simple text-only list now coalesces into a RichTextBlock so it is
        // one continuous selection scope with surrounding prose. RichTextBlock
        // wraps inherently, so the issue #636 clipping does not recur here.
        var element = ChatMarkdownRenderer.Render(
            "- This is an unusually long bullet that must wrap across multiple " +
            "lines instead of being clipped at the right edge of the chat bubble.");

        Assert.IsType<RichTextBlockElement>(element);
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
