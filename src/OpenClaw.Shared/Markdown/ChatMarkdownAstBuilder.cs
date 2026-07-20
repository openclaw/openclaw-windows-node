using System;
using System.Collections.Generic;
using System.Text;
using OpenClaw.Shared.Markdown.Md4c;

namespace OpenClaw.Shared.Markdown;

/// <summary>
/// Translates the vendored md4c SAX callbacks into a
/// <see cref="ChatMarkdownDocument"/> AST suitable for chat-bubble rendering.
///
/// <para>This builder applies the OpenClaw chat security posture in-line:</para>
/// <list type="bullet">
///   <item>Links: the navigation <c>href</c> is dropped and the link's
///         display text is flattened via
///         <c>"display (href)"</c> so the URL stays visible/inspectable but
///         can never become a clickable <c>Hyperlink</c>.</item>
///   <item>Images: replaced with the inert literal <c>[Image: alt]</c>
///         (or <c>[Image]</c> when no alt is present). No URL is ever passed
///         downstream — guarantees no <c>BitmapImage</c> remote fetch.</item>
///   <item>Raw HTML is suppressed at the parser level via
///         <see cref="MarkdownParserFlags.NoHtml"/>; any residual
///         <see cref="MarkdownBlockType.Html"/> blocks collapse to inert
///         <see cref="MdRawTextBlock"/> nodes.</item>
///   <item>Input is hard-capped at <see cref="MaxInputBytes"/> characters
///         (~ 256 KB) — oversized payloads return a single inert
///         paragraph with the leading prefix preserved.</item>
/// </list>
///
/// <para>The builder is single-use (state is owned per call).
/// <see cref="Build(string)"/> is reentrant via fresh instantiation.</para>
/// </summary>
public sealed class ChatMarkdownAstBuilder
{
    /// <summary>
    /// Hard input cap. Roughly 256 KB of UTF-16 chars. Bigger inputs are
    /// truncated and rendered as a single inert paragraph noting the
    /// truncation so a runaway gateway/model can't pin the UI thread on a
    /// pathological parser call.
    /// </summary>
    public const int MaxInputBytes = 256 * 1024;

    /// <summary>
    /// Default parser flags for chat bubbles: GFM dialect (tables,
    /// strikethrough, task lists, permissive autolinks) plus
    /// <see cref="MarkdownParserFlags.NoHtml"/> (raw HTML disabled) and
    /// <see cref="MarkdownParserFlags.CollapseWhitespace"/> (consecutive
    /// whitespace collapsed in normal text — matches how chat clients
    /// typically render assistant prose).
    /// </summary>
    public const MarkdownParserFlags DefaultFlags =
        MarkdownParserFlags.DialectGitHub
        | MarkdownParserFlags.NoHtml
        | MarkdownParserFlags.CollapseWhitespace;

    private readonly MarkdownParserFlags _flags;

    // Block-frame stack — every EnterBlock pushes, every LeaveBlock pops and
    // either adds the resulting block to its parent frame, or (for inline
    // blocks like H/P) drains the inline accumulator into the block.
    private readonly Stack<BlockFrame> _stack = new();

    // Inline accumulator for the currently-open inline-bearing block
    // (paragraph, heading, list-item leaf paragraph, table cell, blockquote
    // paragraph, etc.).
    private readonly List<MdInline> _inlines = new();

    // Inline style state — toggled by Enter/LeaveSpan(Em/Strong/Del/U/Code).
    // Tracked as depth counters so nested same-style spans don't prematurely
    // turn the style off when one of them closes.
    private int _strongDepth;
    private int _emphasisDepth;
    private int _strikeDepth;
    private int _underlineDepth;
    private int _codeSpanDepth;

    // Link/image flattening state. When inside an A/Img span we collect the
    // text the parser would have rendered as the link/image display text,
    // and emit the flattened inert form on LeaveSpan. A stack is used so
    // nested links (e.g. an autolink inside an explicit link, or an image
    // inside a link) preserve the outer span's accumulated buffer.
    private readonly Stack<(StringBuilder Display, string? Href)> _linkStack = new();
    private int _linkDepth;
    private StringBuilder? _linkDisplay;
    private string? _pendingLinkHref;
    private readonly Stack<StringBuilder> _imageStack = new();
    private int _imageDepth;
    private StringBuilder? _imageAlt;

    // Code-block text accumulator.
    private StringBuilder? _codeBlockText;
    private string? _codeBlockLang;

