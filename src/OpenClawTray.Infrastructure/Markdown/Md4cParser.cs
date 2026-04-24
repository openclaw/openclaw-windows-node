// C# port of Martin Mitáš's md4c Markdown parser.
// Ported from md4c/src/md4c.c
//
// AI-HINT: Faithful C# port of md4c — a CommonMark-compliant SAX-style Markdown parser.
// Split across 3 partial files: Md4cParser.cs (orchestration, core structs),
// Md4cParser.Block.cs (block-level parsing: headings, lists, code blocks, HTML blocks),
// Md4cParser.Inline.cs (inline parsing: emphasis, links, code spans, autolinks).
// The parser works in phases: lines → blocks → inlines → callbacks.
// Mark struct tracks potential opener/closer positions for inline delimiters.
// Uses flag bitmasks extensively (MARK_OPENER, MARK_CLOSER, etc.).

namespace OpenClawTray.Infrastructure.Markdown;

/// <summary>
/// SAX-style Markdown parser ported from md4c.
/// Call <see cref="Parse"/> with text and callbacks to parse a Markdown document.
/// </summary>
internal sealed partial class Md4cParser
{
    // ── Internal limits ──────────────────────────────────────────────────
    private const int CODESPAN_MARK_MAXLEN = 32;
    private const int TABLE_MAXCOLCOUNT = 128;

    // ── Internal types ───────────────────────────────────────────────────

    private struct Mark
    {
        public int Beg;
        public int End;
        public int Prev;
        public int Next;
        public char Ch;
        public byte Flags;
    }

    // Mark flags (apply to ALL mark types).
    private const byte MARK_POTENTIAL_OPENER = 0x01;
    private const byte MARK_POTENTIAL_CLOSER = 0x02;
    private const byte MARK_OPENER           = 0x04;
    private const byte MARK_CLOSER           = 0x08;
    private const byte MARK_RESOLVED         = 0x10;

    // Mark flags specific to various mark types (share bits).
    private const byte MARK_EMPH_OC                 = 0x20;
    private const byte MARK_EMPH_MOD3_0             = 0x40;
    private const byte MARK_EMPH_MOD3_1             = 0x80;
    private const byte MARK_EMPH_MOD3_2             = 0x40 | 0x80;
    private const byte MARK_EMPH_MOD3_MASK          = 0x40 | 0x80;
    private const byte MARK_AUTOLINK                = 0x20;
    private const byte MARK_AUTOLINK_MISSING_MAILTO = 0x40;
    private const byte MARK_VALIDPERMISSIVEAUTOLINK = 0x20;
    private const byte MARK_HASNESTEDBRACKETS       = 0x20;

    private struct MarkStack
    {
        public int Top;
    }

    private enum LineType
    {
        Blank,
        Hr,
        AtxHeader,
        SetextHeader,
        SetextUnderline,
        IndentedCode,
        FencedCode,
        Html,
        Text,
        Table,
        TableUnderline,
    }

    private struct LineAnalysis
    {
        public LineType Type;
        public int Data;
        public bool EnforceNewBlock;
        public int Beg;
        public int End;
        public int Indent;
    }

    private struct Line
    {
        public int Beg;
        public int End;
    }

    private struct VerbatimLine
    {
        public int Beg;
        public int End;
        public int Indent;
    }

    private const byte BLOCK_CONTAINER_OPENER  = 0x01;
    private const byte BLOCK_CONTAINER_CLOSER  = 0x02;
    private const byte BLOCK_CONTAINER         = BLOCK_CONTAINER_OPENER | BLOCK_CONTAINER_CLOSER;
    private const byte BLOCK_LOOSE_LIST        = 0x04;
    private const byte BLOCK_SETEXT_HEADER     = 0x08;

    private struct Block
    {
        public MdBlockType Type;
        public byte Flags;
        public int Data;
        public int NLines;
    }

    private struct Container
    {
        public char Ch;
        public bool IsLoose;
        public bool IsTask;
        public int Start;
        public int MarkIndent;
        public int ContentIndent;
        public int BlockIndex; // Index into blocks list (replaces block_byte_off)
        public int TaskMarkOff;
    }

    private struct RefDef
    {
        public string Label;
        public string? Title;
        public uint Hash;
        public int DestBeg;
        public int DestEnd;
    }

    private struct LinkAttr
    {
        public int DestBeg;
        public int DestEnd;
        public string? Title;
    }

