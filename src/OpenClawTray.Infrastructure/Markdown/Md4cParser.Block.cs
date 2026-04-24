// C# port of Martin Mitáš's md4c Markdown parser block processing.
// Ported from md4c/src/md4c.c
//
// AI-HINT: Block-level parsing: identifies block structure (headings, lists, code fences,
// block quotes, tables, HTML blocks) by analyzing line prefixes and indentation.
// Key method: ProcessLine() classifies each line. AnalyzeLine() handles nesting.
// Block types are tracked in a container stack. CommonMark spec §1-§5.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Infrastructure.Markdown;

internal sealed partial class Md4cParser
{
    // ── Block-level constants ────────────────────────────────────────────

    private const string IndentChunkStr = "                "; // 16 spaces
    private const int IndentChunkSize = 16;

    // ── HTML block type 1 tags ───────────────────────────────────────────

    private static readonly string[] HtmlType1Tags = { "pre", "script", "style", "textarea" };

    // ── HTML block type 6 tag table (indexed by first letter a-z) ────────

    private static readonly string[][] HtmlType6Map = BuildType6Map();

    private static string[][] BuildType6Map()
    {
        var empty = Array.Empty<string>();
        var map = new string[26][];
        map[0]  = new[] { "address", "article", "aside" };
        map[1]  = new[] { "base", "basefont", "blockquote", "body" };
        map[2]  = new[] { "caption", "center", "col", "colgroup" };
        map[3]  = new[] { "dd", "details", "dialog", "dir", "div", "dl", "dt" };
        map[4]  = empty;
        map[5]  = new[] { "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset" };
        map[6]  = empty;
        map[7]  = new[] { "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html" };
        map[8]  = new[] { "iframe" };
        map[9]  = empty;
        map[10] = empty;
        map[11] = new[] { "legend", "li", "link" };
        map[12] = new[] { "main", "menu", "menuitem" };
        map[13] = new[] { "nav", "noframes" };
        map[14] = new[] { "ol", "optgroup", "option" };
        map[15] = new[] { "p", "param" };
        map[16] = empty;
        map[17] = empty;
        map[18] = new[] { "search", "section", "summary" };
        map[19] = new[] { "table", "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track" };
        map[20] = new[] { "ul" };
        map[21] = empty;
        map[22] = empty;
        map[23] = empty;
        map[24] = empty;
        map[25] = empty;
        return map;
    }

