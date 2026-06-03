using System.Linq;
using OpenClaw.Shared.Markdown;
using Xunit;

namespace OpenClaw.Shared.Tests.Markdown;

/// <summary>
/// Tests for <see cref="ChatMarkdownAstBuilder"/>. Verifies both the
/// shape of the produced AST (headings, lists, tables) and the chat
/// security posture (inert link/image flattening, raw HTML suppression,
/// input cap).
/// </summary>
public class ChatMarkdownAstBuilderTests
{
    private static ChatMarkdownDocument Build(string markdown)
        => new ChatMarkdownAstBuilder().Build(markdown);

    // ────────────────────────────────────────────────────────────────────
    //  Block shapes
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ProducesEmptyDocument()
    {
        var doc = Build(string.Empty);
        Assert.Empty(doc.Blocks);
    }

    [Fact]
    public void NullInput_ProducesEmptyDocument()
    {
        var doc = new ChatMarkdownAstBuilder().Build(null);
        Assert.Empty(doc.Blocks);
    }

    [Fact]
    public void Paragraph_ProducesSingleParagraphWithTextInline()
    {
        var doc = Build("Hello world");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var text = Assert.IsType<MdInlineText>(Assert.Single(para.Inlines));
        Assert.Equal("Hello world", text.Text);
        Assert.False(text.IsStrong);
        Assert.False(text.IsEmphasis);
    }

    [Theory]
    [InlineData("# Title", 1, "Title")]
    [InlineData("### Sub", 3, "Sub")]
    [InlineData("###### Six", 6, "Six")]
    public void Heading_LevelAndTextCaptured(string md, int level, string text)
    {
        var doc = Build(md);
        var h = Assert.IsType<MdHeading>(Assert.Single(doc.Blocks));
        Assert.Equal(level, h.Level);
        var run = Assert.IsType<MdInlineText>(Assert.Single(h.Inlines));
        Assert.Equal(text, run.Text);
    }

    [Fact]
    public void ThematicBreak_ProducesMdThematicBreak()
    {
        var doc = Build("---\n");
        Assert.IsType<MdThematicBreak>(Assert.Single(doc.Blocks));
    }

