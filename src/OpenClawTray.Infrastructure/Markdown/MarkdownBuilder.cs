using System.Text;
using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Markdown;

// AI-HINT: SAX-style visitor that receives md4c parser callbacks and builds a Reactor Element tree.
//   Pattern: md4c calls OnEnterBlock/OnLeaveBlock/OnEnterSpan/OnLeaveSpan/OnText in document order.
//   _blockStack: push on EnterBlock, pop on LeaveBlock; children accumulate at each level.
//   _inlines: accumulates RichTextInline runs for the current text-bearing block.
//   Formatting state (_boldDepth, _italicDepth, _isCode, etc.) toggles in Enter/LeaveSpan.
//   Special accumulators: _codeAccum for code blocks, _htmlAccum for HTML blocks.
//   MarkdownOptions allows per-block-type render customization via callbacks.

/// <summary>
/// Options for customizing how markdown is rendered to Reactor elements.
/// All callback properties are optional — null means use the default rendering.
/// </summary>
public record MarkdownOptions
{
    /// <summary>Override heading rendering. (level 1-6, defaultElement)</summary>
    public Func<int, Element, Element>? Heading { get; init; }
    /// <summary>Override paragraph rendering. (defaultElement)</summary>
    public Func<Element, Element>? Paragraph { get; init; }
    /// <summary>Override block quote rendering. (defaultElement)</summary>
    public Func<Element, Element>? BlockQuote { get; init; }
    /// <summary>Override unordered list rendering. (items)</summary>
    public Func<Element[], Element>? UnorderedList { get; init; }
    /// <summary>Override ordered list rendering. (startNumber, items)</summary>
    public Func<int, Element[], Element>? OrderedList { get; init; }
    /// <summary>Override list item rendering. (defaultElement)</summary>
    public Func<Element, Element>? ListItem { get; init; }
    /// <summary>Override code block rendering. (code, language)</summary>
    public Func<string, string?, Element>? CodeBlock { get; init; }
    /// <summary>Override thematic break rendering.</summary>
    public Func<Element>? ThematicBreak { get; init; }
    /// <summary>Override table rendering. (rows, columnAlignments)</summary>
    public Func<Element[], MdAlign[], Element>? Table { get; init; }
    /// <summary>Override raw HTML block rendering. (rawHtml)</summary>
    public Func<string, Element>? HtmlBlock { get; init; }
    /// <summary>Override image rendering. (altText, src)</summary>
    public Func<string, Uri, Element>? Image { get; init; }

    /// <summary>Font family for inline code and code blocks. Default: "Consolas".</summary>
    public string CodeFontFamily { get; init; } = "Consolas";
    /// <summary>Parser flags. Default: DialectGitHub (tables, strikethrough, task lists).</summary>
    public MdParserFlags ParserFlags { get; init; } = MdParserFlags.DialectGitHub;
}

/// <summary>
/// Builds a Reactor Element tree from markdown using the md4c SAX parser.
/// </summary>
internal sealed class MarkdownBuilder
{
    private readonly MarkdownOptions _options;

    // Block stack — each frame accumulates child elements for that block.
    private readonly struct BlockFrame
    {
        public readonly MdBlockType Type;
        public readonly object? Detail;
        public readonly List<Element> Children;

        public BlockFrame(MdBlockType type, object? detail)
        {
            Type = type;
            Detail = detail;
            Children = new List<Element>();
        }
    }

    private readonly Stack<BlockFrame> _blockStack = new();

    // Inline accumulator for the current text-bearing block (P, H, Th, Td).
    private readonly List<RichTextInline> _inlines = new();

    // Code block text accumulator.
    private StringBuilder? _codeAccum;
    private string? _codeBlockLang;

    // HTML block accumulator.
    private StringBuilder? _htmlAccum;

    // Inline formatting state (toggled by EnterSpan/LeaveSpan).
    private int _boldDepth;
    private int _italicDepth;
    private bool _isCode;
    private bool _isStrikethrough;