    // ── Helper: IsAnyOf ──────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAnyOf(char ch, string palette)
    {
        if (ch == '\0') return false;
        return palette.IndexOf(ch) >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAnyOf2(char ch, char c1, char c2) => ch == c1 || ch == c2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAnyOf3(char ch, char c1, char c2, char c3) => ch == c1 || ch == c2 || ch == c3;

    // ── Normal block processing ──────────────────────────────────────────

    private int ProcessNormalBlockContents(Line[] lines, int nLines, int lineOffset)
    {
        int ret;

        ret = AnalyzeInlines(lines, nLines, lineOffset, false);
        if (ret < 0) goto cleanup;
        ret = ProcessInlines(lines, nLines, lineOffset);

    cleanup:
        // Free any temporary memory blocks stored within some dummy marks.
        for (int i = ptrStack.Top; i >= 0; i = marks[i].Next)
            markPtrStore.Remove(i);
        ptrStack.Top = -1;

        return ret;
    }

    // ── Verbatim block processing ────────────────────────────────────────

    private int ProcessVerbatimBlockContents(MdTextType textType, VerbatimLine[] lines, int nLines, int lineOffset)
    {
        int ret = 0;

        for (int lineIndex = 0; lineIndex < nLines; lineIndex++)
        {
            ref VerbatimLine line = ref lines[lineOffset + lineIndex];
            int indent = line.Indent;

            Debug.Assert(indent >= 0);

            // Output code indentation.
            while (indent > IndentChunkSize)
            {
                ret = TextBuf(textType, IndentChunkStr.AsSpan(0, IndentChunkSize));
                if (ret != 0) return ret;
                indent -= IndentChunkSize;
            }
            if (indent > 0)
            {
                ret = TextBuf(textType, IndentChunkStr.AsSpan(0, indent));
                if (ret != 0) return ret;
            }

            // Output the code line itself.
            ret = TextInsecure(textType, line.Beg, line.End - line.Beg);
            if (ret != 0) return ret;

            // Enforce end-of-line.
            ret = TextBuf(textType, "\n".AsSpan());
            if (ret != 0) return ret;
        }

        return ret;
    }

    // ── Code block processing ────────────────────────────────────────────

    private int ProcessCodeBlockContents(bool isFenced, VerbatimLine[] lines, int nLines, int lineOffset)
    {
        if (isFenced)
        {
            // Skip the first line in case of fenced code: It is the fence.
            lineOffset++;
            nLines--;
        }
        else
        {
            // Ignore blank lines at start/end of indented code block.
            while (nLines > 0 && lines[lineOffset].Beg == lines[lineOffset].End)
            {
                lineOffset++;
                nLines--;
            }
            while (nLines > 0 && lines[lineOffset + nLines - 1].Beg == lines[lineOffset + nLines - 1].End)
            {
                nLines--;
            }
        }

        if (nLines == 0)
            return 0;

        return ProcessVerbatimBlockContents(MdTextType.Code, lines, nLines, lineOffset);
    }

    // ── Fenced code detail setup ─────────────────────────────────────────

    private int SetupFencedCodeDetail(int blockIndex, out MdBlockCodeDetail det)
    {
        det = default;

        // The first verbatim line is the fence line.
        int firstVerbLineIndex = GetBlockVerbatimLineStart(blockIndex);
        ref VerbatimLine fenceLine = ref blockVerbatimLines_arr[firstVerbLineIndex];

        int beg = fenceLine.Beg;
        int end = fenceLine.End;
        char fenceCh = text[fenceLine.Beg];
        int ret = 0;

        // Skip the fence itself.
        while (beg < size && text[beg] == fenceCh)
            beg++;
        // Trim initial spaces.
        while (beg < size && text[beg] == ' ')
            beg++;

        // Trim trailing spaces.
        while (end > beg && text[end - 1] == ' ')
            end--;

        // Build info string attribute.
        ret = BuildAttribute(beg, end - beg, 0, out MdAttribute infoBuild);
        if (ret < 0) return ret;

        // Build lang string attribute (up to first whitespace).
        int langEnd = beg;
        while (langEnd < end && !Md4cUnicode.IsWhitespace(text[langEnd]))
            langEnd++;
        ret = BuildAttribute(beg, langEnd - beg, 0, out MdAttribute langBuild);
        if (ret < 0) return ret;

        det.Info = infoBuild;
        det.Lang = langBuild;
        det.FenceChar = fenceCh;

        return ret;
    }

    // ── Table cell processing ────────────────────────────────────────────

    private int ProcessTableCell(MdBlockType cellType, MdAlign align, int beg, int end)
    {
        int ret = 0;

        while (beg < end && Md4cUnicode.IsWhitespace(text[beg]))
            beg++;
        while (end > beg && Md4cUnicode.IsWhitespace(text[end - 1]))
            end--;

        var det = new MdBlockTdDetail { Align = align };
        var lineArr = new Line[] { new Line { Beg = beg, End = end } };

        ret = EnterBlock(cellType, det);
        if (ret != 0) return ret;

        ret = ProcessNormalBlockContents(lineArr, 1, 0);
        int ret2 = LeaveBlock(cellType, det);
        if (ret == 0) ret = ret2;

        return ret;
    }

    // ── Table row processing ─────────────────────────────────────────────

    private int ProcessTableRow(MdBlockType cellType, int beg, int end,
                                MdAlign[] align, int colCount)
    {
        int ret = 0;
        int[]? pipeOffs = null;

        var lineArr = new Line[] { new Line { Beg = beg, End = end } };

        // Break the line into table cells by identifying pipe characters.
        ret = AnalyzeInlines(lineArr, 1, 0, true);
        if (ret < 0) goto abort;

        // Remember the cell boundaries in local buffer because
        // marks[] shall be reused during cell contents processing.
        int n = nTableCellBoundaries + 2;
        pipeOffs = new int[n];

        int j = 0;
        pipeOffs[j++] = beg;
        for (int i = tableCellBoundariesHead; i >= 0; i = marks[i].Next)
        {
            ref Mark mark = ref marks[i];
            pipeOffs[j++] = mark.End;
        }
        pipeOffs[j++] = end + 1;

        // Process cells.
        ret = EnterBlock(MdBlockType.Tr, null);
        if (ret != 0) goto abort;

        int k = 0;
        for (int i = 0; i < j - 1 && k < colCount; i++)
        {
            if (pipeOffs[i] < pipeOffs[i + 1] - 1)
            {
                ret = ProcessTableCell(cellType, align[k++], pipeOffs[i], pipeOffs[i + 1] - 1);
                if (ret != 0) goto leave_tr;
            }
        }
        // Make sure we call enough table cells even if the current table contains too few.
        while (k < colCount)
        {
            ret = ProcessTableCell(cellType, align[k++], 0, 0);
            if (ret != 0) goto leave_tr;
        }

    leave_tr:
        {
            int r = LeaveBlock(MdBlockType.Tr, null);
            if (ret == 0) ret = r;
        }

    abort:
        tableCellBoundariesHead = -1;
        tableCellBoundariesTail = -1;

        return ret;
    }

    // ── Table block contents processing ──────────────────────────────────

    private int ProcessTableBlockContents(int colCount, Line[] lines, int nLines, int lineOffset)
    {
        int ret = 0;

        Debug.Assert(nLines >= 2);

        var align = new MdAlign[colCount];

        AnalyzeTableAlignment(lines[lineOffset + 1].Beg, lines[lineOffset + 1].End, align, colCount);

        ret = EnterBlock(MdBlockType.Thead, null);
        if (ret != 0) return ret;
        ret = ProcessTableRow(MdBlockType.Th,
                    lines[lineOffset].Beg, lines[lineOffset].End, align, colCount);
        {
            int r = LeaveBlock(MdBlockType.Thead, null);
            if (ret == 0) ret = r;
            if (ret != 0) return ret;
        }

        if (nLines > 2)
        {
            ret = EnterBlock(MdBlockType.Tbody, null);
            if (ret != 0) return ret;
            for (int lineIndex = 2; lineIndex < nLines; lineIndex++)
            {
                ret = ProcessTableRow(MdBlockType.Td,
                         lines[lineOffset + lineIndex].Beg, lines[lineOffset + lineIndex].End, align, colCount);
                if (ret != 0) break;
            }
            int r = LeaveBlock(MdBlockType.Tbody, null);
            if (ret == 0) ret = r;
        }

        return ret;
    }

    // ── Leaf block processing ────────────────────────────────────────────

    private int ProcessLeafBlock(int blockIndex)
    {
        MdBlockCodeDetail codeDet = default;
        bool isInTightList;
        int ret = 0;

        ref Block block = ref blocks_arr[blockIndex];

        if (nContainers == 0)
            isInTightList = false;
        else
            isInTightList = !containers[nContainers - 1].IsLoose;

        object? detail = null;

        switch (block.Type)
        {
            case MdBlockType.H:
                detail = new MdBlockHDetail { Level = block.Data };
                break;

            case MdBlockType.Code:
                if (block.Data != 0)
                {
                    ret = SetupFencedCodeDetail(blockIndex, out codeDet);
                    if (ret < 0) return ret;
                    detail = codeDet;
                }
                else
                {
                    detail = new MdBlockCodeDetail();
                }
                break;

            case MdBlockType.Table:
                detail = new MdBlockTableDetail
                {
                    ColCount = block.Data,
                    HeadRowCount = 1,
                    BodyRowCount = block.NLines - 2,
                };
                break;

            default:
                break;
        }

        if (!isInTightList || block.Type != MdBlockType.P)
        {
            ret = EnterBlock(block.Type, detail);
            if (ret != 0) return ret;
        }

        // Process the block contents according to its type.
        switch (block.Type)
        {
            case MdBlockType.Hr:
                // noop
                break;

            case MdBlockType.Code:
            {
                int verbLineStart = GetBlockVerbatimLineStart(blockIndex);
                ret = ProcessCodeBlockContents(block.Data != 0, blockVerbatimLines_arr, block.NLines, verbLineStart);
                break;
            }

            case MdBlockType.Html:
            {
                int verbLineStart = GetBlockVerbatimLineStart(blockIndex);
                ret = ProcessVerbatimBlockContents(MdTextType.Html, blockVerbatimLines_arr, block.NLines, verbLineStart);
                break;
            }

            case MdBlockType.Table:
            {
                int lineStart = GetBlockLineStart(blockIndex);
                ret = ProcessTableBlockContents(block.Data, blockLines_arr, block.NLines, lineStart);
                break;
            }

            default:
            {
                int lineStart = GetBlockLineStart(blockIndex);
                ret = ProcessNormalBlockContents(blockLines_arr, block.NLines, lineStart);
                break;
            }
        }

        if (!isInTightList || block.Type != MdBlockType.P)
        {
            int r = LeaveBlock(block.Type, detail);
            if (ret == 0) ret = r;
        }

        return ret;
    }

    // ── Process all blocks ───────────────────────────────────────────────

    // Arrays snapshotted from lists before processing.
    private Block[] blocks_arr = Array.Empty<Block>();
    private Line[] blockLines_arr = Array.Empty<Line>();
    private VerbatimLine[] blockVerbatimLines_arr = Array.Empty<VerbatimLine>();

    // Precomputed line start indices per block.
    private int[] blockLineStarts = Array.Empty<int>();
    private int[] blockVerbatimLineStarts = Array.Empty<int>();

    private int GetBlockLineStart(int blockIndex) => blockLineStarts[blockIndex];

    private int GetBlockVerbatimLineStart(int blockIndex) => blockVerbatimLineStarts[blockIndex];

    /// <summary>
    /// Snapshot the lists to arrays and precompute line start indices.
    /// Called after all blocks are recorded and before ProcessAllBlocks.
    /// </summary>
    private void SnapshotBlocks()
    {
        blocks_arr = blocks.ToArray();
        blockLines_arr = blockLines.ToArray();
        blockVerbatimLines_arr = blockVerbatimLines.ToArray();

        int nBlocks = blocks_arr.Length;
        blockLineStarts = new int[nBlocks];
        blockVerbatimLineStarts = new int[nBlocks];

        int lineIdx = 0;
        int verbIdx = 0;

        for (int i = 0; i < nBlocks; i++)
        {
            ref Block b = ref blocks_arr[i];

            blockLineStarts[i] = lineIdx;
            blockVerbatimLineStarts[i] = verbIdx;

            if ((b.Flags & BLOCK_CONTAINER) != 0)
                continue;

            if (b.Type == MdBlockType.Code || b.Type == MdBlockType.Html)
                verbIdx += b.NLines;
            else
                lineIdx += b.NLines;
        }
    }

    private int ProcessAllBlocks()
    {
        int ret = 0;

        SnapshotBlocks();

        // containers is now reused for tracking loose/tight state during output.
        nContainers = 0;

        int nBlocks = blocks_arr.Length;

        for (int blockIndex = 0; blockIndex < nBlocks; blockIndex++)
        {
            ref Block block = ref blocks_arr[blockIndex];

            object? det = null;

            switch (block.Type)
            {
                case MdBlockType.Ul:
                    det = new MdBlockUlDetail
                    {
                        IsTight = (block.Flags & BLOCK_LOOSE_LIST) == 0,
                        Mark = (char)block.Data,
                    };
                    break;

                case MdBlockType.Ol:
                    det = new MdBlockOlDetail
                    {
                        Start = block.NLines,
                        IsTight = (block.Flags & BLOCK_LOOSE_LIST) == 0,
                        MarkDelimiter = (char)block.Data,
                    };
                    break;

                case MdBlockType.Li:
                    det = new MdBlockLiDetail
                    {
                        IsTask = block.Data != 0,
                        TaskMark = (char)block.Data,
                        TaskMarkOffset = block.NLines,
                    };
                    break;

                default:
                    break;
            }

            if ((block.Flags & BLOCK_CONTAINER) != 0)
            {
                if ((block.Flags & BLOCK_CONTAINER_CLOSER) != 0)
                {
                    ret = LeaveBlock(block.Type, det);
                    if (ret != 0) return ret;

                    if (block.Type == MdBlockType.Ul || block.Type == MdBlockType.Ol || block.Type == MdBlockType.Quote)
                        nContainers--;
                }

                if ((block.Flags & BLOCK_CONTAINER_OPENER) != 0)
                {
                    ret = EnterBlock(block.Type, det);
                    if (ret != 0) return ret;

                    if (block.Type == MdBlockType.Ul || block.Type == MdBlockType.Ol)
                    {
                        containers[nContainers].IsLoose = (block.Flags & BLOCK_LOOSE_LIST) != 0;
                        nContainers++;
                    }
                    else if (block.Type == MdBlockType.Quote)
                    {
                        containers[nContainers].IsLoose = true;
                        nContainers++;
                    }
                }
            }
            else
            {
                ret = ProcessLeafBlock(blockIndex);
                if (ret != 0) return ret;
            }
        }

        blocks.Clear();
        blockLines.Clear();
        blockVerbatimLines.Clear();

        return ret;
    }

    // ── Grouping Lines into Blocks ───────────────────────────────────────

    private int StartNewBlock(ref LineAnalysis line)
    {
        Debug.Assert(currentBlockIndex == -1);

        Block block = default;

        switch (line.Type)
        {
            case LineType.Hr:
                block.Type = MdBlockType.Hr;
                break;

            case LineType.AtxHeader:
            case LineType.SetextHeader:
                block.Type = MdBlockType.H;
                break;

            case LineType.FencedCode:
            case LineType.IndentedCode:
                block.Type = MdBlockType.Code;
                break;

            case LineType.Text:
                block.Type = MdBlockType.P;
                break;

            case LineType.Html:
                block.Type = MdBlockType.Html;
                break;

            case LineType.Blank:
            case LineType.SetextUnderline:
            case LineType.TableUnderline:
            default:
                Debug.Fail("Unreachable: StartNewBlock called with unexpected line type.");
                break;
        }

        block.Flags = 0;
        block.Data = line.Data;
        block.NLines = 0;

        currentBlockIndex = blocks.Count;
        blocks.Add(block);
        return 0;
    }

    /// <summary>
    /// Eat from start of current (textual) block any reference definitions.
    /// </summary>
    private int ConsumeLinkReferenceDefinitions()
    {
        Debug.Assert(currentBlockIndex >= 0);

        int nLines = CurrentBlock.NLines;
        int lineStart = GetCurrentBlockLineStart();
        int n = 0;

        while (n < nLines)
        {
            int nLinkRefLines = IsLinkReferenceDefinition(lineStart + n, nLines - n);
            if (nLinkRefLines == 0)
                break;
            if (nLinkRefLines < 0)
                return -1;
            n += nLinkRefLines;
        }

        if (n > 0)
        {
            if (n == nLines)
            {
                blocks.RemoveAt(currentBlockIndex);
                for (int i = 0; i < n; i++)
                    blockLines.RemoveAt(blockLines.Count - 1);
                currentBlockIndex = -1;
            }
            else
            {
                int removeStart = blockLines.Count - nLines;
                blockLines.RemoveRange(removeStart, n);
                CurrentBlock.NLines -= n;
            }
        }

        return 0;
    }

    private int EndCurrentBlock()
    {
        int ret = 0;

        if (currentBlockIndex < 0)
            return ret;

        // Check for reference definitions.
        if (CurrentBlock.Type == MdBlockType.P ||
           (CurrentBlock.Type == MdBlockType.H && (CurrentBlock.Flags & BLOCK_SETEXT_HEADER) != 0))
        {
            int lineStart = GetCurrentBlockLineStart();
            if (lineStart < blockLines.Count)
            {
                var span = CollectionsMarshal.AsSpan(blockLines);
                ref Line firstLine = ref span[lineStart];
                if (firstLine.Beg < size && text[firstLine.Beg] == '[')
                {
                    ret = ConsumeLinkReferenceDefinitions();
                    if (ret < 0) return ret;
                    if (currentBlockIndex < 0)
                        return ret;
                }
            }
        }

        if (CurrentBlock.Type == MdBlockType.H && (CurrentBlock.Flags & BLOCK_SETEXT_HEADER) != 0)
        {
            int nLines = CurrentBlock.NLines;

            if (nLines > 1)
            {
                CurrentBlock.NLines--;
                blockLines.RemoveAt(blockLines.Count - 1);
            }
            else
            {
                CurrentBlock.Type = MdBlockType.P;
                return 0;
            }
        }

        currentBlockIndex = -1;

        return ret;
    }

    private int AddLineIntoCurrentBlock(ref LineAnalysis analysis)
    {
        Debug.Assert(currentBlockIndex >= 0);

        if (CurrentBlock.Type == MdBlockType.Code || CurrentBlock.Type == MdBlockType.Html)
        {
            blockVerbatimLines.Add(new VerbatimLine
            {
                Indent = analysis.Indent,
                Beg = analysis.Beg,
                End = analysis.End,
            });
        }
        else
        {
            blockLines.Add(new Line
            {
                Beg = analysis.Beg,
                End = analysis.End,
            });
        }

        CurrentBlock.NLines++;
        return 0;
    }

    private int PushContainerBytes(MdBlockType type, int start, int data, byte blockFlags)
    {
        int ret = EndCurrentBlock();
        if (ret != 0) return ret;

        blocks.Add(new Block
        {
            Type = type,
            Flags = blockFlags,
            Data = data,
            NLines = start,
        });

        return 0;
    }

    // ── Current block accessor ───────────────────────────────────────────

    private ref Block CurrentBlock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref CollectionsMarshal.AsSpan(blocks)[currentBlockIndex];
    }

