using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Markdown;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using Windows.UI.Text;
using MUIText = Microsoft.UI.Text;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.Chat.Markdown;

/// <summary>
/// Maps a <see cref="ChatMarkdownDocument"/> (produced by
/// <see cref="ChatMarkdownAstBuilder"/>) into a <see cref="Element"/> tree
/// built from <c>OpenClawTray.FunctionalUI</c> factories so chat bubbles
/// can render block-level markdown (headings, lists, tables, blockquotes,
/// fenced code).
///
/// <para><b>Security posture.</b> Inert by construction:</para>
/// <list type="bullet">
///   <item>Links / images are already flattened to plain text by
///         <see cref="ChatMarkdownAstBuilder"/>. The renderer never sees an
///         href / image URL, so there is no path to <c>Hyperlink</c> or
///         <c>BitmapImage</c>.</item>
///   <item>Raw HTML is suppressed at the parser layer; if a residual
///         <see cref="MdRawTextBlock"/> arrives it is rendered as inert
///         monospaced text.</item>
///   <item>No <see cref="Element"/> produced here installs click / pointer
///         handlers, so untrusted markdown cannot manufacture navigation
///         or action effects.</item>
/// </list>
/// </summary>
public static class ChatMarkdownRenderer
{
    private static readonly double[] HeadingFontSizes =
        { 20.0, 18.0, 16.0, 15.0, 14.0, 14.0 };

    private const double BodyFontSize = 14.0;
    private const double CodeFontSize = 13.0;
    private const string MonoFont = "Cascadia Mono, Consolas, Courier New";

    public static Element? Render(ChatMarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Blocks.Count == 0)
            return null;

        // Coalesce maximal runs of consecutive mergeable blocks (paragraphs,
        // headings, and "simple" bullet/numbered lists) into a single
        // RichTextBlock so the whole run is one continuous text-selection scope
        // — a drag can select across paragraphs AND list items. Blocks that
        // carry their own chrome (code cards, table grids, block-quote borders,
        // rules) — and any list that contains such a block — stay as their own
        // selectable sibling controls, keeping their existing layout.
        var blocks = document.Blocks;
        var segments = new List<Element?>(blocks.Count);
        int i = 0;
        while (i < blocks.Count)
        {
            if (IsMergeable(blocks[i]))
            {
                int start = i;
                while (i < blocks.Count && IsMergeable(blocks[i])) i++;
                int count = i - start;
                if (count == 1 && blocks[start] is MdParagraph or MdHeading)
                {
                    // A lone paragraph / heading keeps the lightweight
                    // single-TextBlock shape (already internally selectable) —
                    // no behavior change.
                    var single = RenderBlock(blocks[start]);
                    if (single is not null) segments.Add(single);
                }
                else
                {
                    // Multi-block prose runs — and any run containing a list —
                    // become one continuous RichTextBlock so paragraphs,
                    // headings, and list items share a single selection scope.
                    var run = new List<MdBlock>(count);
                    for (int j = start; j < i; j++) run.Add(blocks[j]);
                    segments.Add(TextRunRichBlock(run));
                }
            }
            else
            {
                var rendered = RenderBlock(blocks[i]);
                if (rendered is not null) segments.Add(rendered);
                i++;
            }
        }

