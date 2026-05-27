using System.Collections.Generic;
using System.Text;
using OpenClaw.Shared.Markdown.Md4c;
using Xunit;

namespace OpenClaw.Shared.Tests.Markdown;

/// <summary>
/// Smoke tests for the vendored md4c SAX parser. These tests do NOT attempt to
/// re-validate the full CommonMark/GFM spec (the upstream Reactor project owns
/// that coverage) — they only assert the constructs the OpenClaw chat
/// renderer relies on:
///
/// <list type="bullet">
///   <item>Block events fire in the expected order for paragraphs,
///         headings, lists, blockquotes, code blocks, horizontal rules.</item>
///   <item>GFM table extension surfaces Table/Thead/Tbody/Tr/Th/Td blocks
///         with the right column count and alignments.</item>
///   <item>Inline spans (Em/Strong/Code/Del/A/Img) round-trip and carry
///         their detail payloads.</item>
///   <item>The parser returns 0 (success) for all supported inputs and
///         does not throw.</item>
/// </list>
///
/// These tests use a lightweight event-recording visitor instead of pulling
/// in the (un-vendored) <c>MarkdownHtml</c> renderer.
/// </summary>
public class Md4cParserTests
{
    private sealed class EventRecorder
    {
        public List<string> Events { get; } = new();

        public int EnterBlock(MarkdownBlockType type, object? detail)
        {
            Events.Add($"+{type}");
            return 0;
        }

        public int LeaveBlock(MarkdownBlockType type, object? detail)
        {
            Events.Add($"-{type}");
            return 0;
        }

        public int EnterSpan(MarkdownSpanType type, object? detail)
        {
            Events.Add($"+span:{type}");
            return 0;
        }

        public int LeaveSpan(MarkdownSpanType type, object? detail)
        {
            Events.Add($"-span:{type}");
            return 0;
        }

        public int Text(MarkdownTextType type, System.ReadOnlySpan<char> text)
        {
            Events.Add($"text:{type}:{text.ToString()}");
            return 0;
        }
    }

    private static (int ret, List<string> events) Parse(
        string markdown,
        MarkdownParserFlags flags = MarkdownParserFlags.DialectGitHub)
    {
        var rec = new EventRecorder();
        int ret = Md4cParser.Parse(
            markdown, flags,
            rec.EnterBlock, rec.LeaveBlock,
            rec.EnterSpan, rec.LeaveSpan,
            rec.Text);
        return (ret, rec.Events);
    }

    [Fact]
    public void Paragraph_FiresPEvents()
    {
        var (ret, events) = Parse("Hello world");
        Assert.Equal(0, ret);
        Assert.Contains("+P", events);
        Assert.Contains("-P", events);
        Assert.Contains("text:Normal:Hello world", events);
    }

    [Theory]
    [InlineData("# H1", 1)]
    [InlineData("## H2", 2)]
    [InlineData("### H3", 3)]
    [InlineData("###### H6", 6)]
    public void Heading_AtxLevelCarriedInDetail(string markdown, int expectedLevel)
    {
        int? capturedLevel = null;
        int ret = Md4cParser.Parse(
            markdown, MarkdownParserFlags.DialectGitHub,
            (type, detail) =>
            {
                if (type == MarkdownBlockType.H && detail is MarkdownBlockHDetail h)
                    capturedLevel = h.Level;
                return 0;
            },
            (_, _) => 0, (_, _) => 0, (_, _) => 0, (_, _) => 0);

        Assert.Equal(0, ret);
        Assert.Equal(expectedLevel, capturedLevel);
    }

    [Fact]
    public void UnorderedList_EmitsUlLiBlocks()
    {
        var (ret, events) = Parse("- one\n- two\n- three");
        Assert.Equal(0, ret);
        Assert.Contains("+Ul", events);
        Assert.Contains("-Ul", events);
        Assert.Equal(3, events.FindAll(e => e == "+Li").Count);
    }

    [Fact]
    public void OrderedList_StartCarriedInDetail()
    {
        int? start = null;
        int ret = Md4cParser.Parse(
            "3. three\n4. four",
            MarkdownParserFlags.DialectGitHub,
            (type, detail) =>
            {
                if (type == MarkdownBlockType.Ol && detail is MarkdownBlockOlDetail ol)
                    start = ol.Start;
                return 0;
            },
            (_, _) => 0, (_, _) => 0, (_, _) => 0, (_, _) => 0);

        Assert.Equal(0, ret);
        Assert.Equal(3, start);
    }

    [Fact]
    public void TaskList_FlagsItemAsTask()
    {
        var taskMarks = new List<char>();
        int ret = Md4cParser.Parse(
            "- [x] done\n- [ ] todo",
            MarkdownParserFlags.DialectGitHub,
            (type, detail) =>
            {
                if (type == MarkdownBlockType.Li && detail is MarkdownBlockLiDetail li && li.IsTask)
                    taskMarks.Add(li.TaskMark);
                return 0;
            },
            (_, _) => 0, (_, _) => 0, (_, _) => 0, (_, _) => 0);

        Assert.Equal(0, ret);
        Assert.Equal(new[] { 'x', ' ' }, taskMarks.ToArray());
    }