    private int GetCurrentBlockLineStart()
    {
        int lineIdx = 0;
        var span = CollectionsMarshal.AsSpan(blocks);
        for (int i = 0; i < currentBlockIndex; i++)
        {
            ref Block b = ref span[i];
            if ((b.Flags & BLOCK_CONTAINER) != 0)
                continue;
            if (b.Type != MdBlockType.Code && b.Type != MdBlockType.Html)
                lineIdx += b.NLines;
        }
        return lineIdx;
    }

    // ── Line Analysis ────────────────────────────────────────────────────

    private bool IsHrLine(int beg, out int pEnd, out int pKiller)
    {
        int off = beg + 1;
        int n = 1;
        pEnd = 0;
        pKiller = 0;

        while (off < size && (text[off] == text[beg] || text[off] == ' ' || text[off] == '\t'))
        {
            if (text[off] == text[beg])
                n++;
            off++;
        }

        if (n < 3)
        {
            pKiller = off;
            return false;
        }

        if (off < size && !Md4cUnicode.IsNewline(text[off]))
        {
            pKiller = off;
            return false;
        }

        pEnd = off;
        return true;
    }

    private bool IsAtxHeaderLine(int beg, out int pBeg, out int pEnd, out int pLevel)
    {
        int off = beg + 1;
        pBeg = 0;
        pEnd = 0;
        pLevel = 0;

        while (off < size && text[off] == '#' && off - beg < 7)
            off++;
        int n = off - beg;

        if (n > 6)
            return false;
        pLevel = n;

        if ((flags & MdParserFlags.PermissiveAtxHeaders) == 0 && off < size &&
            !Md4cUnicode.IsBlank(text[off]) && !Md4cUnicode.IsNewline(text[off]))
            return false;

        while (off < size && Md4cUnicode.IsBlank(text[off]))
            off++;
        pBeg = off;
        pEnd = off;
        return true;
    }