    // Link tracking.
    private Uri? _linkUri;
    private int _linkInlineStart;

    // Image tracking.
    private bool _insideImage;
    private StringBuilder? _imageAlt;
    private Uri? _imageSrc;

    // Table state.
    private int _tableColCount;
    private MdAlign[]? _tableAligns;
    private readonly List<Element> _tableAllCells = new();
    private int _tableRowIndex;
    private int _tableCellIndex;

    // List item numbering.
    private int _listItemIndex;

    // Result.
    private Element? _result;

    private MarkdownBuilder(MarkdownOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Parse markdown and build a Reactor Element tree.
    /// </summary>
    internal static Element Build(string markdown, MarkdownOptions? options)
    {
        options ??= new MarkdownOptions();
        var builder = new MarkdownBuilder(options);
        int ret = Md4cParser.Parse(
            markdown,
            options.ParserFlags,
            builder.OnEnterBlock,
            builder.OnLeaveBlock,
            builder.OnEnterSpan,
            builder.OnLeaveSpan,
            builder.OnText);
        if (ret != 0)
            global::System.Diagnostics.Debug.WriteLine($"MarkdownBuilder: md4c parse failed with code {ret}");
        return builder._result ?? VStack();
    }

    // ── Block callbacks ──────────────────────────────────────────────────

    private int OnEnterBlock(MdBlockType type, object? detail)
    {
        switch (type)
        {
            case MdBlockType.Doc:
            case MdBlockType.Quote:
            case MdBlockType.Ul:
            case MdBlockType.Ol:
            case MdBlockType.Li:
            case MdBlockType.P:
            case MdBlockType.H:
            case MdBlockType.Thead:
            case MdBlockType.Tbody:
            case MdBlockType.Tr:
                _blockStack.Push(new BlockFrame(type, detail));
                break;

            case MdBlockType.Code:
                _blockStack.Push(new BlockFrame(type, detail));
                _codeAccum = new StringBuilder();
                if (detail is MdBlockCodeDetail codeDet && codeDet.Lang.Text is not null)
                    _codeBlockLang = codeDet.Lang.Text;
                else
                    _codeBlockLang = null;
                break;

            case MdBlockType.Html:
                _blockStack.Push(new BlockFrame(type, detail));
                _htmlAccum = new StringBuilder();
                break;

            case MdBlockType.Table:
                _blockStack.Push(new BlockFrame(type, detail));
                if (detail is MdBlockTableDetail tableDet)
                {
                    _tableColCount = tableDet.ColCount;
                    _tableAligns = new MdAlign[_tableColCount];
                    _tableAllCells.Clear();
                    _tableRowIndex = 0;
                }
                break;

            case MdBlockType.Th:
            case MdBlockType.Td:
                _blockStack.Push(new BlockFrame(type, detail));
                _inlines.Clear();
                break;

            case MdBlockType.Hr:
                // Hr has no children — produce it immediately.
                break;
        }

        // Reset list item index on entering a list.
        if (type == MdBlockType.Ul || type == MdBlockType.Ol)
            _listItemIndex = 0;

        // Prepare inlines for text-bearing blocks.
        if (type == MdBlockType.P || type == MdBlockType.H)
            _inlines.Clear();

        return 0;
    }

    private int OnLeaveBlock(MdBlockType type, object? detail)
    {
        switch (type)
        {
            case MdBlockType.Doc:
                LeaveDoc();
                break;
            case MdBlockType.P:
                LeaveParagraph();
                break;
            case MdBlockType.H:
                LeaveHeading(detail);
                break;
            case MdBlockType.Quote:
                LeaveBlockQuote();
                break;
            case MdBlockType.Ul:
                LeaveUnorderedList();
                break;
            case MdBlockType.Ol:
                LeaveOrderedList(detail);
                break;
            case MdBlockType.Li:
                LeaveListItem(detail);
                break;
            case MdBlockType.Code:
                LeaveCodeBlock();
                break;
            case MdBlockType.Html:
                LeaveHtmlBlock();
                break;
            case MdBlockType.Hr:
                LeaveThematicBreak();
                break;
            case MdBlockType.Table:
                LeaveTable();
                break;
            case MdBlockType.Thead:
            case MdBlockType.Tbody:
                LeaveTableSection();
                break;
            case MdBlockType.Tr:
                LeaveTableRow();
                break;
            case MdBlockType.Th:
            case MdBlockType.Td:
                LeaveTableCell(type, detail);
                break;
        }

        return 0;
    }

    // ── Block leave helpers ──────────────────────────────────────────────

    private void LeaveDoc()
    {
        var frame = _blockStack.Pop();
        _result = VStack(8, frame.Children.ToArray());
    }

    private void LeaveParagraph()
    {
        var frame = _blockStack.Pop();
        var inlines = _inlines.ToArray();
        _inlines.Clear();

        Element element = MakeRichText(inlines);
        if (_options.Paragraph is not null)
            element = _options.Paragraph(element);

        AddToParent(element);
    }

    private void LeaveHeading(object? detail)
    {
        var frame = _blockStack.Pop();
        int level = detail is MdBlockHDetail h ? h.Level : 1;
        var inlines = _inlines.ToArray();
        _inlines.Clear();

        // Prepend bold to all runs for heading weight.
        var boldInlines = new RichTextInline[inlines.Length];
        for (int i = 0; i < inlines.Length; i++)
        {
            boldInlines[i] = inlines[i] switch
            {
                RichTextRun run => run with { IsBold = true },
                _ => inlines[i],
            };
        }

        double fontSize = level switch
        {
            1 => 28,
            2 => 24,
            3 => 20,
            4 => 18,
            5 => 16,
            _ => 14,
        };

        Element element = MakeRichText(boldInlines, fontSize);
        if (_options.Heading is not null)
            element = _options.Heading(level, element);

        AddToParent(element);
    }

    private void LeaveBlockQuote()
    {
        var frame = _blockStack.Pop();
        var content = VStack(4, frame.Children.ToArray());

        Element element = Border(content)
            .Padding(0, 8, 16, 8)
            .Background("#F5F5F5")
            .WithBorder("#D0D0D0", 1);

        // Apply left-accent via native setter for asymmetric border.
        element = ((BorderElement)element) with
        {
            Setters = [.. ((BorderElement)element).Setters, b =>
            {
                b.BorderThickness = new Thickness(3, 0, 0, 0);
                b.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray);
            }]
        };

        if (_options.BlockQuote is not null)
            element = _options.BlockQuote(element);

        AddToParent(element);
    }