    private struct AttributeBuild
    {
        public char[]? Text;
        public MdTextType[]? SubstrTypes;
        public int[]? SubstrOffsets;
        public int SubstrCount;
    }

    // ── Context fields (the full parser state) ───────────────────────────

    // Immutable: parameters of Parse()
    private readonly string text;
    private readonly int size;
    private readonly MdParserFlags flags;
    private readonly MdEnterBlockCallback enterBlock;
    private readonly MdLeaveBlockCallback leaveBlock;
    private readonly MdEnterSpanCallback enterSpan;
    private readonly MdLeaveSpanCallback leaveSpan;
    private readonly MdTextCallback textCb;
    private readonly MdDebugLogCallback? debugLog;

    // Optimization hint
    private readonly bool docEndsWithNewline;

    // Helper temporary growing buffer.
    private char[] buffer = Array.Empty<char>();

    // Reference definitions.
    private List<RefDef> refDefs = new();
    private Dictionary<uint, List<RefDef>>? refDefHashtable;
    private long maxRefDefOutput;

    // Stack of inline/span markers.
    private Mark[] marks = new Mark[64];
    private int nMarks;

    // Mark character map (which chars trigger mark collection).
    private readonly bool[] markCharMap = new bool[256];

    // Opener stacks for inline resolution.
    private readonly MarkStack[] openerStacks = new MarkStack[16];

    // Opener stack indices (named for clarity).
    private ref MarkStack AsteriskOpenersOoMod3_0 => ref openerStacks[0];
    private ref MarkStack AsteriskOpenersOoMod3_1 => ref openerStacks[1];
    private ref MarkStack AsteriskOpenersOoMod3_2 => ref openerStacks[2];
    private ref MarkStack AsteriskOpenersOcMod3_0 => ref openerStacks[3];
    private ref MarkStack AsteriskOpenersOcMod3_1 => ref openerStacks[4];
    private ref MarkStack AsteriskOpenersOcMod3_2 => ref openerStacks[5];
    private ref MarkStack UnderscoreOpenersOoMod3_0 => ref openerStacks[6];
    private ref MarkStack UnderscoreOpenersOoMod3_1 => ref openerStacks[7];
    private ref MarkStack UnderscoreOpenersOoMod3_2 => ref openerStacks[8];
    private ref MarkStack UnderscoreOpenersOcMod3_0 => ref openerStacks[9];
    private ref MarkStack UnderscoreOpenersOcMod3_1 => ref openerStacks[10];
    private ref MarkStack UnderscoreOpenersOcMod3_2 => ref openerStacks[11];
    private ref MarkStack TildeOpeners1 => ref openerStacks[12];
    private ref MarkStack TildeOpeners2 => ref openerStacks[13];
    private ref MarkStack BracketOpeners => ref openerStacks[14];
    private ref MarkStack DollarOpeners => ref openerStacks[15];

    // Stack of dummies needing cleanup.
    private MarkStack ptrStack;

    // For resolving table rows.
    private int nTableCellBoundaries;
    private int tableCellBoundariesHead;
    private int tableCellBoundariesTail;

    // For resolving links.
    private int unresolvedLinkHead;
    private int unresolvedLinkTail;

    // For resolving raw HTML.
    private int htmlCommentHorizon;
    private int htmlProcInstrHorizon;
    private int htmlDeclHorizon;
    private int htmlCdataHorizon;

    // For block analysis.
    private byte[] blockBytes = new byte[256];
    private int nBlockBytes;
    private int currentBlockIndex = -1;

    // For container block analysis.
    private Container[] containers = new Container[16];
    private int nContainers;

    // Minimal indentation for indented code blocks.
    private readonly int codeIndentOffset;

    // Contextual info for line analysis.
    private int codeFenceLength;
    private int htmlBlockType;
    private bool lastLineHasListLooseningEffect;
    private bool lastListItemStartsWithTwoBlankLines;

    // ── Constructor (private — use Parse()) ──────────────────────────────