    private bool IsSetextUnderline(int beg, out int pEnd, out int pLevel)
    {
        int off = beg + 1;
        pEnd = 0;
        pLevel = 0;

        while (off < size && text[off] == text[beg])
            off++;

        while (off < size && Md4cUnicode.IsBlank(text[off]))
            off++;

        if (off < size && !Md4cUnicode.IsNewline(text[off]))
            return false;

        pLevel = text[beg] == '=' ? 1 : 2;
        pEnd = off;
        return true;
    }

    private bool IsTableUnderline(int beg, out int pEnd, out int pColCount)
    {
        int off = beg;
        bool foundPipe = false;
        int colCount = 0;
        pEnd = 0;
        pColCount = 0;

        if (off < size && text[off] == '|')
        {
            foundPipe = true;
            off++;
            while (off < size && Md4cUnicode.IsWhitespace(text[off]))
                off++;
        }

        while (true)
        {
            if (off < size && text[off] == ':')
                off++;
            if (off >= size || text[off] != '-')
                return false;
            while (off < size && text[off] == '-')
                off++;
            if (off < size && text[off] == ':')
                off++;

            colCount++;
            if (colCount > TABLE_MAXCOLCOUNT)
            {
                Log($"Suppressing table (column_count > {TABLE_MAXCOLCOUNT})");
                return false;
            }

            while (off < size && Md4cUnicode.IsWhitespace(text[off]))
                off++;
            bool delimited = false;
            if (off < size && text[off] == '|')
            {
                delimited = true;
                foundPipe = true;
                off++;
                while (off < size && Md4cUnicode.IsWhitespace(text[off]))
                    off++;
            }

            if (off >= size || Md4cUnicode.IsNewline(text[off]))
                break;

            if (!delimited)
                return false;
        }

        if (!foundPipe)
            return false;

        pEnd = off;
        pColCount = colCount;
        return true;
    }

    private bool IsOpeningCodeFence(int beg, out int pEnd)
    {
        int off = beg;
        pEnd = 0;

        while (off < size && text[off] == text[beg])
            off++;

        if (off - beg < 3)
            return false;

        codeFenceLength = off - beg;

        while (off < size && text[off] == ' ')
            off++;

        while (off < size && !Md4cUnicode.IsNewline(text[off]))
        {
            if (text[beg] == '`' && text[off] == '`')
                return false;
            off++;
        }

        pEnd = off;
        return true;
    }

    private bool IsClosingCodeFence(char ch, int beg, out int pEnd)
    {
        int off = beg;
        bool result = false;
        pEnd = 0;

        while (off < size && text[off] == ch)
            off++;
        if (off - beg < codeFenceLength)
            goto done;

        while (off < size && text[off] == ' ')
            off++;

        if (off < size && !Md4cUnicode.IsNewline(text[off]))
            goto done;

        result = true;

    done:
        pEnd = off;
        return result;
    }

    // ── HTML block detection ─────────────────────────────────────────────

    private int IsHtmlBlockStartCondition(int beg)
    {
        int off = beg + 1;

        // Check for type 1
        foreach (string tag in HtmlType1Tags)
        {
            if (off + tag.Length <= size)
            {
                if (AsciiCaseEq(off, tag, tag.Length))
                    return 1;
            }
        }

        // Check for type 2: <!--
        if (off + 3 < size && text[off] == '!' && text[off + 1] == '-' && text[off + 2] == '-')
            return 2;

        // Check for type 3: <?
        if (off < size && text[off] == '?')
            return 3;

        // Check for type 4 or 5: <!
        if (off < size && text[off] == '!')
        {
            if (off + 1 < size && Md4cUnicode.IsUpper(text[off + 1]))
                return 4;

            if (off + 8 < size)
            {
                if (AsciiEq(off, "![CDATA[", 8))
                    return 5;
            }
        }

        // Check for type 6
        if (off + 1 < size && (Md4cUnicode.IsAlpha(text[off]) || (text[off] == '/' && Md4cUnicode.IsAlpha(text[off + 1]))))
        {
            int tagOff = off;
            if (text[tagOff] == '/')
                tagOff++;

            int slot = Md4cUnicode.IsUpper(text[tagOff])
                ? text[tagOff] - 'A'
                : text[tagOff] - 'a';

            if (slot >= 0 && slot < 26)
            {
                string[] tags = HtmlType6Map[slot];

                foreach (string tag in tags)
                {
                    if (tagOff + tag.Length <= size)
                    {
                        if (AsciiCaseEq(tagOff, tag, tag.Length))
                        {
                            int tmp = tagOff + tag.Length;
                            if (tmp >= size)
                                return 6;
                            if (Md4cUnicode.IsBlank(text[tmp]) || Md4cUnicode.IsNewline(text[tmp]) || text[tmp] == '>')
                                return 6;
                            if (tmp + 1 < size && text[tmp] == '/' && text[tmp + 1] == '>')
                                return 6;
                            break;
                        }
                    }
                }
            }
        }

        // Check for type 7
        if (off + 1 < size)
        {
            if (IsHtmlTag(null, 0, beg, size, out int end))
            {
                while (end < size && Md4cUnicode.IsWhitespace(text[end]))
                    end++;
                if (end >= size || Md4cUnicode.IsNewline(text[end]))
                    return 7;
            }
        }

        return 0;
    }