    private void LeaveUnorderedList()
    {
        var frame = _blockStack.Pop();
        var items = frame.Children.ToArray();

        Element element;
        if (_options.UnorderedList is not null)
            element = _options.UnorderedList(items);
        else
            element = VStack(2, items);

        AddToParent(element);
    }

    private void LeaveOrderedList(object? detail)
    {
        var frame = _blockStack.Pop();
        int start = detail is MdBlockOlDetail ol ? ol.Start : 1;
        var items = frame.Children.ToArray();

        Element element;
        if (_options.OrderedList is not null)
            element = _options.OrderedList(start, items);
        else
            element = VStack(2, items);

        AddToParent(element);
    }

    private void LeaveListItem(object? detail)
    {
        var frame = _blockStack.Pop();
        _listItemIndex++;

        // Tight lists: md4c suppresses P blocks, so text arrives directly in Li.
        // Flush any pending inlines as a paragraph.
        FlushPendingInlines(frame);

        // Determine the bullet/number marker.
        string marker;
        var liDetail = detail as MdBlockLiDetail?;
        if (liDetail?.IsTask == true)
        {
            marker = (liDetail.Value.TaskMark == 'x' || liDetail.Value.TaskMark == 'X')
                ? "\u2611 " : "\u2610 "; // checked / unchecked checkbox
        }
        else
        {
            // Check if parent is Ol or Ul.
            bool isOrdered = _blockStack.Count > 0 && _blockStack.Peek().Type == MdBlockType.Ol;
            if (isOrdered)
            {
                int start = _blockStack.Peek().Detail is MdBlockOlDetail olDet ? olDet.Start : 1;
                marker = $"{start + _listItemIndex - 1}. ";
            }
            else
            {
                marker = "\u2022 "; // bullet
            }
        }

        Element content;
        if (frame.Children.Count == 1)
            content = frame.Children[0];
        else if (frame.Children.Count > 1)
            content = VStack(4, frame.Children.ToArray());
        else
            content = TextBlock(""); // empty fallback

        Element element = HStack(4,
            TextBlock(marker).VAlign(VerticalAlignment.Top),
            content
        );

        if (_options.ListItem is not null)
            element = _options.ListItem(element);

        AddToParent(element);
    }