    // Raw-HTML block accumulator (NoHtml flag normally prevents this but we
    // handle it defensively in case a future flag combination admits it).
    private StringBuilder? _rawHtmlText;

    // Table state — md4c emits Table > {Thead,Tbody} > Tr > {Th,Td}. We
    // collect header and body rows separately, then assemble the MdTable on
    // table close.
    private List<MdColumnAlignment>? _tableColumnAligns;
    private List<MdTableRow>? _tableHeaderRows;
    private List<MdTableRow>? _tableBodyRows;
    private List<MdTableCell>? _tableCurrentRow;
    private bool _tableInHead;
    private int _tableCellIndex;

    public ChatMarkdownAstBuilder(MarkdownParserFlags? flags = null)
    {
        _flags = flags ?? DefaultFlags;
    }

    /// <summary>
    /// Parse <paramref name="markdown"/> into a <see cref="ChatMarkdownDocument"/>.
    /// Never throws on malformed input — falls back to a single inert
    /// paragraph containing the verbatim source if the parser reports an
    /// error.
    /// </summary>
    public ChatMarkdownDocument Build(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return new ChatMarkdownDocument(Array.Empty<MdBlock>());

        if (markdown!.Length > MaxInputBytes)
        {
            var truncated = markdown.Substring(0, MaxInputBytes);
            return new ChatMarkdownDocument(new MdBlock[]
            {
                new MdParagraph(new MdInline[]
                {
                    new MdInlineText(
                        truncated + "\n\n[truncated - input exceeded "
                                  + MaxInputBytes.ToString("N0") + " chars]"),
                }),
            });
        }

        _stack.Clear();
        _inlines.Clear();
        _strongDepth = 0;
        _emphasisDepth = 0;
        _strikeDepth = 0;
        _underlineDepth = 0;
        _codeSpanDepth = 0;
        _linkStack.Clear();
        _linkDepth = 0;
        _linkDisplay = null;
        _pendingLinkHref = null;
        _imageStack.Clear();
        _imageDepth = 0;
        _imageAlt = null;
        _codeBlockText = null;
        _codeBlockLang = null;
        _rawHtmlText = null;
        _tableColumnAligns = null;
        _tableHeaderRows = null;
        _tableBodyRows = null;
        _tableCurrentRow = null;
        _tableInHead = false;
        _tableCellIndex = 0;
        _stack.Push(new BlockFrame(MarkdownBlockType.Doc));

        int result = Md4cParser.Parse(
            markdown, _flags,
            EnterBlock, LeaveBlock,
            EnterSpan, LeaveSpan,
            TextCallback);

        if (result != 0)
        {
            return new ChatMarkdownDocument(new MdBlock[]
            {
                new MdParagraph(new MdInline[] { new MdInlineText(markdown) }),
            });
        }

        var doc = _stack.Pop();
        return new ChatMarkdownDocument(doc.Children.ToArray());
    }

    // ────────────────────────────────────────────────────────────────────
    //  Block callbacks
    // ────────────────────────────────────────────────────────────────────

    private int EnterBlock(MarkdownBlockType type, object? detail)
    {
        // If we are about to open a new block while the current parent
        // is a tight-list item that has accumulated raw inline text,
        // synthesize an implicit paragraph for that pending text so it
        // does not bleed into the new block (e.g. nested Ul inside Li).
        FlushPendingTightInlines();

        switch (type)
        {
            case MarkdownBlockType.Doc:
                // Stack already seeded by Build().
                return 0;

            case MarkdownBlockType.Code:
                _codeBlockText = new StringBuilder();
                _codeBlockLang = detail is MarkdownBlockCodeDetail c ? c.Lang.Text : null;
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            case MarkdownBlockType.Html:
                _rawHtmlText = new StringBuilder();
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            case MarkdownBlockType.Table:
                if (detail is MarkdownBlockTableDetail td)
                {
                    _tableColumnAligns = new List<MdColumnAlignment>(td.ColCount);
                    for (int i = 0; i < td.ColCount; i++)
                        _tableColumnAligns.Add(MdColumnAlignment.Default);
                }
                else
                {
                    _tableColumnAligns = new List<MdColumnAlignment>();
                }
                _tableHeaderRows = new List<MdTableRow>();
                _tableBodyRows = new List<MdTableRow>();
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            case MarkdownBlockType.Thead:
                _tableInHead = true;
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            case MarkdownBlockType.Tbody:
                _tableInHead = false;
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            case MarkdownBlockType.Tr:
                _tableCurrentRow = new List<MdTableCell>();
                _tableCellIndex = 0;
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            case MarkdownBlockType.Th:
            case MarkdownBlockType.Td:
                // Cells gather inlines via _inlines; flush any leftover
                // inlines into the prior block first (paranoia).
                _inlines.Clear();
                _stack.Push(new BlockFrame(type, detail));
                return 0;

            default:
                _stack.Push(new BlockFrame(type, detail));
                return 0;
        }
    }