    private bool LineContains(int beg, string what, out int pEnd)
    {
        int whatLen = what.Length;
        pEnd = beg;

        for (int i = beg; i + whatLen <= size; i++)
        {
            if (i < size && Md4cUnicode.IsNewline(text[i]))
                break;
            if (text.AsSpan(i, whatLen).SequenceEqual(what.AsSpan()))
            {
                pEnd = i + whatLen;
                return true;
            }
        }

        int off2 = beg;
        while (off2 < size && !Md4cUnicode.IsNewline(text[off2]))
            off2++;
        pEnd = off2;
        return false;
    }

    private int IsHtmlBlockEndCondition(int beg, out int pEnd)
    {
        pEnd = beg;

        switch (htmlBlockType)
        {
            case 1:
            {
                int off = beg;
                while (off + 1 < size && !Md4cUnicode.IsNewline(text[off]))
                {
                    if (text[off] == '<' && text[off + 1] == '/')
                    {
                        foreach (string tag in HtmlType1Tags)
                        {
                            if (off + 2 + tag.Length < size)
                            {
                                if (AsciiCaseEq(off + 2, tag, tag.Length) &&
                                    text[off + 2 + tag.Length] == '>')
                                {
                                    pEnd = off + 2 + tag.Length + 1;
                                    return 1;
                                }
                            }
                        }
                    }
                    off++;
                }
                pEnd = off;
                return 0;
            }

            case 2:
                return LineContains(beg, "-->", out pEnd) ? 2 : 0;

            case 3:
                return LineContains(beg, "?>", out pEnd) ? 3 : 0;

            case 4:
                return LineContains(beg, ">", out pEnd) ? 4 : 0;

            case 5:
                return LineContains(beg, "]]>", out pEnd) ? 5 : 0;

            case 6:
            case 7:
                if (beg >= size || Md4cUnicode.IsNewline(text[beg]))
                {
                    pEnd = beg;
                    return htmlBlockType;
                }
                return 0;

            default:
                Debug.Fail("Unreachable");
                return 0;
        }
    }

    // ── Container management ─────────────────────────────────────────────

    private static bool IsContainerCompatible(ref Container pivot, ref Container container)
    {
        if (container.Ch == '>')
            return false;
        if (container.Ch != pivot.Ch)
            return false;
        if (container.MarkIndent > pivot.ContentIndent)
            return false;
        return true;
    }

    private const int MaxNestingDepth = 100;

    private int PushContainer(ref Container container)
    {
        if (nContainers >= MaxNestingDepth)
            return -1;

        if (nContainers >= containers.Length)
        {
            int newSize = containers.Length > 0
                ? containers.Length + containers.Length / 2
                : 16;
            Array.Resize(ref containers, newSize);
        }

        containers[nContainers++] = container;
        return 0;
    }

    private int EnterChildContainers(int nChildren)
    {
        int ret = 0;

        for (int i = nContainers - nChildren; i < nContainers; i++)
        {
            ref Container c = ref containers[i];
            bool isOrderedList = false;

            switch (c.Ch)
            {
                case ')':
                case '.':
                    isOrderedList = true;
                    goto case '-';

                case '-':
                case '+':
                case '*':
                    EndCurrentBlock();
                    c.BlockIndex = blocks.Count;

                    ret = PushContainerBytes(
                        isOrderedList ? MdBlockType.Ol : MdBlockType.Ul,
                        c.Start, c.Ch, BLOCK_CONTAINER_OPENER);
                    if (ret != 0) return ret;

                    ret = PushContainerBytes(MdBlockType.Li,
                        c.TaskMarkOff,
                        c.IsTask ? text[c.TaskMarkOff] : 0,
                        BLOCK_CONTAINER_OPENER);
                    if (ret != 0) return ret;
                    break;

                case '>':
                    ret = PushContainerBytes(MdBlockType.Quote, 0, 0, BLOCK_CONTAINER_OPENER);
                    if (ret != 0) return ret;
                    break;

                default:
                    Debug.Fail("Unreachable");
                    break;
            }
        }

        return ret;
    }

    private int LeaveChildContainers(int nKeep)
    {
        int ret = 0;

        while (nContainers > nKeep)
        {
            ref Container c = ref containers[nContainers - 1];
            bool isOrderedList = false;

            switch (c.Ch)
            {
                case ')':
                case '.':
                    isOrderedList = true;
                    goto case '-';

                case '-':
                case '+':
                case '*':
                    ret = PushContainerBytes(MdBlockType.Li,
                        c.TaskMarkOff, c.IsTask ? text[c.TaskMarkOff] : 0,
                        BLOCK_CONTAINER_CLOSER);
                    if (ret != 0) return ret;

                    ret = PushContainerBytes(
                        isOrderedList ? MdBlockType.Ol : MdBlockType.Ul,
                        0, c.Ch, BLOCK_CONTAINER_CLOSER);
                    if (ret != 0) return ret;
                    break;

                case '>':
                    ret = PushContainerBytes(MdBlockType.Quote, 0, 0, BLOCK_CONTAINER_CLOSER);
                    if (ret != 0) return ret;
                    break;

                default:
                    Debug.Fail("Unreachable");
                    break;
            }

            nContainers--;
        }

        return ret;
    }

    private bool IsContainerMark(int indent, int beg, out int pEnd, out Container pContainer)
    {
        int off = beg;
        pEnd = 0;
        pContainer = default;

        if (off >= size || indent >= codeIndentOffset)
            return false;

        // Check for block quote mark.
        if (text[off] == '>')
        {
            off++;
            pContainer.Ch = '>';
            pContainer.IsLoose = false;
            pContainer.IsTask = false;
            pContainer.MarkIndent = indent;
            pContainer.ContentIndent = indent + 1;
            pEnd = off;
            return true;
        }

        // Check for list item bullet mark.
        if (IsAnyOf(text[off], "-+*") && (off + 1 >= size || Md4cUnicode.IsBlank(text[off + 1]) || Md4cUnicode.IsNewline(text[off + 1])))
        {
            pContainer.Ch = text[off];
            pContainer.IsLoose = false;
            pContainer.IsTask = false;
            pContainer.MarkIndent = indent;
            pContainer.ContentIndent = indent + 1;
            pEnd = off + 1;
            return true;
        }

        // Check for ordered list item marks.
        int maxEnd = Math.Min(off + 9, size);
        pContainer.Start = 0;
        while (off < maxEnd && Md4cUnicode.IsDigit(text[off]))
        {
            pContainer.Start = pContainer.Start * 10 + text[off] - '0';
            off++;
        }
        if (off > beg &&
            off < size &&
            (text[off] == '.' || text[off] == ')') &&
            (off + 1 >= size || Md4cUnicode.IsBlank(text[off + 1]) || Md4cUnicode.IsNewline(text[off + 1])))
        {
            pContainer.Ch = text[off];
            pContainer.IsLoose = false;
            pContainer.IsTask = false;
            pContainer.MarkIndent = indent;
            pContainer.ContentIndent = indent + off - beg + 1;
            pEnd = off + 1;
            return true;
        }

        return false;
    }