    /// <summary>
    /// If there are accumulated inlines not yet flushed into a paragraph element,
    /// flush them now. This handles tight lists where md4c suppresses P blocks.
    /// </summary>
    private void FlushPendingInlines(BlockFrame frame)
    {
        if (_inlines.Count > 0)
        {
            var inlines = _inlines.ToArray();
            _inlines.Clear();
            frame.Children.Add(MakeRichText(inlines));
        }
    }

    private void LeaveCodeBlock()
    {
        var frame = _blockStack.Pop();
        string code = _codeAccum?.ToString().TrimEnd('\n') ?? "";
        string? lang = _codeBlockLang;
        _codeAccum = null;
        _codeBlockLang = null;

        Element element;
        if (_options.CodeBlock is not null)
        {
            element = _options.CodeBlock(code, lang);
        }
        else
        {
            element = Border(
                new RichTextBlockElement("")
                {
                    Paragraphs = [new RichTextParagraph([
                        new RichTextRun(code) { FontFamily = _options.CodeFontFamily }
                    ])],
                    IsTextSelectionEnabled = true,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                }
            )
            .Background("#F5F5F5")
            .Padding(12)
            .CornerRadius(4);
        }

        AddToParent(element);
    }

    private void LeaveHtmlBlock()
    {
        var frame = _blockStack.Pop();
        string html = _htmlAccum?.ToString() ?? "";
        _htmlAccum = null;

        Element element;
        if (_options.HtmlBlock is not null)
            element = _options.HtmlBlock(html);
        else
            element = TextBlock(html).Foreground("#888888");

        AddToParent(element);
    }

    private void LeaveThematicBreak()
    {
        Element element;
        if (_options.ThematicBreak is not null)
            element = _options.ThematicBreak();
        else
            element = Border(VStack()).Height(1).Background("#CCCCCC").Margin(0, 4, 0, 4);

        AddToParent(element);
    }

    private void LeaveTable()
    {
        var frame = _blockStack.Pop();
        var aligns = _tableAligns ?? Array.Empty<MdAlign>();

        if (_options.Table is not null)
        {
            // Pass rows (as children of Thead/Tbody frames) and alignments.
            var rows = frame.Children.ToArray();
            var element = _options.Table(rows, aligns);
            AddToParent(element);
            return;
        }

        // Build a Grid.
        int colCount = _tableColCount;
        int rowCount = _tableRowIndex;
        if (colCount == 0 || rowCount == 0)
        {
            AddToParent(VStack());
            return;
        }

        var columns = Enumerable.Repeat("*", colCount).ToArray();
        var rows2 = Enumerable.Repeat("Auto", rowCount).ToArray();
        var cells = _tableAllCells.ToArray();

        var grid = Grid(columns, rows2, cells);

        AddToParent(grid);
    }