    private int LeaveBlock(MarkdownBlockType type, object? detail)
    {
        switch (type)
        {
            case MarkdownBlockType.Doc:
                // Keep the Doc frame for Build() to drain.
                return 0;

            case MarkdownBlockType.P:
                _stack.Pop();
                AddToParent(new MdParagraph(DrainInlines()));
                return 0;

            case MarkdownBlockType.H:
            {
                var frame = _stack.Pop();
                var hDetail = frame.Detail ?? detail;
                int level = hDetail is MarkdownBlockHDetail h ? h.Level : 1;
                AddToParent(new MdHeading(level, DrainInlines()));
                return 0;
            }

            case MarkdownBlockType.Hr:
                _stack.Pop();
                AddToParent(new MdThematicBreak());
                return 0;

            case MarkdownBlockType.Quote:
            {
                var frame = _stack.Pop();
                AddToParent(new MdBlockQuote(frame.Children.ToArray()));
                return 0;
            }

            case MarkdownBlockType.Ul:
            case MarkdownBlockType.Ol:
            {
                var frame = _stack.Pop();
                var items = new List<MdListItem>(frame.Children.Count);
                foreach (var child in frame.Children)
                {
                    if (child is MdListItem li) items.Add(li);
                }
                int start = 1;
                var marker = MdListMarker.Bullet;
                if (type == MarkdownBlockType.Ol)
                {
                    marker = MdListMarker.Ordered;
                    var olDetail = frame.Detail ?? detail;
                    if (olDetail is MarkdownBlockOlDetail ol) start = ol.Start;
                }
                AddToParent(new MdList(marker, start, items));
                return 0;
            }

            case MarkdownBlockType.Li:
            {
                var frame = _stack.Pop();
                // Tight-list case: md4c emits text events directly inside
                // Li without an enclosing P (only loose lists get explicit
                // P children). Drain any pending inlines into an implicit
                // paragraph so the bullet has visible content.
                if (_inlines.Count > 0)
                {
                    frame.Children.Add(new MdParagraph(DrainInlines()));
                }
                MdTaskState? task = null;
                if (frame.Detail is MarkdownBlockLiDetail liDetail && liDetail.IsTask)
                {
                    task = liDetail.TaskMark == 'x' || liDetail.TaskMark == 'X'
                        ? MdTaskState.Checked
                        : MdTaskState.Unchecked;
                }
                AddToParent(new MdListItem(frame.Children.ToArray(), task));
                return 0;
            }

            case MarkdownBlockType.Code:
            {
                _stack.Pop();
                var code = _codeBlockText?.ToString() ?? string.Empty;
                AddToParent(new MdCodeBlock(code, _codeBlockLang));
                _codeBlockText = null;
                _codeBlockLang = null;
                return 0;
            }

            case MarkdownBlockType.Html:
            {
                _stack.Pop();
                var text = _rawHtmlText?.ToString() ?? string.Empty;
                AddToParent(new MdRawTextBlock(text));
                _rawHtmlText = null;
                return 0;
            }

            case MarkdownBlockType.Table:
            {
                _stack.Pop();
                AddToParent(new MdTable(
                    _tableColumnAligns?.ToArray() ?? Array.Empty<MdColumnAlignment>(),
                    _tableHeaderRows?.ToArray() ?? Array.Empty<MdTableRow>(),
                    _tableBodyRows?.ToArray() ?? Array.Empty<MdTableRow>()));
                _tableColumnAligns = null;
                _tableHeaderRows = null;
                _tableBodyRows = null;
                _tableInHead = false;
                return 0;
            }

            case MarkdownBlockType.Thead:
            case MarkdownBlockType.Tbody:
                _stack.Pop();
                return 0;

            case MarkdownBlockType.Tr:
            {
                _stack.Pop();
                var row = new MdTableRow(_tableCurrentRow?.ToArray() ?? Array.Empty<MdTableCell>());
                if (_tableInHead) _tableHeaderRows!.Add(row);
                else _tableBodyRows!.Add(row);
                _tableCurrentRow = null;
                return 0;
            }

            case MarkdownBlockType.Th:
            case MarkdownBlockType.Td:
            {
                _stack.Pop();
                var cellInlines = DrainInlines();
                // Header cells carry the column alignment (md4c puts the
                // alignment on the Th detail rather than the column itself).
                if (type == MarkdownBlockType.Th
                    && detail is MarkdownBlockTdDetail th
                    && _tableColumnAligns is not null
                    && _tableCellIndex < _tableColumnAligns.Count)
                {
                    _tableColumnAligns[_tableCellIndex] = MapAlign(th.Align);
                }
                _tableCurrentRow?.Add(new MdTableCell(cellInlines));
                _tableCellIndex++;
                return 0;
            }

            default:
                _stack.Pop();
                DrainInlines();
                return 0;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Inline span callbacks
    // ────────────────────────────────────────────────────────────────────

    private int EnterSpan(MarkdownSpanType type, object? detail)
    {
        switch (type)
        {
            case MarkdownSpanType.Em:
                _emphasisDepth++;
                return 0;
            case MarkdownSpanType.Strong:
                _strongDepth++;
                return 0;
            case MarkdownSpanType.Code:
                _codeSpanDepth++;
                return 0;
            case MarkdownSpanType.Del:
                _strikeDepth++;
                return 0;
            case MarkdownSpanType.U:
                _underlineDepth++;
                return 0;
            case MarkdownSpanType.A:
            {
                _linkDepth++;
                var display = new StringBuilder();
                var href = detail is MarkdownSpanADetail a ? a.Href.Text : null;
                _linkStack.Push((display, href));
                _linkDisplay = display;
                _pendingLinkHref = href;
                return 0;
            }
            case MarkdownSpanType.Img:
            {
                _imageDepth++;
                var alt = new StringBuilder();
                _imageStack.Push(alt);
                _imageAlt = alt;
                return 0;
            }
            default:
                return 0;
        }
    }

    private int LeaveSpan(MarkdownSpanType type, object? detail)
    {
        switch (type)
        {
            case MarkdownSpanType.Em:
                if (_emphasisDepth > 0) _emphasisDepth--;
                return 0;
            case MarkdownSpanType.Strong:
                if (_strongDepth > 0) _strongDepth--;
                return 0;
            case MarkdownSpanType.Code:
                if (_codeSpanDepth > 0) _codeSpanDepth--;
                return 0;
            case MarkdownSpanType.Del:
                if (_strikeDepth > 0) _strikeDepth--;
                return 0;
            case MarkdownSpanType.U:
                if (_underlineDepth > 0) _underlineDepth--;
                return 0;
            case MarkdownSpanType.A:
            {
                if (_linkDepth > 0) _linkDepth--;
                string display = string.Empty;
                string? href = null;
                if (_linkStack.Count > 0)
                {
                    var popped = _linkStack.Pop();
                    display = popped.Display.ToString();
                    href = popped.Href;
                }
                // Reset current pointers to the new top of the link stack
                // (if any), so further text inside an enclosing link
                // accumulates into the outer link's display buffer.
                if (_linkStack.Count > 0)
                {
                    var top = _linkStack.Peek();
                    _linkDisplay = top.Display;
                    _pendingLinkHref = top.Href;
                }
                else
                {
                    _linkDisplay = null;
                    _pendingLinkHref = null;
                }
                // Flatten to inert text. If we're still inside an outer
                // link/image span after the pop, route the flattened text
                // into that buffer instead of the inline accumulator so the
                // outer span captures the full display.
                EmitFlattenedLinkText(display, href);
                return 0;
            }
            case MarkdownSpanType.Img:
            {
                if (_imageDepth > 0) _imageDepth--;
                string alt = string.Empty;
                if (_imageStack.Count > 0)
                {
                    alt = _imageStack.Pop().ToString();
                }
                _imageAlt = _imageStack.Count > 0 ? _imageStack.Peek() : null;
                var flattened = string.IsNullOrEmpty(alt) ? "[Image]" : "[Image: " + alt + "]";
                EmitInlineText(flattened);
                return 0;
            }
            default:
                return 0;
        }
    }

    private void EmitFlattenedLinkText(string display, string? href)
    {
        var text = string.IsNullOrEmpty(display)
            ? (href ?? string.Empty)
            : string.Equals(display, href, StringComparison.Ordinal)
                ? display
                : string.IsNullOrEmpty(href) ? display : display + " (" + href + ")";
        EmitInlineText(text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Text callback
    // ────────────────────────────────────────────────────────────────────

    private int TextCallback(MarkdownTextType type, ReadOnlySpan<char> text)
    {
        // Code blocks accumulate raw text directly (no inline styling).
        if (_codeBlockText is not null && _stack.Count > 0 && _stack.Peek().Type == MarkdownBlockType.Code)
        {
            _codeBlockText.Append(text);
            return 0;
        }

        if (_rawHtmlText is not null && _stack.Count > 0 && _stack.Peek().Type == MarkdownBlockType.Html)
        {
            _rawHtmlText.Append(text);
            return 0;
        }

        // Handle special text types up front so they're not lost when
        // captured inside a link/image span (where the raw `text` ReadOnly
        // span is typically empty for Br/SoftBr/NullChar).
        switch (type)
        {
            case MarkdownTextType.SoftBr:
            case MarkdownTextType.Br:
                if (_imageDepth > 0 && _imageAlt is not null)
                {
                    _imageAlt.Append(' ');
                }
                else if (_linkDepth > 0 && _linkDisplay is not null)
                {
                    _linkDisplay.Append(' ');
                }
                else
                {
                    _inlines.Add(new MdInlineLineBreak(IsHard: type == MarkdownTextType.Br));
                }
                return 0;
            case MarkdownTextType.NullChar:
                if (_imageDepth > 0 && _imageAlt is not null)
                {
                    _imageAlt.Append('\uFFFD');
                }
                else if (_linkDepth > 0 && _linkDisplay is not null)
                {
                    _linkDisplay.Append('\uFFFD');
                }
                else
                {
                    EmitInlineText("\uFFFD");
                }
                return 0;
            case MarkdownTextType.Entity:
            {
                // md4c hands us the raw entity reference (e.g. "&amp;",
                // "&#65;", "&#x41;"). Decode it to the actual Unicode
                // character(s) so the renderer doesn't display the
                // literal source token (e.g. "AT&amp;T" → "AT&T").
                string decoded = DecodeEntity(text);
                if (_imageDepth > 0 && _imageAlt is not null)
                {
                    _imageAlt.Append(decoded);
                }
                else if (_linkDepth > 0 && _linkDisplay is not null)
                {
                    _linkDisplay.Append(decoded);
                }
                else
                {
                    EmitInlineText(decoded);
                }
                return 0;
            }
        }

        // Inside an image span we capture into the alt buffer.
        if (_imageDepth > 0 && _imageAlt is not null)
        {
            _imageAlt.Append(text);
            return 0;
        }

        // Inside a link span we capture into the display buffer.
        if (_linkDepth > 0 && _linkDisplay is not null)
        {
            _linkDisplay.Append(text);
            return 0;
        }

        EmitInlineText(text.ToString());
        return 0;
    }

    private void EmitInlineText(string text)
    {
        if (text.Length == 0) return;
        // If a previous LeaveSpan(A/Img) emitted flattened text while still
        // inside an outer link/image span, route the flattened text into
        // that span's buffer so it isn't double-emitted to the inline list.
        if (_imageDepth > 0 && _imageAlt is not null)
        {
            _imageAlt.Append(text);
            return;
        }
        if (_linkDepth > 0 && _linkDisplay is not null)
        {
            _linkDisplay.Append(text);
            return;
        }
        _inlines.Add(new MdInlineText(
            text,
            IsStrong: _strongDepth > 0,
            IsEmphasis: _emphasisDepth > 0,
            IsCode: _codeSpanDepth > 0,
            IsStrike: _strikeDepth > 0,
            IsUnderline: _underlineDepth > 0));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private void AddToParent(MdBlock block)
    {
        if (_stack.Count == 0) return;
        _stack.Peek().Children.Add(block);
    }

    /// <summary>
    /// Decodes an HTML entity reference (e.g. <c>&amp;amp;</c>,
    /// <c>&amp;copy;</c>, <c>&amp;#65;</c>, <c>&amp;#x41;</c>) to its
    /// Unicode replacement string. Falls back to returning the raw
    /// token verbatim on unrecognized named entities so the source
    /// text is never silently lost. Per CommonMark, the NUL codepoint
    /// and out-of-range numeric entities resolve to U+FFFD.
    /// </summary>
    private static string DecodeEntity(ReadOnlySpan<char> token)
    {
        // Defensive: a well-formed entity is at minimum "&;".
        if (token.Length < 2 || token[0] != '&' || token[token.Length - 1] != ';')
        {
            return token.ToString();
        }

        // Numeric character reference: &#NNN; or &#xHH; / &#XHH;
        if (token.Length >= 4 && token[1] == '#')
        {
            ReadOnlySpan<char> digits;
            int radix;
            if (token[2] == 'x' || token[2] == 'X')
            {
                digits = token.Slice(3, token.Length - 4);
                radix = 16;
            }
            else
            {
                digits = token.Slice(2, token.Length - 3);
                radix = 10;
            }

            if (digits.Length == 0 || digits.Length > 8)
            {
                return token.ToString();
            }

            int codepoint = 0;
            foreach (char c in digits)
            {
                int digit;
                if (radix == 10)
                {
                    if (c < '0' || c > '9') return token.ToString();
                    digit = c - '0';
                }
                else
                {
                    if (c >= '0' && c <= '9') digit = c - '0';
                    else if (c >= 'a' && c <= 'f') digit = 10 + (c - 'a');
                    else if (c >= 'A' && c <= 'F') digit = 10 + (c - 'A');
                    else return token.ToString();
                }
                codepoint = (codepoint * radix) + digit;
                if (codepoint > 0x10FFFF)
                {
                    // Out of Unicode range — CommonMark says U+FFFD.
                    return "\uFFFD";
                }
            }

            // CommonMark: NUL → U+FFFD; surrogates / non-characters → U+FFFD.
            if (codepoint == 0
                || (codepoint >= 0xD800 && codepoint <= 0xDFFF))
            {
                return "\uFFFD";
            }
            return char.ConvertFromUtf32(codepoint);
        }

        // Named entity: look up the full "&name;" form in the md4c table.
        var entity = Md4cEntity.EntityLookup(token);
        if (entity is null)
        {
            // Unrecognized named entity — leave the token alone so the
            // user can see what was actually written.
            return token.ToString();
        }

        uint cp0 = entity.Value.Codepoint0;
        uint cp1 = entity.Value.Codepoint1;
        // The md4c entity table uses Codepoint0 == 0 as the "no replacement"
        // sentinel — fall back to the raw token rather than substituting U+FFFD.
        if (cp0 == 0) return token.ToString();
        if (cp1 == 0)
        {
            return SafeConvertFromUtf32(cp0);
        }
        // A handful of named entities (e.g. &nGt;) map to a 2-codepoint sequence.
        return SafeConvertFromUtf32(cp0) + SafeConvertFromUtf32(cp1);
    }

    /// <summary>
    /// Wraps <see cref="char.ConvertFromUtf32(int)"/> with a defensive check so a
    /// corrupted entity-table entry (value > U+10FFFF or in the surrogate range)
    /// cannot throw and crash the markdown pipeline.
    /// </summary>
    private static string SafeConvertFromUtf32(uint cp)
    {
        if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
        {
            return "\uFFFD";
        }
        return char.ConvertFromUtf32((int)cp);
    }

    private IReadOnlyList<MdInline> DrainInlines()
    {
        if (_inlines.Count == 0) return Array.Empty<MdInline>();
        var arr = _inlines.ToArray();
        _inlines.Clear();
        return arr;
    }

    /// <summary>
    /// md4c does NOT emit explicit <c>P</c> open/close events for the
    /// raw inline text inside tight list items (and may not for the
    /// "lazy" text inside a block quote either). If another block
    /// (e.g. a nested list) opens while such inline text is still
    /// pending in <see cref="_inlines"/>, that text would otherwise
    /// leak into the next block that drains. Flush it now as an
    /// implicit paragraph attached to the current parent.
    /// </summary>
    private void FlushPendingTightInlines()
    {
        if (_inlines.Count == 0 || _stack.Count == 0) return;
        var parent = _stack.Peek().Type;
        if (parent != MarkdownBlockType.Li && parent != MarkdownBlockType.Quote)
            return;
        AddToParent(new MdParagraph(DrainInlines()));
    }

    private static MdColumnAlignment MapAlign(MarkdownAlign align) => align switch
    {
        MarkdownAlign.Left => MdColumnAlignment.Left,
        MarkdownAlign.Center => MdColumnAlignment.Center,
        MarkdownAlign.Right => MdColumnAlignment.Right,
        _ => MdColumnAlignment.Default,
    };

    private sealed class BlockFrame
    {
        public MarkdownBlockType Type { get; }
        public object? Detail { get; }
        public List<MdBlock> Children { get; } = new();

        public BlockFrame(MarkdownBlockType type, object? detail = null)
        {
            Type = type;
            Detail = detail;
        }
    }
}