        if (segments.Count == 0) return null;
        if (segments.Count == 1) return segments[0];
        return VStack(8.0, segments.ToArray());
    }

    // Blocks that can flow into one continuous RichTextBlock: prose (paragraphs,
    // headings) and "simple" lists whose items contain only more mergeable
    // blocks. A list containing a code block, table, block quote, thematic
    // break, or raw block is NOT mergeable — it keeps its own layout as a
    // sibling island so that chrome (code cards, table grids) survives and the
    // list's non-text children stay selectable in their native form.
    private static bool IsMergeable(MdBlock block) => block switch
    {
        MdParagraph => true,
        MdHeading => true,
        MdList list => IsSimpleList(list),
        _ => false,
    };

    private static bool IsSimpleList(MdList list)
    {
        foreach (var item in list.Items)
            foreach (var child in item.Children)
                if (!IsMergeable(child)) return false;
        return true;
    }

    public static Element? Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return null;
        var doc = GetOrParse(markdown!);
        return Render(doc);
    }

    // Bounded AST cache so the per-tick FunctionalUI re-render of a chat
    // bubble doesn't reparse the same markdown source. We cache the parsed
    // AST (cheap to retain, pure-data records) and always rebuild a fresh
    // Element tree from it - WinUI elements can only live in one visual
    // parent slot, so re-using rendered Elements across mounts is unsafe.
    private const int CacheCapacity = 64;
    private static readonly object s_cacheLock = new();
    private static readonly LinkedList<string> s_cacheOrder = new();
    private static readonly Dictionary<string, (LinkedListNode<string> Node, ChatMarkdownDocument Doc)>
        s_cache = new(StringComparer.Ordinal);

    private static ChatMarkdownDocument GetOrParse(string markdown)
    {
        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(markdown, out var hit))
            {
                s_cacheOrder.Remove(hit.Node);
                s_cacheOrder.AddFirst(hit.Node);
                return hit.Doc;
            }
        }
        var doc = new ChatMarkdownAstBuilder().Build(markdown);
        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(markdown, out var existing))
            {
                s_cacheOrder.Remove(existing.Node);
                s_cacheOrder.AddFirst(existing.Node);
                return existing.Doc;
            }
            var node = new LinkedListNode<string>(markdown);
            s_cacheOrder.AddFirst(node);
            s_cache[markdown] = (node, doc);
            while (s_cache.Count > CacheCapacity)
            {
                var oldest = s_cacheOrder.Last!;
                s_cacheOrder.RemoveLast();
                s_cache.Remove(oldest.Value);
            }
        }
        return doc;
    }

    /// <summary>
    /// Cheap O(n) screen for block-level markdown signals. Lets callers
    /// skip the parser for plain-prose bubbles (the common case) and keep
    /// the legacy inline-only fast path.
    /// </summary>
    public static bool ContainsBlockMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        bool atLineStart = true;
        bool sawNewline = false;
        for (int i = 0; i < text!.Length; i++)
        {
            char c = text[i];
            if (c == '\n') sawNewline = true;
            if (atLineStart)
            {
                // Skip up to 3 leading spaces (CommonMark allows that on
                // fences, HRs, headings, lists, etc.).
                int s = i;
                int spaces = 0;
                while (spaces < 3 && s < text.Length && text[s] == ' ')
                {
                    spaces++;
                    s++;
                }
                if (s < text.Length)
                {
                    char sc = text[s];
                    switch (sc)
                    {
                        case '#':
                            // ATX heading requires '#' (1-6) followed by space/tab or EOL.
                            int hashCount = 0;
                            int hi = s;
                            while (hi < text.Length && text[hi] == '#' && hashCount < 7)
                            {
                                hashCount++;
                                hi++;
                            }
                            if (hashCount >= 1 && hashCount <= 6
                                && (hi >= text.Length || text[hi] == ' ' || text[hi] == '\t' || text[hi] == '\n'))
                                return true;
                            break;
                        case '>':
                        case '|':
                            return true;
                        case '-':
                        case '*':
                        case '+':
                            // List item: marker followed by space/tab.
                            if (s + 1 < text.Length && (text[s + 1] == ' ' || text[s + 1] == '\t'))
                                return true;
                            // Thematic break: 3+ of '-', '*', or '_' on a
                            // line (optionally separated by spaces/tabs).
                            if (sc == '-' || sc == '*')
                            {
                                if (IsThematicBreakLine(text, s, sc)) return true;
                            }
                            break;
                        case '_':
                            if (IsThematicBreakLine(text, s, '_')) return true;
                            break;
                        case '`':
                            if (s + 2 < text.Length && text[s + 1] == '`' && text[s + 2] == '`')
                                return true;
                            break;
                        case '~':
                            if (s + 2 < text.Length && text[s + 1] == '~' && text[s + 2] == '~')
                                return true;
                            break;
                    }
                    if (sc >= '0' && sc <= '9')
                    {
                        int j = s + 1;
                        while (j < text.Length && text[j] >= '0' && text[j] <= '9') j++;
                        if (j < text.Length && (text[j] == '.' || text[j] == ')')
                            && j + 1 < text.Length && (text[j + 1] == ' ' || text[j + 1] == '\t'))
                            return true;
                    }
                }
            }
            atLineStart = c == '\n';
        }
        // Mid-line pipe table cue: any multi-line input containing a '|'.
        if (sawNewline && text.IndexOf('|') >= 0) return true;
        return false;
    }

    private static bool IsThematicBreakLine(string text, int start, char marker)
    {
        int count = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == marker) count++;
            else if (c == ' ' || c == '\t') { /* allowed between markers */ }
            else if (c == '\n' || c == '\r') break;
            else return false;
        }
        return count >= 3;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Block rendering
    // ────────────────────────────────────────────────────────────────────

    private static Element? RenderBlock(MdBlock block) => block switch
    {
        MdHeading h          => RenderHeading(h),
        MdParagraph p        => RenderParagraph(p),
        MdBlockQuote q       => RenderBlockQuote(q),
        MdThematicBreak _    => RenderHr(),
        MdCodeBlock c        => RenderCodeBlock(c),
        MdRawTextBlock r     => RenderRaw(r),
        MdList l             => RenderList(l),
        MdListItem li        => RenderListItemContent(li),
        MdTable t            => RenderTable(t),
        _                    => null,
    };

    private static Element RenderHeading(MdHeading heading)
    {
        int idx = Math.Clamp(heading.Level - 1, 0, HeadingFontSizes.Length - 1);
        return InlinesTextBlock(heading.Inlines)
            .FontSize(HeadingFontSizes[idx])
            .FontWeight(MUIText.FontWeights.SemiBold);
    }

    private static Element RenderParagraph(MdParagraph paragraph) =>
        InlinesTextBlock(paragraph.Inlines).FontSize(BodyFontSize);

    // ────────────────────────────────────────────────────────────────────
    //  Coalesced text run → single selectable RichTextBlock
    // ────────────────────────────────────────────────────────────────────

    private const double ParagraphSpacing = 8.0;

    // Cache the block run applied to each RichTextBlock so re-renders with
    // identical content skip the Blocks.Clear()+rebuild round trip. Clearing
    // Blocks while the user has an active selection wipes it (same failure mode
    // the TextBlock inline cache guards against). Equality is structural
    // (see BlockRunsEqual) so it holds even after the bounded AST cache evicts
    // and re-parses an unchanged message into fresh block instances.
    private static readonly ConditionalWeakTable<RichTextBlock, IReadOnlyList<MdBlock>>
        s_blocksCache = new();

    private static RichTextBlockElement TextRunRichBlock(IReadOnlyList<MdBlock> blocks) =>
        RichTextBlock().Set(rtb =>
        {
            rtb.TextWrapping = TextWrapping.Wrap;
            rtb.IsTextSelectionEnabled = true;
            ApplyBlocks(rtb, blocks);
        });

    private static void ApplyBlocks(RichTextBlock rtb, IReadOnlyList<MdBlock> blocks)
    {
        if (rtb.Blocks.Count > 0
            && s_blocksCache.TryGetValue(rtb, out var cached)
            && BlockRunsEqual(cached, blocks))
        {
            return;
        }
        s_blocksCache.AddOrUpdate(rtb, blocks);
        rtb.Blocks.Clear();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdParagraph:
                case MdHeading:
                {
                    var p = BuildParagraph(block);
                    p.Margin = new Thickness(0, TopGap(rtb, ParagraphSpacing), 0, 0);
                    rtb.Blocks.Add(p);
                    break;
                }
                case MdList list:
                    AppendList(rtb, list, level: 1);
                    break;
            }
        }
    }

    // Vertical gap before the next paragraph: none for the very first paragraph
    // in the RichTextBlock, otherwise the supplied gap. Keeping this off the
    // first paragraph avoids a leading blank strip at the top of the bubble.
    private static double TopGap(RichTextBlock rtb, double gap) =>
        rtb.Blocks.Count == 0 ? 0.0 : gap;

    // ────────────────────────────────────────────────────────────────────
    //  Lists coalesced into the shared RichTextBlock (hanging-indent
    //  paragraphs) — one continuous selection scope with the surrounding
    //  prose. Only reached for "simple" lists (see IsSimpleList); lists that
    //  carry chrome-bearing children render via RenderList as their own island.
    // ────────────────────────────────────────────────────────────────────

    private const double ListIndentPerLevel = 18.0;
    private const double ListItemSpacing = 3.0;
    // Fixed offset from a list item's marker column to its text/content column.
    // Applied as a hanging indent (negative TextIndent on the marker line) so
    // wrapped lines and continuation paragraphs align under the text rather than
    // back at the marker — approximating the Grid path's Auto/Star columns with
    // the flat Paragraph model (which has no per-item measured marker width).
    private const double ListHangingIndent = 18.0;
    // Marker → text separator. En space gives a stable, font-independent gap
    // after the bullet / number without relying on tab stops (which Paragraph
    // does not expose).
    private const string ListMarkerGap = "\u2002";

    private static void AppendList(RichTextBlock rtb, MdList list, int level)
    {
        bool ordered = list.Marker == MdListMarker.Ordered;
        int number = ordered ? list.StartNumber : 0;
        for (int idx = 0; idx < list.Items.Count; idx++)
        {
            var item = list.Items[idx];
            string marker = item.TaskState switch
            {
                MdTaskState.Checked   => "\u2611",
                MdTaskState.Unchecked => "\u2610",
                _ => ordered ? $"{number}." : "\u2022",
            };
            if (ordered) number++;
            // A top-level list's first item gets the fuller paragraph gap so the
            // list separates from preceding prose; every other item uses the
            // tighter inter-item gap.
            double leadGap = (level == 1 && idx == 0) ? ParagraphSpacing : ListItemSpacing;
            AppendListItem(rtb, item, marker, level, leadGap);
        }
    }

    private static void AppendListItem(
        RichTextBlock rtb, MdListItem item, string marker, int level, double leadGap)
    {
        double indent = ListIndentPerLevel * level;
        bool markerEmitted = false;
        foreach (var child in item.Children)
        {
            double gap = markerEmitted ? ListItemSpacing : leadGap;
            switch (child)
            {
                case MdParagraph p:
                    rtb.Blocks.Add(BuildListParagraph(
                        rtb, p.Inlines, indent, markerEmitted ? null : marker, gap));
                    markerEmitted = true;
                    break;
                case MdHeading h:
                {
                    int hidx = Math.Clamp(h.Level - 1, 0, HeadingFontSizes.Length - 1);
                    rtb.Blocks.Add(BuildListParagraph(
                        rtb, h.Inlines, indent, markerEmitted ? null : marker, gap,
                        HeadingFontSizes[hidx], semiBold: true));
                    markerEmitted = true;
                    break;
                }
                case MdList nested:
                    if (!markerEmitted)
                    {
                        rtb.Blocks.Add(BuildListParagraph(
                            rtb, Array.Empty<MdInline>(), indent, marker, leadGap));
                        markerEmitted = true;
                    }
                    AppendList(rtb, nested, level + 1);
                    break;
            }
        }
        if (!markerEmitted)
        {
            // Empty item — still emit the marker so numbering stays consistent.
            rtb.Blocks.Add(BuildListParagraph(
                rtb, Array.Empty<MdInline>(), indent, marker, leadGap));
        }
    }

    private static Paragraph BuildListParagraph(
        RichTextBlock rtb, IReadOnlyList<MdInline> inlines, double indent,
        string? marker, double gap, double fontSize = BodyFontSize, bool semiBold = false)
    {
        var p = new Paragraph
        {
            FontSize = fontSize,
            // Content column sits a fixed hanging offset past the marker indent
            // so wrapped lines / continuation paragraphs align under the text.
            Margin = new Thickness(indent + ListHangingIndent, TopGap(rtb, gap), 0, 0),
        };
        if (semiBold) p.FontWeight = MUIText.FontWeights.SemiBold;
        if (marker is not null)
        {
            // Hanging indent: pull the marker line back to the marker column
            // while wrapped lines stay at the content column set by Margin.
            p.TextIndent = -ListHangingIndent;
            p.Inlines.Add(new Run { Text = marker + ListMarkerGap });
        }
        AppendInlines(p.Inlines, inlines);
        return p;
    }

    // Compare coalesced text runs structurally rather than via record equality.
    // MdParagraph / MdHeading records compare their MdInline lists by reference,
    // so two equivalent-but-distinct block instances (produced when the bounded
    // GetOrParse AST cache evicts and re-parses an unchanged message) would
    // compare unequal and needlessly rebuild Blocks — wiping the active
    // selection. MdInline subtypes are value-comparable, so comparing the inline
    // sequences by value keeps the selection-preservation guarantee independent
    // of parser-cache residency.
    private static bool BlockRunsEqual(IReadOnlyList<MdBlock> a, IReadOnlyList<MdBlock> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!BlockEqual(a[i], b[i])) return false;
        }
        return true;
    }

    private static bool BlockEqual(MdBlock a, MdBlock b) => (a, b) switch
    {
        (MdHeading ha, MdHeading hb) => ha.Level == hb.Level && ha.Inlines.SequenceEqual(hb.Inlines),
        (MdParagraph pa, MdParagraph pb) => pa.Inlines.SequenceEqual(pb.Inlines),
        (MdList la, MdList lb) => ListEqual(la, lb),
        _ => a.Equals(b),
    };

    // Structural list comparison mirroring BlockEqual's rationale: MdList /
    // MdListItem records compare their child collections BY REFERENCE, so a
    // re-parsed-but-identical list would otherwise force a Blocks rebuild and
    // wipe the active selection. Recurse through items and their block children
    // (nested lists included) comparing inline content by value.
    private static bool ListEqual(MdList a, MdList b)
    {
        if (a.Marker != b.Marker || a.StartNumber != b.StartNumber) return false;
        if (a.Items.Count != b.Items.Count) return false;
        for (int i = 0; i < a.Items.Count; i++)
        {
            var ia = a.Items[i];
            var ib = b.Items[i];
            if (ia.TaskState != ib.TaskState) return false;
            if (ia.Children.Count != ib.Children.Count) return false;
            for (int j = 0; j < ia.Children.Count; j++)
                if (!BlockEqual(ia.Children[j], ib.Children[j])) return false;
        }
        return true;
    }

    private static Paragraph BuildParagraph(MdBlock block)
    {
        switch (block)
        {
            case MdHeading heading:
            {
                int idx = Math.Clamp(heading.Level - 1, 0, HeadingFontSizes.Length - 1);
                var p = new Paragraph
                {
                    FontSize = HeadingFontSizes[idx],
                    FontWeight = MUIText.FontWeights.SemiBold,
                };
                AppendInlines(p.Inlines, heading.Inlines);
                return p;
            }
            case MdParagraph paragraph:
            {
                var p = new Paragraph { FontSize = BodyFontSize };
                AppendInlines(p.Inlines, paragraph.Inlines);
                return p;
            }
            default:
                // Only paragraphs / headings reach this method — lists flow
                // through AppendList, other blocks render as sibling islands.
                return new Paragraph { FontSize = BodyFontSize };
        }
    }

    private static Element RenderBlockQuote(MdBlockQuote quote)
    {
        var children = new List<Element?>(quote.Children.Count);
        foreach (var child in quote.Children)
        {
            var rendered = RenderBlock(child);
            if (rendered is not null) children.Add(rendered);
        }
        Element inner = children.Count == 1
            ? children[0]!
            : VStack(6.0, children.ToArray());
        return Border(inner)
            .WithBorder(Theme.SecondaryText, 1)
            .Padding(10, 4, 8, 4);
    }

    private static Element RenderHr() =>
        Border()
            .Height(1)
            .Background(Theme.DividerStroke)
            .Margin(0, 6, 0, 6);

    private static Element RenderCodeBlock(MdCodeBlock code) =>
        Border(
            TextBlock(code.Code.TrimEnd('\n'))
                .FontFamily(MonoFont)
                .FontSize(CodeFontSize)
                .Set(t => { t.TextWrapping = TextWrapping.NoWrap; t.IsTextSelectionEnabled = true; })
        )
        .Background(Theme.CardBackground)
        .CornerRadius(6)
        .Padding(10, 8, 10, 8);

    private static Element RenderRaw(MdRawTextBlock raw) =>
        TextBlock(raw.Text)
            .FontFamily(MonoFont)
            .FontSize(CodeFontSize)
            .Set(t => { t.TextWrapping = TextWrapping.Wrap; t.IsTextSelectionEnabled = true; });

    // ────────────────────────────────────────────────────────────────────
    //  Lists
    // ────────────────────────────────────────────────────────────────────

    private const double BulletMarkerMinWidth  = 10.0;
    private const double OrderedMarkerMinWidth = 18.0;

    private static Element RenderList(MdList list)
    {
        // Allocated per list (not per row) and not shared across calls, so the
        // arrays can't be mutated by anyone else between renders.
        var columns = new[] { GridSize.Auto, GridSize.Star() };
        var rowDefs = new[] { GridSize.Auto };

        var rows = new List<Element?>(list.Items.Count);
        int number = list.Marker == MdListMarker.Ordered ? list.StartNumber : 0;
        bool isOrdered = list.Marker == MdListMarker.Ordered;
        double markerMinWidth = isOrdered ? OrderedMarkerMinWidth : BulletMarkerMinWidth;
        foreach (var item in list.Items)
        {
            string marker = item.TaskState switch
            {
                MdTaskState.Checked   => "\u2611",
                MdTaskState.Unchecked => "\u2610",
                _ => isOrdered ? $"{number}." : "\u2022",
            };
            if (isOrdered) number++;

            // A Grid is used here (rather than a horizontal StackPanel) so the
            // content column receives a finite width during measure and the
            // paragraph TextBlock can wrap. Inside a horizontal StackPanel the
            // content would be measured with infinite width and long bullets
            // would render on a single clipped line. See issue #636.
            rows.Add(Grid(
                columns,
                rowDefs,
                TextBlock(marker)
                    .FontSize(BodyFontSize)
                    .VAlign(VerticalAlignment.Top)
                    .MinWidth(markerMinWidth)
                    .Grid(row: 0, column: 0),
                RenderListItemContent(item).Grid(row: 0, column: 1)));
        }
        return VStack(4.0, rows.ToArray());
    }

    private static Element RenderListItemContent(MdListItem item)
    {
        if (item.Children.Count == 0)
            return TextBlock(string.Empty);
        if (item.Children.Count == 1)
            return RenderBlock(item.Children[0]) ?? TextBlock(string.Empty);
        var children = new List<Element?>(item.Children.Count);
        foreach (var child in item.Children)
        {
            var rendered = RenderBlock(child);
            if (rendered is not null) children.Add(rendered);
        }
        return VStack(4.0, children.ToArray());
    }

    // ────────────────────────────────────────────────────────────────────
    //  Tables
    // ────────────────────────────────────────────────────────────────────

    private const string TableGridColor = "#40808080";

    // Shared brush - avoids allocating one DependencyObject per cell on the UI thread.
    // SolidColorBrush is freezable/lightweight, but we still construct it lazily on first
    // table render so type initialization doesn't run in headless test contexts that
    // never touch a renderer.
    private static SolidColorBrush? s_tableGridBrush;
    private static SolidColorBrush TableGridBrush =>
        s_tableGridBrush ??= new SolidColorBrush(ParseHex(TableGridColor));

    private static Element RenderTable(MdTable table)
    {
        int colCount = Math.Max(
            table.ColumnAlignments.Count,
            MaxCells(table.HeaderRows, table.BodyRows));
        if (colCount == 0)
            return TextBlock(string.Empty);

        int rowCount = table.HeaderRows.Count + table.BodyRows.Count;
        if (rowCount == 0)
            return TextBlock(string.Empty);

        var cols = new GridSize[colCount];
        for (int c = 0; c < colCount; c++) cols[c] = GridSize.Star();
        var rows = new GridSize[rowCount];
        for (int r = 0; r < rowCount; r++) rows[r] = GridSize.Auto;

        var cells = new List<Element?>(rowCount * colCount);
        int rowIndex = 0;
        foreach (var hr in table.HeaderRows)
        {
            for (int c = 0; c < colCount; c++)
            {
                cells.Add(RenderTableCell(hr, c, rowIndex, table, isHeader: true)
                    .Grid(row: rowIndex, column: c));
            }
            rowIndex++;
        }
        foreach (var br in table.BodyRows)
        {
            for (int c = 0; c < colCount; c++)
            {
                cells.Add(RenderTableCell(br, c, rowIndex, table, isHeader: false)
                    .Grid(row: rowIndex, column: c));
            }
            rowIndex++;
        }
        // Outer border closes the right/bottom edges of the last column/row,
        // matching the per-cell top/left strokes for a uniform 1px grid.
        return Border(Grid(cols, rows, cells.ToArray()))
            .WithBorder(TableGridBrush, thickness: 1);
    }

    private static Element RenderTableCell(
        MdTableRow row,
        int colIndex,
        int rowIndex,
        MdTable table,
        bool isHeader)
    {
        var inlines = colIndex < row.Cells.Count
            ? row.Cells[colIndex].Inlines
            : Array.Empty<MdInline>();
        var tb = InlinesTextBlock(inlines).FontSize(BodyFontSize).Padding(8, 4, 8, 4);
        if (isHeader) tb = tb.FontWeight(MUIText.FontWeights.SemiBold);
        var align = colIndex < table.ColumnAlignments.Count
            ? table.ColumnAlignments[colIndex]
            : MdColumnAlignment.Default;
        tb = align switch
        {
            MdColumnAlignment.Right  => tb.HAlign(HorizontalAlignment.Right),
            MdColumnAlignment.Center => tb.HAlign(HorizontalAlignment.Center),
            _                        => tb,
        };
        // Per-cell top/left strokes only - the table-level Border closes
        // the bottom/right edges. This produces a uniform 1px grid with
        // no double-thickness interior lines.
        double leftThickness = colIndex == 0 ? 0 : 1;
        double topThickness  = rowIndex == 0 ? 0 : 1;
        var cell = Border(tb).Set(b =>
        {
            b.BorderBrush = TableGridBrush;
            b.BorderThickness = new Thickness(leftThickness, topThickness, 0, 0);
        });
        if (isHeader)
            cell = cell.Background(Theme.CardBackground);
        return cell;
    }

    /// <summary>
    /// Parses a hex color in the form <c>#AARRGGBB</c> or <c>#RRGGBB</c>.
    /// Local copy to avoid depending on the internal helper in
    /// <c>OpenClawTray.FunctionalUI</c>.
    /// </summary>
    private static global::Windows.UI.Color ParseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            return Microsoft.UI.Colors.Transparent;
        string s = hex.Substring(1);
        byte a = 0xFF, r, g, b;
        if (s.Length == 8)
        {
            a = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
        }
        else if (s.Length == 6)
        {
            r = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            g = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            b = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        }
        else
        {
            return Microsoft.UI.Colors.Transparent;
        }
        return global::Windows.UI.Color.FromArgb(a, r, g, b);
    }

    private static int MaxCells(IReadOnlyList<MdTableRow> a, IReadOnlyList<MdTableRow> b)
    {
        int max = 0;
        foreach (var r in a) if (r.Cells.Count > max) max = r.Cells.Count;
        foreach (var r in b) if (r.Cells.Count > max) max = r.Cells.Count;
        return max;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Inline → TextBlock.Inlines
    // ────────────────────────────────────────────────────────────────────

    // Cache the inline list applied to each TextBlock so that re-renders
    // with identical content can skip the Clear()+AppendInlines() round
    // trip. FunctionalUI pools TextBlocks by tree path and re-runs every
    // setter on each render (see FunctionalUI.ConfigureTextBlock); clearing
    // the Inlines collection while the user has an active text selection
    // wipes that selection (bug: selection disappears when the pointer
    // leaves the bubble, because hover-state changes trigger a full
    // subtree re-render). MdInline subtypes are records with value
    // equality, so SequenceEqual is a sound structural comparison.
    private static readonly ConditionalWeakTable<TextBlock, IReadOnlyList<MdInline>>
        s_inlinesCache = new();

    private static TextBlockElement InlinesTextBlock(IReadOnlyList<MdInline> inlines) =>
        TextBlock(string.Empty).Set(tb =>
        {
            tb.TextWrapping = TextWrapping.Wrap;
            tb.IsTextSelectionEnabled = true;
            ApplyInlines(tb, inlines);
        });

    private static void ApplyInlines(TextBlock tb, IReadOnlyList<MdInline> inlines)
    {
        // Only honour the cache when the previously-built inlines are still
        // present. ConfigureTextBlock may set Text="" before this setter
        // runs, which clears the Inlines collection out from under us; in
        // that case we must rebuild even if the source list is unchanged.
        if (tb.Inlines.Count > 0
            && s_inlinesCache.TryGetValue(tb, out var cached)
            && (ReferenceEquals(cached, inlines) || cached.SequenceEqual(inlines)))
        {
            return;
        }
        s_inlinesCache.AddOrUpdate(tb, inlines);
        tb.Inlines.Clear();
        AppendInlines(tb.Inlines, inlines);
    }

    private static void AppendInlines(InlineCollection sink, IReadOnlyList<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdInlineLineBreak br:
                    // CommonMark: a soft break is rendered as whitespace
                    // (visual line wrap, but no forced break in the bubble).
                    // A hard break (trailing two spaces or backslash) maps
                    // to a real LineBreak so authored breaks survive.
                    if (br.IsHard)
                        sink.Add(new LineBreak());
                    else
                        sink.Add(new Run { Text = " " });
                    break;
                case MdInlineText text:
                    sink.Add(BuildRun(text));
                    break;
            }
        }
    }

    private static Inline BuildRun(MdInlineText text)
    {
        var run = new Run { Text = text.Text };
        if (text.IsCode)
        {
            run.FontFamily = new FontFamily(MonoFont);
            run.FontSize = CodeFontSize;
        }
        if (text.IsStrike) run.TextDecorations |= TextDecorations.Strikethrough;
        if (text.IsUnderline) run.TextDecorations |= TextDecorations.Underline;

        Inline current = run;
        if (text.IsEmphasis)
        {
            var italic = new Italic();
            italic.Inlines.Add(current);
            current = italic;
        }
        if (text.IsStrong)
        {
            var bold = new Bold();
            bold.Inlines.Add(current);
            current = bold;
        }
        return current;
    }
}