    private void LeaveTableSection()
    {
        var frame = _blockStack.Pop();
        // Thead/Tbody are passthrough — rows are already added to tableAllCells.
    }

    private void LeaveTableRow()
    {
        var frame = _blockStack.Pop();
        _tableRowIndex++;
        _tableCellIndex = 0;
    }

    private void LeaveTableCell(MdBlockType type, object? detail)
    {
        var frame = _blockStack.Pop();
        var inlines = _inlines.ToArray();
        _inlines.Clear();

        bool isHeader = type == MdBlockType.Th;
        var align = detail is MdBlockTdDetail td ? td.Align : MdAlign.Default;

        // Store alignment from header cells.
        if (isHeader && _tableAligns is not null && _tableCellIndex < _tableAligns.Length)
            _tableAligns[_tableCellIndex] = align;

        // Make header text bold.
        if (isHeader)
        {
            for (int i = 0; i < inlines.Length; i++)
            {
                if (inlines[i] is RichTextRun run)
                    inlines[i] = run with { IsBold = true };
            }
        }

        Element cell = MakeRichText(inlines);

        // Apply alignment.
        cell = align switch
        {
            MdAlign.Center => cell.HAlign(HorizontalAlignment.Center),
            MdAlign.Right => cell.HAlign(HorizontalAlignment.Right),
            _ => cell.HAlign(HorizontalAlignment.Left),
        };

        // Position in grid and add padding.
        cell = cell
            .Grid(row: _tableRowIndex, column: _tableCellIndex)
            .Padding(4, 2, 4, 2);

        // Header row gets subtle background.
        if (isHeader)
            cell = cell.Background("#F0F0F0");

        _tableAllCells.Add(cell);
        _tableCellIndex++;
    }

    // ── Span callbacks ───────────────────────────────────────────────────

    private int OnEnterSpan(MdSpanType type, object? detail)
    {
        switch (type)
        {
            case MdSpanType.Strong:
                _boldDepth++;
                break;
            case MdSpanType.Em:
                _italicDepth++;
                break;
            case MdSpanType.Code:
                _isCode = true;
                break;
            case MdSpanType.Del:
                _isStrikethrough = true;
                break;
            case MdSpanType.A:
                if (detail is MdSpanADetail a && a.Href.Text is not null)
                {
                    Uri.TryCreate(a.Href.Text, UriKind.RelativeOrAbsolute, out var uri);
                    _linkUri = IsSafeUri(uri) ? uri : null;
                }
                _linkInlineStart = _inlines.Count;
                break;
            case MdSpanType.Img:
                _insideImage = true;
                _imageAlt = new StringBuilder();
                if (detail is MdSpanImgDetail img && img.Src.Text is not null)
                {
                    Uri.TryCreate(img.Src.Text, UriKind.RelativeOrAbsolute, out var imgUri);
                    _imageSrc = IsSafeUri(imgUri) ? imgUri : null;
                }
                break;
        }
        return 0;
    }

    private int OnLeaveSpan(MdSpanType type, object? detail)
    {
        switch (type)
        {
            case MdSpanType.Strong:
                _boldDepth = Math.Max(0, _boldDepth - 1);
                break;
            case MdSpanType.Em:
                _italicDepth = Math.Max(0, _italicDepth - 1);
                break;
            case MdSpanType.Code:
                _isCode = false;
                break;
            case MdSpanType.Del:
                _isStrikethrough = false;
                break;
            case MdSpanType.A:
                LeaveLink();
                break;
            case MdSpanType.Img:
                LeaveImage();
                break;
        }
        return 0;
    }

    private static bool IsSafeUri(Uri? uri)
    {
        if (uri is null) return false;
        if (!uri.IsAbsoluteUri) return true;
        var scheme = uri.Scheme;
        return scheme is "http" or "https" or "mailto";
    }

