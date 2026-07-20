using OpenClawTray.Chat.Markdown;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using Xunit;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Tests for the assistant-bubble text coalescing behavior: consecutive
/// paragraph / heading blocks AND "simple" lists (whose items contain only
/// more mergeable blocks) merge into a single <see cref="RichTextBlockElement"/>
/// so the whole run is one continuous, drag-selectable scope. Blocks that carry
/// their own chrome (code cards, table grids) — and any list that contains such
/// a block — stay as their own selectable sibling controls.
///
/// Assertions live at the FunctionalUI <see cref="Element"/> record level (no
/// WinUI runtime): the Blocks of the RichTextBlock are populated imperatively
/// by a setter at reconcile time, but the element shape is what pins the
/// coalescing contract.
/// </summary>
public sealed class MarkdownRendererCoalesceTests
{
    [Fact]
    public void SingleParagraph_StaysLightweightTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("just one line of prose");

        // A lone text block keeps the single-TextBlock shape (no RichTextBlock
        // overhead) — it is already internally selectable.
        Assert.IsType<TextBlockElement>(element);
    }

    [Fact]
    public void ConsecutiveParagraphs_CoalesceIntoOneRichTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("first paragraph\n\nsecond paragraph");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void HeadingThenParagraph_CoalesceIntoOneRichTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("# Title\n\nbody text under the title");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void ParagraphThenSimpleList_CoalesceIntoOneRichTextBlock()
    {
        // A simple bullet list flows into the shared RichTextBlock with the
        // preceding prose, so a single drag selects across both.
        var element = ChatMarkdownRenderer.Render(
            "# Title\n\nintro paragraph\n\n- a bullet\n- another bullet");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void LoneSimpleList_BecomesRichTextBlock()
    {
        // Even by itself a simple list renders as a RichTextBlock (continuous
        // selection across items) rather than the Grid-based island.
        var element = ChatMarkdownRenderer.Render("- one\n- two\n- three");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void OrderedSimpleList_BecomesRichTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("1. first\n2. second");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void NestedSimpleList_BecomesRichTextBlock()
    {
        // Nested simple lists are still simple (all children mergeable), so the
        // whole tree flows into one RichTextBlock.
        var element = ChatMarkdownRenderer.Render(
            "- parent\n    - child one\n    - child two\n- sibling");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void ListContainingNonTextBlock_StaysSeparateIsland()
    {
        // A list item with a block quote (any non-text child, e.g. a code block
        // or table too) is NOT simple — it keeps the Grid-based island so the
        // child's chrome survives. The intro prose stays its own sibling.
        // A block quote is used here rather than a code block because it renders
        // without eager WinRT activation, keeping this record-level test
        // runtime-free.
        var element = ChatMarkdownRenderer.Render(
            "intro paragraph\n\n- item one\n\n- item two\n\n  > side note");

        var stack = Assert.IsType<StackElement>(element);
        Assert.Equal(Microsoft.UI.Xaml.Controls.Orientation.Vertical, stack.Orientation);
        Assert.Equal(2, stack.Children.Count);

        // Lone intro paragraph keeps the lightweight TextBlock shape.
        Assert.IsType<TextBlockElement>(stack.Children[0]!);
        // The complex list remains its own StackElement island.
        Assert.IsType<StackElement>(stack.Children[1]!);
    }
}