    private Md4cParser(
        string text,
        MdParserFlags flags,
        MdEnterBlockCallback enterBlock,
        MdLeaveBlockCallback leaveBlock,
        MdEnterSpanCallback enterSpan,
        MdLeaveSpanCallback leaveSpan,
        MdTextCallback textCb,
        MdDebugLogCallback? debugLog)
    {
        this.text = text;
        this.size = text.Length;
        this.flags = flags;
        this.enterBlock = enterBlock;
        this.leaveBlock = leaveBlock;
        this.enterSpan = enterSpan;
        this.leaveSpan = leaveSpan;
        this.textCb = textCb;
        this.debugLog = debugLog;

        codeIndentOffset = (flags & MdParserFlags.NoIndentedCodeBlocks) != 0 ? -1 : 4;
        docEndsWithNewline = size > 0 && Md4cUnicode.IsNewline(text[size - 1]);
        maxRefDefOutput = Math.Min(Math.Min(16L * size, 1024 * 1024L), int.MaxValue);

        BuildMarkCharMap();

        // Reset all mark stacks.
        for (int i = 0; i < openerStacks.Length; i++)
            openerStacks[i].Top = -1;
        ptrStack.Top = -1;
        unresolvedLinkHead = -1;
        unresolvedLinkTail = -1;
        tableCellBoundariesHead = -1;
        tableCellBoundariesTail = -1;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Parse a Markdown document and invoke callbacks for each structural element.
    /// Returns 0 on success, -1 on error, or a non-zero callback return value.
    /// </summary>
    public static int Parse(
        string text,
        MdParserFlags flags,
        MdEnterBlockCallback enterBlock,
        MdLeaveBlockCallback leaveBlock,
        MdEnterSpanCallback enterSpan,
        MdLeaveSpanCallback leaveSpan,
        MdTextCallback textCb,
        MdDebugLogCallback? debugLog = null)
    {
        var parser = new Md4cParser(text, flags, enterBlock, leaveBlock, enterSpan, leaveSpan, textCb, debugLog);
        return parser.ProcessDoc();
    }

    // ── Character accessors ──────────────────────────────────────────────

    private char CH(int off) => text[off];

    // ── Logging ──────────────────────────────────────────────────────────

    private void Log(string msg)
    {
        debugLog?.Invoke(msg);
    }

    // ── Callback helpers ─────────────────────────────────────────────────

    private int EnterBlock(MdBlockType type, object? detail)
    {
        int ret = enterBlock(type, detail);
        if (ret != 0) Log("Aborted from enter_block() callback.");
        return ret;
    }

    private int LeaveBlock(MdBlockType type, object? detail)
    {
        int ret = leaveBlock(type, detail);
        if (ret != 0) Log("Aborted from leave_block() callback.");
        return ret;
    }

    private int EnterSpan(MdSpanType type, object? detail)
    {
        int ret = enterSpan(type, detail);
        if (ret != 0) Log("Aborted from enter_span() callback.");
        return ret;
    }

    private int LeaveSpan(MdSpanType type, object? detail)
    {
        int ret = leaveSpan(type, detail);
        if (ret != 0) Log("Aborted from leave_span() callback.");
        return ret;
    }

    private int Text(MdTextType type, int off, int len)
    {
        if (len > 0)
        {
            int ret = textCb(type, text.AsSpan(off, len));
            if (ret != 0) { Log("Aborted from text() callback."); return ret; }
        }
        return 0;
    }

    private int TextBuf(MdTextType type, ReadOnlySpan<char> buf)
    {
        if (buf.Length > 0)
        {
            int ret = textCb(type, buf);
            if (ret != 0) { Log("Aborted from text() callback."); return ret; }
        }
        return 0;
    }

    private int TextInsecure(MdTextType type, int off, int len)
    {
        return TextWithNullReplacement(type, off, len);
    }

    private int TextWithNullReplacement(MdTextType type, int off, int len)
    {
        int end = off + len;
        int segStart = off;
        while (off < end)
        {
            if (text[off] == '\0')
            {
                if (off > segStart)
                {
                    int ret = textCb(type, text.AsSpan(segStart, off - segStart));
                    if (ret != 0) return ret;
                }
                int ret2 = textCb(MdTextType.NullChar, "\0".AsSpan());
                if (ret2 != 0) return ret2;
                off++;
                segStart = off;
            }
            else
            {
                off++;
            }
        }
        if (off > segStart)
        {
            int ret = textCb(type, text.AsSpan(segStart, off - segStart));
            if (ret != 0) return ret;
        }
        return 0;
    }

    // ── Temp buffer ──────────────────────────────────────────────────────

    private void EnsureBuffer(int needed)
    {
        if (needed > buffer.Length)
        {
            int newSize = ((needed + needed / 2 + 128) & ~127);
            Array.Resize(ref buffer, newSize);
        }
    }

    // ── Mark management ──────────────────────────────────────────────────

    private int AddMark(char ch, int beg, int end, byte markFlags)
    {
        if (nMarks >= marks.Length)
            Array.Resize(ref marks, marks.Length + marks.Length / 2);

        int idx = nMarks++;
        ref Mark mark = ref marks[idx];
        mark.Beg = beg;
        mark.End = end;
        mark.Prev = -1;
        mark.Next = -1;
        mark.Ch = ch;
        mark.Flags = markFlags;
        return idx;
    }

    private void MarkStackPush(ref MarkStack stack, int markIndex)
    {
        marks[markIndex].Next = stack.Top;
        stack.Top = markIndex;
    }

    private int MarkStackPop(ref MarkStack stack)
    {
        int top = stack.Top;
        if (top >= 0)
            stack.Top = marks[top].Next;
        return top;
    }

    private void ResolveRange(int openerIndex, int closerIndex)
    {
        ref Mark opener = ref marks[openerIndex];
        ref Mark closer = ref marks[closerIndex];

        opener.Next = closerIndex;
        closer.Prev = openerIndex;

        opener.Flags |= MARK_OPENER | MARK_RESOLVED;
        closer.Flags |= MARK_CLOSER | MARK_RESOLVED;
    }

    private const int ROLLBACK_CROSSING = 0;
    private const int ROLLBACK_ALL = 1;

    private void Rollback(int openerIndex, int closerIndex, int how)
    {
        for (int i = 0; i < openerStacks.Length; i++)
        {
            ref MarkStack stack = ref openerStacks[i];
            while (stack.Top >= openerIndex)
                MarkStackPop(ref stack);
        }

        if (how == ROLLBACK_ALL)
        {
            for (int i = openerIndex + 1; i < closerIndex; i++)
            {
                marks[i].Ch = 'D';
                marks[i].Flags = 0;
            }
        }
    }

    // Store/retrieve a string pointer in a dummy mark's Beg/End fields.
    // In C, this uses memcpy to store a void* in beg/end.
    // In C#, we use a side dictionary instead.
    private readonly Dictionary<int, string> markPtrStore = new();

    private void MarkStorePtr(int markIndex, string ptr)
    {
        markPtrStore[markIndex] = ptr;
    }

    private string? MarkGetPtr(int markIndex)
    {
        return markPtrStore.GetValueOrDefault(markIndex);
    }

    private void BuildMarkCharMap()
    {
        Array.Clear(markCharMap);

        markCharMap['\\'] = true;
        markCharMap['*'] = true;
        markCharMap['_'] = true;
        markCharMap['`'] = true;
        markCharMap['&'] = true;
        markCharMap[';'] = true;
        markCharMap['<'] = true;
        markCharMap['>'] = true;
        markCharMap['['] = true;
        markCharMap['!'] = true;
        markCharMap[']'] = true;
        // '\0' is not a valid char in C# strings, skip it.

        if ((flags & MdParserFlags.Strikethrough) != 0)
            markCharMap['~'] = true;

        if ((flags & MdParserFlags.LatexMathSpans) != 0)
            markCharMap['$'] = true;

        if ((flags & MdParserFlags.PermissiveEmailAutolinks) != 0)
            markCharMap['@'] = true;

        if ((flags & MdParserFlags.PermissiveUrlAutolinks) != 0)
            markCharMap[':'] = true;

        if ((flags & MdParserFlags.PermissiveWwwAutolinks) != 0)
            markCharMap['.'] = true;

        if ((flags & (MdParserFlags.Tables | MdParserFlags.WikiLinks)) != 0)
            markCharMap['|'] = true;

        if ((flags & MdParserFlags.CollapseWhitespace) != 0)
        {
            for (int i = 0; i < markCharMap.Length; i++)
            {
                if (Md4cUnicode.IsWhitespace((char)i))
                    markCharMap[i] = true;
            }
        }
    }

    private bool IsMarkChar(int off)
    {
        char ch = text[off];
        return ch < markCharMap.Length && markCharMap[ch];
    }

    // ── Emphasis stack selection ─────────────────────────────────────────

    private ref MarkStack EmphStack(char ch, byte markFlags)
    {
        int baseIdx = ch == '*' ? 0 : 6; // underscore starts at 6
        if ((markFlags & MARK_EMPH_OC) != 0)
            baseIdx += 3;

        int mod3 = (markFlags & MARK_EMPH_MOD3_MASK) switch
        {
            MARK_EMPH_MOD3_0 => 0,
            MARK_EMPH_MOD3_1 => 1,
            MARK_EMPH_MOD3_2 => 2,
            _ => 0,
        };

        return ref openerStacks[baseIdx + mod3];
    }

    private ref MarkStack OpenerStack(int markIndex)
    {
        ref Mark mark = ref marks[markIndex];
        switch (mark.Ch)
        {
            case '*':
            case '_':
                return ref EmphStack(mark.Ch, mark.Flags);
            case '~':
                if (mark.End - mark.Beg == 1)
                    return ref TildeOpeners1;
                else
                    return ref TildeOpeners2;
            case '!':
            case '[':
                return ref BracketOpeners;
            default:
                return ref BracketOpeners; // unreachable
        }
    }

    // ── String helpers ───────────────────────────────────────────────────

    private bool AsciiCaseEq(int off1, string s2, int len)
    {
        for (int i = 0; i < len; i++)
        {
            char ch1 = char.ToUpperInvariant(text[off1 + i]);
            char ch2 = char.ToUpperInvariant(s2[i]);
            if (ch1 != ch2) return false;
        }
        return true;
    }

    private static bool AsciiCaseEq(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2)
    {
        if (s1.Length != s2.Length) return false;
        for (int i = 0; i < s1.Length; i++)
        {
            if (char.ToUpperInvariant(s1[i]) != char.ToUpperInvariant(s2[i]))
                return false;
        }
        return true;
    }

    private bool AsciiEq(int off, string s, int len)
    {
        return text.AsSpan(off, len).SequenceEqual(s.AsSpan(0, len));
    }

    /// <summary>
    /// Copy text[beg..end] into buffer, replacing line breaks between lines with replacement char.
    /// Returns the output length.
    /// </summary>
    private int MergeLines(int beg, int end, Line[] lines, int nLines, int lineOffset, char replacement, char[] outBuf)
    {
        int ptr = 0;
        int lineIndex = 0;
        int off = beg;

        while (true)
        {
            ref Line line = ref lines[lineOffset + lineIndex];
            int lineEnd = Math.Min(line.End, end);

            while (off < lineEnd)
            {
                outBuf[ptr++] = text[off++];
            }

            if (off >= end)
                return ptr;

            outBuf[ptr++] = replacement;
            lineIndex++;
            off = lines[lineOffset + lineIndex].Beg;
        }
    }

    private string MergeLinesAlloc(int beg, int end, Line[] lines, int nLines, int lineOffset, char replacement)
    {
        char[] buf = new char[end - beg];
        int len = MergeLines(beg, end, lines, nLines, lineOffset, replacement, buf);
        return new string(buf, 0, len);
    }

    private int SkipUnicodeWhitespace(string label, int off, int labelSize)
    {
        while (off < labelSize)
        {
            uint cp = Md4cUnicode.DecodeUnicode(label, off, labelSize, out int charSize);
            if (!Md4cUnicode.IsUnicodeWhitespace(cp) && !Md4cUnicode.IsNewline(label[off]))
                break;
            off += charSize;
        }
        return off;
    }

    // ── Line lookup (binary search) ──────────────────────────────────────

    private int LookupLineIndex(int off, Line[] lines, int nLines, int lineOffset)
    {
        int lo = 0, hi = nLines - 1;
        while (lo <= hi)
        {
            int pivot = (lo + hi) / 2;
            ref Line line = ref lines[lineOffset + pivot];
            if (off < line.Beg)
            {
                if (hi == 0 || lines[lineOffset + hi - 1].End < off)
                    return pivot;
                hi = pivot - 1;
            }
            else if (off > line.End)
            {
                lo = pivot + 1;
            }
            else
            {
                return pivot;
            }
        }
        return -1;
    }

    // ── Block bytes management ───────────────────────────────────────────

    private int AllocBlockBytes(int size)
    {
        int needed = nBlockBytes + size;
        if (needed > blockBytes.Length)
        {
            int newSize = Math.Max(needed + needed / 2, 256);
            Array.Resize(ref blockBytes, newSize);
        }
        int offset = nBlockBytes;
        nBlockBytes += size;
        return offset;
    }

    // We store Block and Line structs in blockBytes as indices.
    // For simplicity in C#, we use separate lists instead.
    private readonly List<Block> blocks = new();
    private readonly List<Line> blockLines = new();
    private readonly List<VerbatimLine> blockVerbatimLines = new();

    // ── Dummy blank line analysis (used as sentinel) ─────────────────────

    private static readonly LineAnalysis DummyBlankLine = new() { Type = LineType.Blank };
}