    private void LeaveLink()
    {
        if (_linkUri is null)
        {
            _linkUri = null;
            return;
        }

        // Flatten all inlines accumulated since the link opened into plain text.
        var sb = new StringBuilder();
        for (int i = _linkInlineStart; i < _inlines.Count; i++)
        {
            if (_inlines[i] is RichTextRun run)
                sb.Append(run.Text);
        }

        // Remove the inlines that were part of the link.
        if (_linkInlineStart < _inlines.Count)
            _inlines.RemoveRange(_linkInlineStart, _inlines.Count - _linkInlineStart);

        // Add the hyperlink inline.
        _inlines.Add(new RichTextHyperlink(sb.ToString(), _linkUri));
        _linkUri = null;
    }

    private void LeaveImage()
    {
        _insideImage = false;
        string alt = _imageAlt?.ToString() ?? "";
        _imageAlt = null;

        if (_imageSrc is not null)
        {
            Element img;
            if (_options.Image is not null)
                img = _options.Image(alt, _imageSrc);
            else
                img = Image(_imageSrc.ToString());

            // Images are block-level — add to parent frame.
            if (_blockStack.Count > 0)
                _blockStack.Peek().Children.Add(img);
        }
        _imageSrc = null;
    }

    // ── Text callback ────────────────────────────────────────────────────

    private int OnText(MdTextType type, ReadOnlySpan<char> text)
    {
        // Code block accumulation.
        if (_codeAccum is not null)
        {
            _codeAccum.Append(text);
            return 0;
        }

        // HTML block accumulation.
        if (_htmlAccum is not null)
        {
            _htmlAccum.Append(text);
            return 0;
        }

        // Inside image — accumulate alt text.
        if (_insideImage)
        {
            _imageAlt?.Append(text);
            return 0;
        }

        switch (type)
        {
            case MdTextType.Normal:
            case MdTextType.Code:
                AddInlineRun(text.ToString());
                break;

            case MdTextType.SoftBr:
                _inlines.Add(new RichTextRun(" "));
                break;

            case MdTextType.Br:
                _inlines.Add(new RichTextLineBreak());
                break;

            case MdTextType.Entity:
                AddEntityText(text.ToString());
                break;

            case MdTextType.NullChar:
                _inlines.Add(new RichTextRun("\uFFFD"));
                break;

            case MdTextType.Html:
                // Inline HTML — pass through as text.
                _inlines.Add(new RichTextRun(text.ToString()));
                break;
        }

        return 0;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void AddInlineRun(string text)
    {
        _inlines.Add(new RichTextRun(text)
        {
            IsBold = _boldDepth > 0,
            IsItalic = _italicDepth > 0,
            IsStrikethrough = _isStrikethrough,
            FontFamily = _isCode ? _options.CodeFontFamily : null,
        });
    }

    private void AddEntityText(string entityStr)
    {
        var entity = Md4cEntity.EntityLookup(entityStr);
        if (entity is not null)
        {
            var sb = new StringBuilder();
            sb.Append(char.ConvertFromUtf32((int)entity.Value.Codepoint0));
            if (entity.Value.Codepoint1 != 0)
                sb.Append(char.ConvertFromUtf32((int)entity.Value.Codepoint1));
            AddInlineRun(sb.ToString());
        }
        else
        {
            // Unknown entity — pass through verbatim.
            AddInlineRun(entityStr);
        }
    }

    private static RichTextBlockElement MakeRichText(RichTextInline[] inlines, double? fontSize = null)
    {
        return new RichTextBlockElement("")
        {
            Paragraphs = [new RichTextParagraph(inlines)],
            FontSize = fontSize,
            IsTextSelectionEnabled = true,
        };
    }

    private void AddToParent(Element element)
    {
        if (_blockStack.Count > 0)
            _blockStack.Peek().Children.Add(element);
        else
            _result = element; // Top-level, shouldn't normally happen.
    }
}