    private int LineIndentation(int totalIndent, int beg, out int pEnd)
    {
        int off = beg;
        int indent = totalIndent;
        pEnd = beg;

        while (off < size && Md4cUnicode.IsBlank(text[off]))
        {
            if (text[off] == '\t')
                indent = (indent + 4) & ~3;
            else
                indent++;
            off++;
        }

        pEnd = off;
        return indent - totalIndent;
    }

    // ── The main line classification state machine ───────────────────────

    private int AnalyzeLine(int beg, out int pEnd, ref LineAnalysis pivotLine, ref LineAnalysis line)
    {
        int totalIndent = 0;
        int nParents = 0;
        int nBrothers = 0;
        int nChildren = 0;
        Container container = default;
        bool prevLineHasListLooseningEffect = lastLineHasListLooseningEffect;
        int off = beg;
        int hrKiller = 0;
        int ret = 0;
        pEnd = beg;

        line.Indent = LineIndentation(totalIndent, off, out off);
        totalIndent += line.Indent;
        line.Beg = off;
        line.EnforceNewBlock = false;

        // Determine how many of the current containers are our parents.
        while (nParents < nContainers)
        {
            ref Container c = ref containers[nParents];

            if (c.Ch == '>' && line.Indent < codeIndentOffset &&
                off < size && text[off] == '>')
            {
                off++;
                totalIndent++;
                line.Indent = LineIndentation(totalIndent, off, out off);
                totalIndent += line.Indent;

                if (line.Indent > 0)
                    line.Indent--;

                line.Beg = off;
            }
            else if (c.Ch != '>' && line.Indent >= c.ContentIndent)
            {
                line.Indent -= c.ContentIndent;
            }
            else
            {
                break;
            }

            nParents++;
        }

        if (off >= size || Md4cUnicode.IsNewline(text[off]))
        {
            if (nBrothers + nChildren == 0)
            {
                while (nParents < nContainers && containers[nParents].Ch != '>')
                    nParents++;
            }
        }

        while (true)
        {
            // Check whether we are fenced code continuation.
            if (pivotLine.Type == LineType.FencedCode)
            {
                line.Beg = off;

                if (line.Indent < codeIndentOffset)
                {
                    if (IsClosingCodeFence(text[pivotLine.Beg], off, out off))
                    {
                        line.Type = LineType.Blank;
                        lastLineHasListLooseningEffect = false;
                        break;
                    }
                }

                if (nParents == nContainers)
                {
                    if (line.Indent > pivotLine.Indent)
                        line.Indent -= pivotLine.Indent;
                    else
                        line.Indent = 0;

                    line.Type = LineType.FencedCode;
                    break;
                }
            }

            // Check whether we are HTML block continuation.
            if (pivotLine.Type == LineType.Html && htmlBlockType > 0)
            {
                if (nParents < nContainers)
                {
                    htmlBlockType = 0;
                }
                else
                {
                    int htmlEndType = IsHtmlBlockEndCondition(off, out off);
                    if (htmlEndType > 0)
                    {
                        Debug.Assert(htmlEndType == htmlBlockType);
                        htmlBlockType = 0;

                        if (htmlEndType == 6 || htmlEndType == 7)
                        {
                            line.Type = LineType.Blank;
                            line.Indent = 0;
                            break;
                        }
                    }

                    line.Type = LineType.Html;
                    nParents = nContainers;
                    break;
                }
            }

            // Check for blank line.
            if (off >= size || Md4cUnicode.IsNewline(text[off]))
            {
                if (pivotLine.Type == LineType.IndentedCode && nParents == nContainers)
                {
                    line.Type = LineType.IndentedCode;
                    if (line.Indent > codeIndentOffset)
                        line.Indent -= codeIndentOffset;
                    else
                        line.Indent = 0;
                    lastLineHasListLooseningEffect = false;
                }
                else
                {
                    line.Type = LineType.Blank;
                    lastLineHasListLooseningEffect = (nParents > 0 &&
                        nBrothers + nChildren == 0 &&
                        containers[nParents - 1].Ch != '>');

                    if (nParents > 0 && containers[nParents - 1].Ch != '>' &&
                        nBrothers + nChildren == 0 && currentBlockIndex < 0 &&
                        blocks.Count > 0)
                    {
                        var span = CollectionsMarshal.AsSpan(blocks);
                        ref Block topBlock = ref span[blocks.Count - 1];
                        if (topBlock.Type == MdBlockType.Li)
                            lastListItemStartsWithTwoBlankLines = true;
                    }
                }
                break;
            }
            else
            {
                if (lastListItemStartsWithTwoBlankLines)
                {
                    if (nParents > 0 && nParents == nContainers &&
                        containers[nParents - 1].Ch != '>' &&
                        nBrothers + nChildren == 0 && currentBlockIndex < 0 &&
                        blocks.Count > 0)
                    {
                        var span = CollectionsMarshal.AsSpan(blocks);
                        ref Block topBlock = ref span[blocks.Count - 1];
                        if (topBlock.Type == MdBlockType.Li)
                        {
                            nParents--;
                            line.Indent = totalIndent;
                            if (nParents > 0)
                                line.Indent -= Math.Min(line.Indent, containers[nParents - 1].ContentIndent);
                        }
                    }

                    lastListItemStartsWithTwoBlankLines = false;
                }
                lastLineHasListLooseningEffect = false;
            }

            // Check whether we are Setext underline.
            if (line.Indent < codeIndentOffset && pivotLine.Type == LineType.Text &&
                off < size && IsAnyOf2(text[off], '=', '-') &&
                nParents == nContainers)
            {
                if (IsSetextUnderline(off, out int setextEnd, out int level))
                {
                    off = setextEnd;
                    line.Type = LineType.SetextUnderline;
                    line.Data = level;
                    break;
                }
            }

            // Check for thematic break line.
            if (line.Indent < codeIndentOffset &&
                off < size && off >= hrKiller &&
                IsAnyOf(text[off], "-_*"))
            {
                if (IsHrLine(off, out int hrEnd, out hrKiller))
                {
                    off = hrEnd;
                    line.Type = LineType.Hr;
                    break;
                }
            }

            // Check for "brother" container.
            if (nParents < nContainers && nBrothers + nChildren == 0)
            {
                if (IsContainerMark(line.Indent, off, out int tmp, out container) &&
                    IsContainerCompatible(ref containers[nParents], ref container))
                {
                    pivotLine = DummyBlankLine;

                    off = tmp;

                    totalIndent += container.ContentIndent - container.MarkIndent;
                    line.Indent = LineIndentation(totalIndent, off, out off);
                    totalIndent += line.Indent;
                    line.Beg = off;

                    if (off >= size || Md4cUnicode.IsNewline(text[off]))
                    {
                        container.ContentIndent++;
                    }
                    else if (line.Indent <= codeIndentOffset)
                    {
                        container.ContentIndent += line.Indent;
                        line.Indent = 0;
                    }
                    else
                    {
                        container.ContentIndent += 1;
                        line.Indent--;
                    }

                    containers[nParents].MarkIndent = container.MarkIndent;
                    containers[nParents].ContentIndent = container.ContentIndent;

                    nBrothers++;
                    continue;
                }
            }

            // Check for indented code.
            if (line.Indent >= codeIndentOffset && pivotLine.Type != LineType.Text)
            {
                line.Type = LineType.IndentedCode;
                line.Indent -= codeIndentOffset;
                line.Data = 0;
                break;
            }

            // Check for start of a new container block.
            if (line.Indent < codeIndentOffset &&
                IsContainerMark(line.Indent, off, out int containerEnd, out container))
            {
                if (pivotLine.Type == LineType.Text && nParents == nContainers &&
                    (containerEnd >= size || Md4cUnicode.IsNewline(text[containerEnd])) && container.Ch != '>')
                {
                    // Noop. List mark followed by a blank line cannot interrupt a paragraph.
                }
                else if (pivotLine.Type == LineType.Text && nParents == nContainers &&
                         IsAnyOf2(container.Ch, '.', ')') && container.Start != 1)
                {
                    // Noop. Ordered list cannot interrupt a paragraph unless start index is 1.
                }
                else
                {
                    off = containerEnd;
                    totalIndent += container.ContentIndent - container.MarkIndent;
                    line.Indent = LineIndentation(totalIndent, off, out off);
                    totalIndent += line.Indent;

                    line.Beg = off;
                    line.Data = container.Ch;

                    if (off >= size || Md4cUnicode.IsNewline(text[off]))
                    {
                        container.ContentIndent++;
                    }
                    else if (line.Indent <= codeIndentOffset)
                    {
                        container.ContentIndent += line.Indent;
                        line.Indent = 0;
                    }
                    else
                    {
                        container.ContentIndent += 1;
                        line.Indent--;
                    }

                    if (nBrothers + nChildren == 0)
                        pivotLine = DummyBlankLine;

                    if (nChildren == 0)
                    {
                        ret = LeaveChildContainers(nParents + nBrothers);
                        if (ret != 0) { pEnd = off; return ret; }
                    }

                    nChildren++;
                    ret = PushContainer(ref container);
                    if (ret != 0) { pEnd = off; return ret; }
                    continue;
                }
            }

            // Check whether we are table continuation.
            if (pivotLine.Type == LineType.Table && nParents == nContainers)
            {
                line.Type = LineType.Table;
                break;
            }

            // Check for ATX header.
            if (line.Indent < codeIndentOffset &&
                off < size && text[off] == '#')
            {
                if (IsAtxHeaderLine(off, out int atxBeg, out int atxEnd, out int level))
                {
                    line.Beg = atxBeg;
                    off = atxEnd;
                    line.Type = LineType.AtxHeader;
                    line.Data = level;
                    break;
                }
            }

            // Check whether we are starting code fence.
            if (line.Indent < codeIndentOffset &&
                off < size && IsAnyOf2(text[off], '`', '~'))
            {
                if (IsOpeningCodeFence(off, out int fenceEnd))
                {
                    off = fenceEnd;
                    line.Type = LineType.FencedCode;
                    line.Data = 1;
                    line.EnforceNewBlock = true;
                    break;
                }
            }

            // Check for start of raw HTML block.
            if (off < size && text[off] == '<' &&
                (flags & MdParserFlags.NoHtmlBlocks) == 0)
            {
                htmlBlockType = IsHtmlBlockStartCondition(off);

                if (htmlBlockType == 7 && pivotLine.Type == LineType.Text)
                    htmlBlockType = 0;

                if (htmlBlockType > 0)
                {
                    if (IsHtmlBlockEndCondition(off, out off) == htmlBlockType)
                    {
                        htmlBlockType = 0;
                    }

                    line.EnforceNewBlock = true;
                    line.Type = LineType.Html;
                    break;
                }
            }

            // Check for table underline.
            if ((flags & MdParserFlags.Tables) != 0 && pivotLine.Type == LineType.Text &&
                off < size && IsAnyOf3(text[off], '|', '-', ':') &&
                nParents == nContainers)
            {
                if (currentBlockIndex >= 0 && CurrentBlock.NLines == 1 &&
                    IsTableUnderline(off, out int tableEnd, out int colCount))
                {
                    off = tableEnd;
                    line.Data = colCount;
                    line.Type = LineType.TableUnderline;
                    break;
                }
            }

            // By default, we are normal text line.
            line.Type = LineType.Text;
            if (pivotLine.Type == LineType.Text && nBrothers + nChildren == 0)
            {
                nParents = nContainers;
            }

            // Check for task mark.
            if ((flags & MdParserFlags.TaskLists) != 0 && nBrothers + nChildren > 0 &&
                IsAnyOf(containers[nContainers - 1].Ch, "-+*.)"))
            {
                int tmp = off;

                while (tmp < size && tmp < off + 3 && Md4cUnicode.IsBlank(text[tmp]))
                    tmp++;
                if (tmp + 2 < size && text[tmp] == '[' &&
                    IsAnyOf(text[tmp + 1], "xX ") && text[tmp + 2] == ']' &&
                    (tmp + 3 == size || Md4cUnicode.IsBlank(text[tmp + 3]) || Md4cUnicode.IsNewline(text[tmp + 3])))
                {
                    ref Container taskContainer = ref (nChildren > 0 ? ref containers[nContainers - 1] : ref container);
                    taskContainer.IsTask = true;
                    taskContainer.TaskMarkOff = tmp + 1;
                    off = tmp + 3;
                    while (off < size && Md4cUnicode.IsWhitespace(text[off]))
                        off++;
                    line.Beg = off;
                }
            }

            break;
        }

        // Scan for end of the line.
        while (off + 3 < size && !Md4cUnicode.IsNewline(text[off]) && !Md4cUnicode.IsNewline(text[off + 1]) &&
                                  !Md4cUnicode.IsNewline(text[off + 2]) && !Md4cUnicode.IsNewline(text[off + 3]))
            off += 4;
        while (off < size && !Md4cUnicode.IsNewline(text[off]))
            off++;

        line.End = off;

        // For ATX header, exclude the optional trailing mark.
        if (line.Type == LineType.AtxHeader)
        {
            int tmp = line.End;
            while (tmp > line.Beg && Md4cUnicode.IsBlank(text[tmp - 1]))
                tmp--;
            while (tmp > line.Beg && text[tmp - 1] == '#')
                tmp--;
            if (tmp == line.Beg || Md4cUnicode.IsBlank(text[tmp - 1]) || (flags & MdParserFlags.PermissiveAtxHeaders) != 0)
                line.End = tmp;
        }

        // Trim trailing spaces.
        if (line.Type != LineType.IndentedCode && line.Type != LineType.FencedCode && line.Type != LineType.Html)
        {
            while (line.End > line.Beg && Md4cUnicode.IsBlank(text[line.End - 1]))
                line.End--;
        }

        // Eat also the new line.
        if (off < size && text[off] == '\r')
            off++;
        if (off < size && text[off] == '\n')
            off++;

        pEnd = off;

        // If we belong to a list after seeing a blank line, the list is loose.
        if (prevLineHasListLooseningEffect && line.Type != LineType.Blank && nParents + nBrothers > 0)
        {
            ref Container c = ref containers[nParents + nBrothers - 1];
            if (c.Ch != '>')
            {
                int blockIdx = c.BlockIndex;
                if (blockIdx >= 0 && blockIdx < blocks.Count)
                {
                    CollectionsMarshal.AsSpan(blocks)[blockIdx].Flags |= BLOCK_LOOSE_LIST;
                }
            }
        }

        // Leave any containers we are not part of anymore.
        if (nChildren == 0 && nParents + nBrothers < nContainers)
        {
            ret = LeaveChildContainers(nParents + nBrothers);
            if (ret != 0) return ret;
        }

        // Enter any container we found a mark for.
        if (nBrothers > 0)
        {
            Debug.Assert(nBrothers == 1);
            ret = PushContainerBytes(MdBlockType.Li,
                containers[nParents].TaskMarkOff,
                containers[nParents].IsTask ? text[containers[nParents].TaskMarkOff] : 0,
                BLOCK_CONTAINER_CLOSER);
            if (ret != 0) return ret;
            ret = PushContainerBytes(MdBlockType.Li,
                container.TaskMarkOff,
                container.IsTask ? text[container.TaskMarkOff] : 0,
                BLOCK_CONTAINER_OPENER);
            if (ret != 0) return ret;
            containers[nParents].IsTask = container.IsTask;
            containers[nParents].TaskMarkOff = container.TaskMarkOff;
        }

        if (nChildren > 0)
        {
            ret = EnterChildContainers(nChildren);
            if (ret != 0) return ret;
        }

        return ret;
    }