    [Fact]
    public void Table_GFM_EmitsTableHeadBodyAndCorrectColumnCount()
    {
        const string md =
            "| Name | Score |\n" +
            "|------|------:|\n" +
            "| Ada  |   100 |\n" +
            "| Lin  |    87 |\n";

        int? colCount = null;
        int? headRows = null;
        int? bodyRows = null;
        var aligns = new List<MarkdownAlign>();
        var events = new List<string>();

        int ret = Md4cParser.Parse(
            md, MarkdownParserFlags.DialectGitHub,
            (type, detail) =>
            {
                events.Add($"+{type}");
                if (type == MarkdownBlockType.Table && detail is MarkdownBlockTableDetail tbl)
                {
                    colCount = tbl.ColCount;
                    headRows = tbl.HeadRowCount;
                    bodyRows = tbl.BodyRowCount;
                }
                if (type == MarkdownBlockType.Th && detail is MarkdownBlockTdDetail th)
                    aligns.Add(th.Align);
                return 0;
            },
            (type, _) => { events.Add($"-{type}"); return 0; },
            (_, _) => 0, (_, _) => 0, (_, _) => 0);

        Assert.Equal(0, ret);
        Assert.Contains("+Table", events);
        Assert.Contains("+Thead", events);
        Assert.Contains("+Tbody", events);
        Assert.Equal(2, colCount);
        Assert.Equal(1, headRows);
        Assert.Equal(2, bodyRows);
        Assert.Equal(new[] { MarkdownAlign.Default, MarkdownAlign.Right }, aligns.ToArray());
    }

    [Fact]
    public void FencedCode_CarriesLangDetail()
    {
        string? lang = null;
        var body = new StringBuilder();
        bool inCode = false;
        int ret = Md4cParser.Parse(
            "```csharp\nvar x = 1;\n```\n",
            MarkdownParserFlags.DialectGitHub,
            (type, detail) =>
            {
                if (type == MarkdownBlockType.Code)
                {
                    inCode = true;
                    if (detail is MarkdownBlockCodeDetail c) lang = c.Lang.Text;
                }
                return 0;
            },
            (type, _) => { if (type == MarkdownBlockType.Code) inCode = false; return 0; },
            (_, _) => 0, (_, _) => 0,
            (textType, span) => { if (inCode) body.Append(span.ToString()); return 0; });

        Assert.Equal(0, ret);
        Assert.Equal("csharp", lang);
        Assert.Contains("var x = 1;", body.ToString());
    }

    [Fact]
    public void EmphasisAndStrong_EmitSpans()
    {
        var (ret, events) = Parse("*em* and **strong**");
        Assert.Equal(0, ret);
        Assert.Contains("+span:Em", events);
        Assert.Contains("-span:Em", events);
        Assert.Contains("+span:Strong", events);
        Assert.Contains("-span:Strong", events);
    }

    [Fact]
    public void InlineCode_EmitsCodeSpan()
    {
        var (ret, events) = Parse("Try `dotnet build` here.");
        Assert.Equal(0, ret);
        Assert.Contains("+span:Code", events);
        Assert.Contains("text:Code:dotnet build", events);
    }

    [Fact]
    public void Link_HrefCarriedInDetail()
    {
        string? href = null;
        int ret = Md4cParser.Parse(
            "[example](https://example.com)",
            MarkdownParserFlags.DialectGitHub,
            (_, _) => 0, (_, _) => 0,
            (type, detail) =>
            {
                if (type == MarkdownSpanType.A && detail is MarkdownSpanADetail a)
                    href = a.Href.Text;
                return 0;
            },
            (_, _) => 0,
            (_, _) => 0);

        Assert.Equal(0, ret);
        Assert.Equal("https://example.com", href);
    }

    [Fact]
    public void Image_SrcCarriedInDetail()
    {
        string? src = null;
        int ret = Md4cParser.Parse(
            "![alt](https://example.com/a.png)",
            MarkdownParserFlags.DialectGitHub,
            (_, _) => 0, (_, _) => 0,
            (type, detail) =>
            {
                if (type == MarkdownSpanType.Img && detail is MarkdownSpanImgDetail img)
                    src = img.Src.Text;
                return 0;
            },
            (_, _) => 0,
            (_, _) => 0);

        Assert.Equal(0, ret);
        Assert.Equal("https://example.com/a.png", src);
    }

    [Fact]
    public void Blockquote_EmitsQuoteBlock()
    {
        var (ret, events) = Parse("> quoted");
        Assert.Equal(0, ret);
        Assert.Contains("+Quote", events);
        Assert.Contains("-Quote", events);
    }

    [Fact]
    public void ThematicBreak_EmitsHr()
    {
        var (ret, events) = Parse("---\n");
        Assert.Equal(0, ret);
        Assert.Contains("+Hr", events);
    }

    [Fact]
    public void NoHtmlFlag_SuppressesRawHtmlBlocks()
    {
        var (ret, events) = Parse(
            "<div>hi</div>\n",
            MarkdownParserFlags.DialectGitHub | MarkdownParserFlags.NoHtmlBlocks);
        Assert.Equal(0, ret);
        Assert.DoesNotContain("+Html", events);
    }

    [Fact]
    public void EmptyInput_ParsesCleanly()
    {
        var (ret, events) = Parse(string.Empty);
        Assert.Equal(0, ret);
        Assert.Contains("+Doc", events);
        Assert.Contains("-Doc", events);
    }
}