    [Fact]
    public void UnorderedList_ProducesMdListWithBulletMarker()
    {
        var doc = Build("- one\n- two\n- three");
        var list = Assert.IsType<MdList>(Assert.Single(doc.Blocks));
        Assert.Equal(MdListMarker.Bullet, list.Marker);
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public void TightList_LiContentSurfacesAsImplicitParagraph()
    {
        // Regression: md4c does NOT wrap tight-list item text in P; the
        // builder must drain pending inlines into an implicit paragraph
        // at LeaveBlock(Li), otherwise bullets render empty and the
        // accumulated text leaks into the next real paragraph.
        var doc = Build("- **URLs** to identify resources\n- HTTP methods\n\nAfter the list.");
        Assert.Equal(2, doc.Blocks.Count);
        var list = Assert.IsType<MdList>(doc.Blocks[0]);
        Assert.Equal(2, list.Items.Count);
        foreach (var item in list.Items)
        {
            var para = Assert.IsType<MdParagraph>(Assert.Single(item.Children));
            Assert.NotEmpty(para.Inlines);
        }
        // First item should contain the bolded "URLs" plus trailing text.
        var firstPara = (MdParagraph)list.Items[0].Children[0];
        Assert.Contains(firstPara.Inlines, i => i is MdInlineText { IsStrong: true, Text: "URLs" });
        Assert.Contains(firstPara.Inlines, i => i is MdInlineText t && t.Text.Contains("identify resources"));
        // The trailing paragraph must contain ONLY its own text, not the
        // accumulated tight-list inlines.
        var trailing = Assert.IsType<MdParagraph>(doc.Blocks[1]);
        var trailingText = string.Concat(trailing.Inlines.OfType<MdInlineText>().Select(t => t.Text));
        Assert.Equal("After the list.", trailingText);
        Assert.DoesNotContain("URLs", trailingText);
        Assert.DoesNotContain("HTTP methods", trailingText);
    }

    [Fact]
    public void TightList_NestedItemsAlsoSurfaceContent()
    {
        var doc = Build("- outer one\n- outer two\n  - inner a\n  - inner b\n");
        var list = Assert.IsType<MdList>(Assert.Single(doc.Blocks));
        Assert.Equal(2, list.Items.Count);
        // Second outer item: paragraph + nested list.
        var outerTwo = list.Items[1];
        Assert.Equal(2, outerTwo.Children.Count);
        var outerTwoPara = Assert.IsType<MdParagraph>(outerTwo.Children[0]);
        Assert.Contains(outerTwoPara.Inlines, i => i is MdInlineText t && t.Text.Contains("outer two"));
        var nested = Assert.IsType<MdList>(outerTwo.Children[1]);
        Assert.Equal(2, nested.Items.Count);
        foreach (var item in nested.Items)
        {
            var para = Assert.IsType<MdParagraph>(Assert.Single(item.Children));
            Assert.NotEmpty(para.Inlines);
        }
    }

    [Fact]
    public void OrderedList_StartNumberCaptured()
    {
        var doc = Build("3. three\n4. four");
        var list = Assert.IsType<MdList>(Assert.Single(doc.Blocks));
        Assert.Equal(MdListMarker.Ordered, list.Marker);
        Assert.Equal(3, list.StartNumber);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void TaskList_StateCaptured()
    {
        var doc = Build("- [x] done\n- [ ] todo");
        var list = Assert.IsType<MdList>(Assert.Single(doc.Blocks));
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(MdTaskState.Checked, list.Items[0].TaskState);
        Assert.Equal(MdTaskState.Unchecked, list.Items[1].TaskState);
    }

    [Fact]
    public void BlockQuote_NestsParagraph()
    {
        var doc = Build("> quoted");
        var quote = Assert.IsType<MdBlockQuote>(Assert.Single(doc.Blocks));
        Assert.IsType<MdParagraph>(Assert.Single(quote.Children));
    }

    [Fact]
    public void FencedCodeBlock_LanguageAndContentCaptured()
    {
        var doc = Build("```csharp\nvar x = 1;\n```\n");
        var code = Assert.IsType<MdCodeBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("csharp", code.Language);
        Assert.Contains("var x = 1;", code.Code);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Tables (the headline use-case)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Table_GFM_ProducesMdTableWithRowsAndAlignment()
    {
        const string md =
            "| Name | Score |\n" +
            "|:-----|------:|\n" +
            "| Ada  | 100   |\n" +
            "| Lin  | 87    |\n";

        var doc = Build(md);
        var table = Assert.IsType<MdTable>(Assert.Single(doc.Blocks));

        Assert.Equal(2, table.ColumnAlignments.Count);
        Assert.Equal(MdColumnAlignment.Left, table.ColumnAlignments[0]);
        Assert.Equal(MdColumnAlignment.Right, table.ColumnAlignments[1]);

        var header = Assert.Single(table.HeaderRows);
        Assert.Equal(2, header.Cells.Count);
        Assert.Equal("Name",
            Assert.IsType<MdInlineText>(Assert.Single(header.Cells[0].Inlines)).Text);
        Assert.Equal("Score",
            Assert.IsType<MdInlineText>(Assert.Single(header.Cells[1].Inlines)).Text);

        Assert.Equal(2, table.BodyRows.Count);
        Assert.Equal("Ada",
            Assert.IsType<MdInlineText>(Assert.Single(table.BodyRows[0].Cells[0].Inlines)).Text);
        Assert.Equal("100",
            Assert.IsType<MdInlineText>(Assert.Single(table.BodyRows[0].Cells[1].Inlines)).Text);
    }

    [Fact]
    public void Table_LikeScreenshotFromTeams_RendersAllRows()
    {
        // Mirrors the structure of the Teams screenshot in the chat thread:
        // a four-column "Recommend APPROVE" table with five tool rows.
        const string md =
            "| # | Tool | Language | Notes |\n" +
            "|---|------|----------|-------|\n" +
            "| 517 | Screen Setup Saver | Python | clean tiny tool |\n" +
            "| 516 | Onboard | Go + JS | local-first |\n" +
            "| 515 | Markdown Editor | JavaScript | live demo |\n" +
            "| 514 | Timekeeper | Rust | SQLite storage |\n" +
            "| 512 | DECKIO | JavaScript | creative concept |\n";

        var doc = Build(md);
        var table = Assert.IsType<MdTable>(Assert.Single(doc.Blocks));
        Assert.Equal(4, table.ColumnAlignments.Count);
        Assert.Single(table.HeaderRows);
        Assert.Equal(5, table.BodyRows.Count);
        Assert.All(table.BodyRows, row => Assert.Equal(4, row.Cells.Count));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Inline styling
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void StrongAndEmphasis_FlagsPropagateToInline()
    {
        var doc = Build("**bold** and *em*");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var runs = para.Inlines.OfType<MdInlineText>().ToArray();
        Assert.Contains(runs, r => r.Text == "bold" && r.IsStrong);
        Assert.Contains(runs, r => r.Text == "em" && r.IsEmphasis);
    }

    [Fact]
    public void InlineCode_FlagsRunAsCode()
    {
        var doc = Build("run `dotnet build`");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var code = para.Inlines.OfType<MdInlineText>().Single(r => r.IsCode);
        Assert.Equal("dotnet build", code.Text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  HTML entity decoding
    // ────────────────────────────────────────────────────────────────────

    private static string FlattenText(ChatMarkdownDocument doc)
        => string.Concat(
            doc.Blocks.OfType<MdParagraph>()
                .SelectMany(p => p.Inlines.OfType<MdInlineText>())
                .Select(t => t.Text));

    [Fact]
    public void NamedEntity_AmpersandDecodes()
    {
        // Regression: md4c emits "&amp;" as MarkdownTextType.Entity with the
        // raw token; without decoding the user sees "AT&amp;T" in the bubble.
        var doc = Build("AT&amp;T");
        Assert.Equal("AT&T", FlattenText(doc));
    }

    [Fact]
    public void NamedEntity_CopyrightDecodes()
    {
        var doc = Build("Hello &copy; World");
        Assert.Equal("Hello © World", FlattenText(doc));
    }

    [Theory]
    [InlineData("&lt;", "<")]
    [InlineData("&gt;", ">")]
    [InlineData("&quot;", "\"")]
    [InlineData("&apos;", "'")]
    [InlineData("&nbsp;", "\u00A0")]
    [InlineData("&reg;", "\u00AE")]
    [InlineData("&hellip;", "\u2026")]
    [InlineData("&mdash;", "\u2014")]
    public void NamedEntity_CommonEntitiesDecode(string entity, string expected)
    {
        var doc = Build("x" + entity + "y");
        Assert.Equal("x" + expected + "y", FlattenText(doc));
    }

    [Fact]
    public void NumericEntity_DecimalDecodes()
    {
        var doc = Build("&#65;&#66;&#67;");
        Assert.Equal("ABC", FlattenText(doc));
    }

    [Fact]
    public void NumericEntity_HexDecodesEitherCase()
    {
        var doc = Build("&#x41;&#X42;&#x4a;");
        Assert.Equal("ABJ", FlattenText(doc));
    }

    [Fact]
    public void NumericEntity_SupplementaryCodepointEmitsSurrogatePair()
    {
        // U+1F600 GRINNING FACE 😀 — must serialize as a 2-char surrogate pair.
        var doc = Build("smile &#x1F600; here");
        var flat = FlattenText(doc);
        Assert.Equal("smile \U0001F600 here", flat);
        Assert.Equal(13, flat.Length); // "smile " (6) + surrogate pair (2) + " here" (5)
    }

    [Fact]
    public void NumericEntity_NulCodepointReplacedWithU_FFFD()
    {
        // CommonMark: &#0; must become U+FFFD, never a literal NUL.
        var doc = Build("before&#0;after");
        Assert.Equal("before\uFFFDafter", FlattenText(doc));
    }

    [Fact]
    public void NumericEntity_SurrogateReplacedWithU_FFFD()
    {
        // CommonMark: codepoints in the surrogate range must be U+FFFD.
        var doc = Build("x&#xD800;y");
        Assert.Equal("x\uFFFDy", FlattenText(doc));
    }

    [Fact]
    public void UnknownNamedEntity_PassesThroughVerbatim()
    {
        // We must not silently swallow text the user wrote.
        var doc = Build("foo &totallymadeup; bar");
        Assert.Equal("foo &totallymadeup; bar", FlattenText(doc));
    }

    [Fact]
    public void Entity_InsideStrongSpan_Decodes()
    {
        var doc = Build("**AT&amp;T**");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var runs = para.Inlines.OfType<MdInlineText>().ToArray();
        Assert.Contains(runs, r => r.IsStrong && r.Text.Contains('&'));
        Assert.Equal("AT&T", string.Concat(runs.Where(r => r.IsStrong).Select(r => r.Text)));
    }

    [Fact]
    public void NumericEntity_MaxValidCodepointDecodes()
    {
        // U+10FFFF is the highest valid Unicode scalar value. It is a non-character
        // but DecodeEntity should still emit it as a surrogate pair, not U+FFFD.
        var doc = Build("x&#x10FFFF;y");
        Assert.Equal("x\U0010FFFFy", FlattenText(doc));
    }

    [Fact]
    public void NumericEntity_AboveMaxCodepointReplacedWithU_FFFD()
    {
        // &#x110000; is one past the max valid codepoint → must fold to U+FFFD.
        // md4c may pass this through as Entity or as Normal text depending on
        // its own validation; either way the rendered text must contain the
        // U+FFFD replacement character (and never the raw "110000" token).
        var doc = Build("x&#x110000;y");
        var flat = FlattenText(doc);
        Assert.DoesNotContain("110000", flat);
        Assert.Contains("\uFFFD", flat);
    }

    [Fact]
    public void NamedEntity_TwoCodepointSequenceDecodes()
    {
        // &fjlig; → "fj" (codepoints U+0066 U+006A). Exercises the cp1 != 0
        // branch which would otherwise have zero test coverage.
        var doc = Build("a&fjlig;b");
        Assert.Equal("afjb", FlattenText(doc));
    }

    [Fact]
    public void Strikethrough_FlagsRunAsStrike()
    {
        var doc = Build("~~gone~~");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        Assert.Contains(para.Inlines.OfType<MdInlineText>(), r => r.Text == "gone" && r.IsStrike);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Security posture: links + images + raw HTML
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Link_FlattenedToInertTextWithUrl()
    {
        var doc = Build("[example](https://example.com)");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var run = Assert.IsType<MdInlineText>(Assert.Single(para.Inlines));
        Assert.Equal("example (https://example.com)", run.Text);
    }

    [Fact]
    public void Link_DisplayEqualsHref_RendersOnlyOnce()
    {
        var doc = Build("[https://example.com](https://example.com)");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var run = Assert.IsType<MdInlineText>(Assert.Single(para.Inlines));
        Assert.Equal("https://example.com", run.Text);
    }

    [Fact]
    public void Autolink_FlattenedToInertText()
    {
        var doc = Build("see https://example.com please");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var joined = string.Join("", para.Inlines.OfType<MdInlineText>().Select(r => r.Text));
        Assert.Contains("https://example.com", joined);
        // No part of the AST should carry an href — the AST type doesn't
        // even have a place for it.
    }

    [Fact]
    public void Image_FlattenedToInertAltText()
    {
        var doc = Build("![puppy](https://example.com/p.png)");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var run = Assert.IsType<MdInlineText>(Assert.Single(para.Inlines));
        Assert.Equal("[Image: puppy]", run.Text);
    }

    [Fact]
    public void Image_WithoutAlt_RendersBareImageMarker()
    {
        var doc = Build("![](https://example.com/p.png)");
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var run = Assert.IsType<MdInlineText>(Assert.Single(para.Inlines));
        Assert.Equal("[Image]", run.Text);
    }

    [Fact]
    public void RawHtmlBlock_NotEmittedUnderDefaultNoHtmlFlag()
    {
        var doc = Build("<script>alert(1)</script>");
        Assert.DoesNotContain(doc.Blocks, b => b is MdRawTextBlock);
        // The leak surface for raw HTML in NoHtml mode is a text run inside
        // a paragraph. Verify the script tag is preserved as inert text.
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var joined = string.Join("", para.Inlines.OfType<MdInlineText>().Select(r => r.Text));
        Assert.Contains("alert(1)", joined);
    }

    [Fact]
    public void OversizedInput_TruncatedAndMarked()
    {
        var huge = new string('a', ChatMarkdownAstBuilder.MaxInputBytes + 100);
        var doc = new ChatMarkdownAstBuilder().Build(huge);
        var para = Assert.IsType<MdParagraph>(Assert.Single(doc.Blocks));
        var run = Assert.IsType<MdInlineText>(Assert.Single(para.Inlines));
        Assert.Contains("[truncated", run.Text);
    }
}