    // ── Process a single line ────────────────────────────────────────────

    /// <summary>
    /// Process a parsed line. pivotSlot tracks which lineBuf slot
    /// the pivot line references: -1 = DummyBlankLine, 0 or 1 = lineBuf index.
    /// </summary>
    private int ProcessLine(LineAnalysis[] lineBuf, int lineSlot, ref int pivotSlot)
    {
        ref LineAnalysis line = ref lineBuf[lineSlot];
        int ret = 0;

        // Blank line ends current leaf block.
        if (line.Type == LineType.Blank)
        {
            ret = EndCurrentBlock();
            if (ret != 0) return ret;
            pivotSlot = -1;
            return 0;
        }

        if (line.EnforceNewBlock)
        {
            ret = EndCurrentBlock();
            if (ret != 0) return ret;
        }

        LineType pivotType = pivotSlot < 0 ? DummyBlankLine.Type : lineBuf[pivotSlot].Type;

        // Some line types form block on their own.
        if (line.Type == LineType.Hr || line.Type == LineType.AtxHeader)
        {
            ret = EndCurrentBlock();
            if (ret != 0) return ret;

            ret = StartNewBlock(ref line);
            if (ret != 0) return ret;
            ret = AddLineIntoCurrentBlock(ref line);
            if (ret != 0) return ret;
            ret = EndCurrentBlock();
            if (ret != 0) return ret;
            pivotSlot = -1;
            return 0;
        }

        // SetextUnderline changes meaning of the current block and ends it.
        if (line.Type == LineType.SetextUnderline)
        {
            Debug.Assert(currentBlockIndex >= 0);
            CurrentBlock.Type = MdBlockType.H;
            CurrentBlock.Data = line.Data;
            CurrentBlock.Flags |= BLOCK_SETEXT_HEADER;
            ret = AddLineIntoCurrentBlock(ref line);
            if (ret != 0) return ret;
            ret = EndCurrentBlock();
            if (ret != 0) return ret;
            if (currentBlockIndex < 0)
            {
                pivotSlot = -1;
            }
            else
            {
                line.Type = LineType.Text;
                pivotSlot = lineSlot;
            }
            return 0;
        }

        // TableUnderline changes meaning of the current block.
        if (line.Type == LineType.TableUnderline)
        {
            Debug.Assert(currentBlockIndex >= 0);
            Debug.Assert(CurrentBlock.NLines == 1);
            CurrentBlock.Type = MdBlockType.Table;
            CurrentBlock.Data = line.Data;
            Debug.Assert(pivotSlot >= 0);
            lineBuf[pivotSlot].Type = LineType.Table;
            ret = AddLineIntoCurrentBlock(ref line);
            return ret;
        }

        // The current block also ends if the line has different type.
        if (line.Type != pivotType)
        {
            ret = EndCurrentBlock();
            if (ret != 0) return ret;
        }

        // The current line may start a new block.
        if (currentBlockIndex < 0)
        {
            ret = StartNewBlock(ref line);
            if (ret != 0) return ret;
            pivotSlot = lineSlot;
        }

        // In all other cases the line is just a continuation of the current block.
        ret = AddLineIntoCurrentBlock(ref line);

        return ret;
    }

    // ── Process the entire document ──────────────────────────────────────

    private int ProcessDoc()
    {
        // pivotSlot: -1 = DummyBlankLine, 0 = lineBuf[0], 1 = lineBuf[1]
        int pivotSlot = -1;
        var lineBuf = new LineAnalysis[2];
        int lineSlot = 0;
        int off = 0;
        int ret = 0;

        ret = EnterBlock(MdBlockType.Doc, null);
        if (ret != 0) return ret;

        while (off < size)
        {
            // If pivot points to the same buffer slot as the current line, switch.
            if (lineSlot == pivotSlot)
                lineSlot = lineSlot == 0 ? 1 : 0;

            // Build a pivot analysis to pass by ref. AnalyzeLine may reset it to DummyBlankLine.
            var pivotCopy = pivotSlot < 0 ? DummyBlankLine : lineBuf[pivotSlot];

            ret = AnalyzeLine(off, out off, ref pivotCopy, ref lineBuf[lineSlot]);
            if (ret != 0) goto abort;

            // If AnalyzeLine changed pivot to DummyBlankLine, reflect that.
            if (pivotCopy.Type == LineType.Blank && pivotSlot >= 0 &&
                lineBuf[pivotSlot].Type != LineType.Blank)
            {
                // pivotLine was reset to DummyBlankLine inside AnalyzeLine.
                pivotSlot = -1;
            }
            else if (pivotSlot >= 0)
            {
                // Write back any changes AnalyzeLine made to the pivot.
                lineBuf[pivotSlot] = pivotCopy;
            }

            ret = ProcessLine(lineBuf, lineSlot, ref pivotSlot);
            if (ret != 0) goto abort;
        }

        EndCurrentBlock();

        ret = BuildRefDefHashtable();
        if (ret != 0) goto abort;

        ret = LeaveChildContainers(0);
        if (ret != 0) goto abort;
        ret = ProcessAllBlocks();
        if (ret != 0) goto abort;

        {
            int r = LeaveBlock(MdBlockType.Doc, null);
            if (ret == 0) ret = r;
        }

    abort:
        return ret;
    }

    // ── Table alignment analysis ─────────────────────────────────────────

    private void AnalyzeTableAlignment(int beg, int end, MdAlign[] align, int colCount)
    {
        int off = beg;
        int k = 0;

        while (off < end && Md4cUnicode.IsWhitespace(text[off]))
            off++;
        if (off < end && text[off] == '|')
            off++;

        while (k < colCount)
        {
            while (off < end && Md4cUnicode.IsWhitespace(text[off]))
                off++;

            bool hasLeft = off < end && text[off] == ':';
            if (hasLeft) off++;

            while (off < end && text[off] == '-')
                off++;

            bool hasRight = off < end && text[off] == ':';
            if (hasRight) off++;

            if (hasLeft && hasRight)
                align[k] = MdAlign.Center;
            else if (hasLeft)
                align[k] = MdAlign.Left;
            else if (hasRight)
                align[k] = MdAlign.Right;
            else
                align[k] = MdAlign.Default;

            k++;

            while (off < end && Md4cUnicode.IsWhitespace(text[off]))
                off++;
            if (off < end && text[off] == '|')
                off++;
        }
    }

}
