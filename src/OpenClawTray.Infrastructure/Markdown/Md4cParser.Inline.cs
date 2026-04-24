// C# port of Martin Mitas's md4c Markdown parser inline processing.
// Ported from md4c/src/md4c.c
//
// AI-HINT: Inline-level parsing: emphasis (*, _, ~), code spans, links, images, autolinks,
// HTML entities, raw HTML. Uses a Mark-based approach: first pass collects potential
// opener/closer marks, second pass resolves them using delimiter stack rules from
// CommonMark spec §6. FNV1A hashing used for reference link label matching.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenClawTray.Infrastructure.Markdown;

internal sealed partial class Md4cParser
{
    // ── Constants ────────────────────────────────────────────────────────

    private const uint FNV1A_BASE  = 2166136261U;
    private const uint FNV1A_PRIME = 16777619U;

    private const int BUILD_ATTR_NO_ESCAPES = 0x0001;

    private const int ANALYZE_NOSKIP_EMPH = 0x01;

    private static readonly (string scheme, string suffix)[] s_autolinkSchemes =
    {
        ("http", "//"),
        ("https", "//"),
        ("ftp", "//"),
    };

    // ── Raw HTML recognition ────────────────────────────────────────────

    /// <summary>
    /// Recognizes an HTML tag starting at <paramref name="beg"/>.
    /// When <paramref name="nLines"/> == 0 we are in block analysis mode
    /// (single line).
    /// </summary>
    private bool IsHtmlTag(Line[] lines, int nLines, int lineOffset,
                           int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        int attrState;
        int off = beg;
        int lineEnd = (nLines > 0) ? lines[lineOffset].End : size;
        int lineIndex = 0;

        Debug.Assert(text[beg] == '<');

        if (off + 1 >= lineEnd)
            return false;
        off++;

        // Attribute parsing state automaton:
        // -1: no attributes allowed
        //  0: attribute could follow after whitespace
        //  1: after whitespace (attribute name may follow)
        //  2: after attribute name ('=' may follow)
        //  3: after '=' (value must follow)
        // 41: unquoted attribute value
        // 42: single-quoted attribute value
        // 43: double-quoted attribute value
        attrState = 0;

        if (text[off] == '/')
        {
            attrState = -1;
            off++;
        }

        // Tag name
        if (off >= lineEnd || !Md4cUnicode.IsAlpha(text[off]))
            return false;
        off++;
        while (off < lineEnd && (Md4cUnicode.IsAlNum(text[off]) || text[off] == '-'))
            off++;

        while (true)
        {
            while (off < lineEnd && !Md4cUnicode.IsNewline(text[off]))
            {
                if (attrState > 40)
                {
                    if (attrState == 41 && (Md4cUnicode.IsBlank(text[off]) || IsAnyOf(text[off], "\"'=<>`")))
                    {
                        attrState = 0;
                        off--;
                    }
                    else if (attrState == 42 && text[off] == '\'')
                    {
                        attrState = 0;
                    }
                    else if (attrState == 43 && text[off] == '"')
                    {
                        attrState = 0;
                    }
                    off++;
                }
                else if (Md4cUnicode.IsWhitespace(text[off]))
                {
                    if (attrState == 0)
                        attrState = 1;
                    off++;
                }
                else if (attrState <= 2 && text[off] == '>')
                {
                    goto done;
                }
                else if (attrState <= 2 && text[off] == '/' && off + 1 < lineEnd && text[off + 1] == '>')
                {
                    off++;
                    goto done;
                }
                else if ((attrState == 1 || attrState == 2) &&
                         (Md4cUnicode.IsAlpha(text[off]) || text[off] == '_' || text[off] == ':'))
                {
                    off++;
                    while (off < lineEnd && (Md4cUnicode.IsAlNum(text[off]) || IsAnyOf(text[off], "_.:-")))
                        off++;
                    attrState = 2;
                }
                else if (attrState == 2 && text[off] == '=')
                {
                    off++;
                    attrState = 3;
                }
                else if (attrState == 3)
                {
                    if (text[off] == '"')
                        attrState = 43;
                    else if (text[off] == '\'')
                        attrState = 42;
                    else if (!IsAnyOf(text[off], "\"'=<>`") && !Md4cUnicode.IsNewline(text[off]))
                        attrState = 41;
                    else
                        return false;
                    off++;
                }
                else
                {
                    return false;
                }
            }

            if (nLines == 0)
                return false;

            lineIndex++;
            if (lineIndex >= nLines)
                return false;

            off = lines[lineOffset + lineIndex].Beg;
            lineEnd = lines[lineOffset + lineIndex].End;

            if (attrState == 0 || attrState == 41)
                attrState = 1;

            if (off >= maxEnd)
                return false;
        }

    done:
        if (off >= maxEnd)
            return false;

        pEnd = off + 1;
        return true;
    }

    private bool ScanForHtmlCloser(string closerStr, int closerLen,
                                   Line[] lines, int nLines, int lineOffset,
                                   int beg, int maxEnd, out int pEnd,
                                   ref int scanHorizon)
    {
        pEnd = 0;
        int off = beg;
        int lineIndex = 0;

        if (off < scanHorizon && scanHorizon >= maxEnd - closerLen)
            return false;

        while (true)
        {
            while (off + closerLen <= lines[lineOffset + lineIndex].End && off + closerLen <= maxEnd)
            {
                if (AsciiEq(off, closerStr, closerLen))
                {
                    pEnd = off + closerLen;
                    return true;
                }
                off++;
            }

            lineIndex++;
            if (off >= maxEnd || lineIndex >= nLines)
            {
                scanHorizon = off;
                return false;
            }

            off = lines[lineOffset + lineIndex].Beg;
        }
    }

    private bool IsHtmlComment(Line[] lines, int nLines, int lineOffset,
                               int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        int off = beg;

        Debug.Assert(text[beg] == '<');

        if (off + 4 >= lines[lineOffset].End)
            return false;
        if (text[off + 1] != '!' || text[off + 2] != '-' || text[off + 3] != '-')
            return false;

        off += 2;

        return ScanForHtmlCloser("-->", 3, lines, nLines, lineOffset,
                                 off, maxEnd, out pEnd, ref htmlCommentHorizon);
    }

    private bool IsHtmlProcessingInstruction(Line[] lines, int nLines, int lineOffset,
                                              int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        int off = beg;

        if (off + 2 >= lines[lineOffset].End)
            return false;
        if (text[off + 1] != '?')
            return false;
        off += 2;

        return ScanForHtmlCloser("?>", 2, lines, nLines, lineOffset,
                                 off, maxEnd, out pEnd, ref htmlProcInstrHorizon);
    }

    private bool IsHtmlDeclaration(Line[] lines, int nLines, int lineOffset,
                                    int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        int off = beg;

        if (off + 2 >= lines[lineOffset].End)
            return false;
        if (text[off + 1] != '!')
            return false;
        off += 2;

        if (off >= lines[lineOffset].End || !Md4cUnicode.IsAlpha(text[off]))
            return false;
        off++;
        while (off < lines[lineOffset].End && Md4cUnicode.IsAlpha(text[off]))
            off++;

        return ScanForHtmlCloser(">", 1, lines, nLines, lineOffset,
                                 off, maxEnd, out pEnd, ref htmlDeclHorizon);
    }

    private bool IsHtmlCdata(Line[] lines, int nLines, int lineOffset,
                              int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        const string openStr = "<![CDATA[";
        int off = beg;

        if (off + openStr.Length >= lines[lineOffset].End)
            return false;
        if (!AsciiEq(off, openStr, openStr.Length))
            return false;
        off += openStr.Length;

        return ScanForHtmlCloser("]]>", 3, lines, nLines, lineOffset,
                                 off, maxEnd, out pEnd, ref htmlCdataHorizon);
    }

    private bool IsHtmlAny(Line[] lines, int nLines, int lineOffset,
                            int beg, int maxEnd, out int pEnd)
    {
        Debug.Assert(text[beg] == '<');
        return IsHtmlTag(lines, nLines, lineOffset, beg, maxEnd, out pEnd) ||
               IsHtmlComment(lines, nLines, lineOffset, beg, maxEnd, out pEnd) ||
               IsHtmlProcessingInstruction(lines, nLines, lineOffset, beg, maxEnd, out pEnd) ||
               IsHtmlDeclaration(lines, nLines, lineOffset, beg, maxEnd, out pEnd) ||
               IsHtmlCdata(lines, nLines, lineOffset, beg, maxEnd, out pEnd);
    }

    // ── Entity recognition ──────────────────────────────────────────────

    private static bool IsHexEntityContents(string txt, int beg, int maxEnd, out int pEnd)
    {
        pEnd = beg;
        int off = beg;

        while (off < maxEnd && Md4cUnicode.IsXDigit(txt[off]) && off - beg <= 8)
            off++;

        if (1 <= off - beg && off - beg <= 6)
        {
            pEnd = off;
            return true;
        }
        return false;
    }

    private static bool IsDecEntityContents(string txt, int beg, int maxEnd, out int pEnd)
    {
        pEnd = beg;
        int off = beg;

        while (off < maxEnd && Md4cUnicode.IsDigit(txt[off]) && off - beg <= 8)
            off++;

        if (1 <= off - beg && off - beg <= 7)
        {
            pEnd = off;
            return true;
        }
        return false;
    }

    private static bool IsNamedEntityContents(string txt, int beg, int maxEnd, out int pEnd)
    {
        pEnd = beg;
        int off = beg;

        if (off < maxEnd && Md4cUnicode.IsAlpha(txt[off]))
            off++;
        else
            return false;

        while (off < maxEnd && Md4cUnicode.IsAlNum(txt[off]) && off - beg <= 48)
            off++;

        if (2 <= off - beg && off - beg <= 48)
        {
            pEnd = off;
            return true;
        }
        return false;
    }

    private static bool IsEntityStr(string txt, int beg, int maxEnd, out int pEnd)
    {
        pEnd = beg;
        int off = beg;
        bool isContents;

        Debug.Assert(txt[off] == '&');
        off++;

        if (off + 2 < maxEnd && txt[off] == '#' && (txt[off + 1] == 'x' || txt[off + 1] == 'X'))
            isContents = IsHexEntityContents(txt, off + 2, maxEnd, out off);
        else if (off + 1 < maxEnd && txt[off] == '#')
            isContents = IsDecEntityContents(txt, off + 1, maxEnd, out off);
        else
            isContents = IsNamedEntityContents(txt, off, maxEnd, out off);

        if (isContents && off < maxEnd && txt[off] == ';')
        {
            pEnd = off + 1;
            return true;
        }
        return false;
    }

    private bool IsEntity(int beg, int maxEnd, out int pEnd)
    {
        return IsEntityStr(text, beg, maxEnd, out pEnd);
    }

    // ── Attribute management ────────────────────────────────────────────

    private static void BuildAttrAppendSubstr(ref AttributeBuild build, MdTextType type, int off)
    {
        if (build.SubstrTypes == null || build.SubstrCount >= build.SubstrTypes.Length)
        {
            int newAlloc = build.SubstrTypes != null
                ? build.SubstrTypes.Length + build.SubstrTypes.Length / 2
                : 8;
            var newTypes = new MdTextType[newAlloc];
            var newOffsets = new int[newAlloc + 1];
            if (build.SubstrTypes != null)
            {
                Array.Copy(build.SubstrTypes, newTypes, build.SubstrCount);
                Array.Copy(build.SubstrOffsets!, newOffsets, build.SubstrCount);
            }
            build.SubstrTypes = newTypes;
            build.SubstrOffsets = newOffsets;
        }

        build.SubstrTypes[build.SubstrCount] = type;
        build.SubstrOffsets![build.SubstrCount] = off;
        build.SubstrCount++;
    }

    private int BuildAttribute(string rawText, int rawSize, int attrFlags,
                               out MdAttribute attr, ref AttributeBuild build)
    {
        attr = default;
        build = default;
        int rawOff, off;
        // If there is no backslash and no ampersand, build trivial attribute.
        bool isTrivial = true;
        for (rawOff = 0; rawOff < rawSize; rawOff++)
        {
            char ch = rawText[rawOff];
            if (ch == '\\' || ch == '&' || ch == '\0')
            {
                isTrivial = false;
                break;
            }
        }

        if (isTrivial)
        {
            build.Text = rawSize > 0 ? rawText.ToCharArray(0, rawSize) : null;
            build.SubstrTypes = new[] { MdTextType.Normal };
            build.SubstrOffsets = new[] { 0, rawSize };
            build.SubstrCount = 1;
            off = rawSize;
        }
        else
        {
            build.Text = new char[rawSize];

            rawOff = 0;
            off = 0;

            while (rawOff < rawSize)
            {
                if (rawText[rawOff] == '\0')
                {
                    BuildAttrAppendSubstr(ref build, MdTextType.NullChar, off);
                    build.Text![off] = rawText[rawOff];
                    off++;
                    rawOff++;
                    continue;
                }

                if (rawText[rawOff] == '&')
                {
                    if (IsEntityStr(rawText, rawOff, rawSize, out int entEnd))
                    {
                        BuildAttrAppendSubstr(ref build, MdTextType.Entity, off);
                        rawText.CopyTo(rawOff, build.Text!, off, entEnd - rawOff);
                        off += entEnd - rawOff;
                        rawOff = entEnd;
                        continue;
                    }
                }

                if (build.SubstrCount == 0 || build.SubstrTypes![build.SubstrCount - 1] != MdTextType.Normal)
                    BuildAttrAppendSubstr(ref build, MdTextType.Normal, off);

                if ((attrFlags & BUILD_ATTR_NO_ESCAPES) == 0 &&
                    rawText[rawOff] == '\\' && rawOff + 1 < rawSize &&
                    (Md4cUnicode.IsPunct(rawText[rawOff + 1]) || Md4cUnicode.IsNewline(rawText[rawOff + 1])))
                {
                    rawOff++;
                }

                build.Text![off++] = rawText[rawOff++];
            }
            build.SubstrOffsets![build.SubstrCount] = off;
        }

        attr = new MdAttribute(
            build.Text != null ? new string(build.Text, 0, off) : "",
            build.SubstrTypes!,
            build.SubstrOffsets!);
        return 0;
    }

    // ── FNV1A hash ──────────────────────────────────────────────────────

    private static uint Fnv1a(uint hash, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= FNV1A_PRIME;
        }
        return hash;
    }

    private static uint Fnv1aCodepoints(uint hash, uint[] codepoints, int count)
    {
        for (int i = 0; i < count; i++)
        {
            uint cp = codepoints[i];
            hash ^= (byte)(cp);
            hash *= FNV1A_PRIME;
            hash ^= (byte)(cp >> 8);
            hash *= FNV1A_PRIME;
            hash ^= (byte)(cp >> 16);
            hash *= FNV1A_PRIME;
            hash ^= (byte)(cp >> 24);
            hash *= FNV1A_PRIME;
        }
        return hash;
    }

    // ── Reference definition dictionary ─────────────────────────────────

    private static uint LinkLabelHash(string label, int labelSize)
    {
        uint hash = FNV1A_BASE;
        int off;
        bool isWhitespace;

        off = SkipUnicodeWhitespaceStatic(label, 0, labelSize);
        while (off < labelSize)
        {
            uint codepoint = Md4cUnicode.DecodeUnicode(label, off, labelSize, out int charSize);
            isWhitespace = Md4cUnicode.IsUnicodeWhitespace(codepoint) || Md4cUnicode.IsNewline(label[off]);

            if (isWhitespace)
            {
                uint spCp = ' ';
                hash = Fnv1aCodepoints(hash, new[] { spCp }, 1);
                off = SkipUnicodeWhitespaceStatic(label, off, labelSize);
            }
            else
            {
                var foldInfo = new UnicodeFoldInfo { Codepoints = new uint[3] };
                Md4cUnicode.GetUnicodeFoldInfo(codepoint, ref foldInfo);
                hash = Fnv1aCodepoints(hash, foldInfo.Codepoints, foldInfo.Count);
                off += charSize;
            }
        }

        return hash;
    }

    private static int SkipUnicodeWhitespaceStatic(string label, int off, int labelSize)
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

    private static int LinkLabelCmpLoadFoldInfo(string label, int off, int labelSize,
                                                 ref UnicodeFoldInfo foldInfo)
    {
        if (off >= labelSize)
        {
            // Treat end of label as whitespace.
            foldInfo.Codepoints[0] = ' ';
            foldInfo.Count = 1;
            return SkipUnicodeWhitespaceStatic(label, off, labelSize);
        }

        uint codepoint = Md4cUnicode.DecodeUnicode(label, off, labelSize, out int charSize);
        off += charSize;
        if (Md4cUnicode.IsUnicodeWhitespace(codepoint))
        {
            foldInfo.Codepoints[0] = ' ';
            foldInfo.Count = 1;
            return SkipUnicodeWhitespaceStatic(label, off, labelSize);
        }

        Md4cUnicode.GetUnicodeFoldInfo(codepoint, ref foldInfo);
        return off;
    }

    private static int LinkLabelCmp(string aLabel, int aSize, string bLabel, int bSize)
    {
        int aOff, bOff;
        var aFi = new UnicodeFoldInfo { Codepoints = new uint[3], Count = 0 };
        var bFi = new UnicodeFoldInfo { Codepoints = new uint[3], Count = 0 };
        int aFiOff = 0;
        int bFiOff = 0;

        aOff = SkipUnicodeWhitespaceStatic(aLabel, 0, aSize);
        bOff = SkipUnicodeWhitespaceStatic(bLabel, 0, bSize);

        while (aOff < aSize || aFiOff < aFi.Count ||
               bOff < bSize || bFiOff < bFi.Count)
        {
            if (aFiOff >= aFi.Count)
            {
                aFiOff = 0;
                aOff = LinkLabelCmpLoadFoldInfo(aLabel, aOff, aSize, ref aFi);
            }
            if (bFiOff >= bFi.Count)
            {
                bFiOff = 0;
                bOff = LinkLabelCmpLoadFoldInfo(bLabel, bOff, bSize, ref bFi);
            }

            int cmp = (int)bFi.Codepoints[bFiOff] - (int)aFi.Codepoints[aFiOff];
            if (cmp != 0)
                return cmp;

            aFiOff++;
            bFiOff++;
        }

        return 0;
    }

    private int BuildRefDefHashtable()
    {
        if (refDefs.Count == 0)
            return 0;

        refDefHashtable = new Dictionary<uint, List<RefDef>>();

        for (int i = 0; i < refDefs.Count; i++)
        {
            var def = refDefs[i];
            def.Hash = LinkLabelHash(def.Label, def.Label.Length);
            refDefs[i] = def;

            if (!refDefHashtable.TryGetValue(def.Hash, out var bucket))
            {
                bucket = new List<RefDef>();
                refDefHashtable[def.Hash] = bucket;
            }

            // Check for duplicates in the bucket.
            bool isDuplicate = false;
            for (int j = 0; j < bucket.Count; j++)
            {
                if (LinkLabelCmp(def.Label, def.Label.Length,
                                 bucket[j].Label, bucket[j].Label.Length) == 0)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
                bucket.Add(def);
        }

        // Sort each bucket that has more than one entry.
        foreach (var kv in refDefHashtable)
        {
            if (kv.Value.Count > 1)
            {
                kv.Value.Sort((a, b) =>
                {
                    int cmp = LinkLabelCmp(a.Label, a.Label.Length, b.Label, b.Label.Length);
                    return cmp;
                });
            }
        }

        return 0;
    }

    private RefDef? LookupRefDef(string label, int labelSize)
    {
        if (refDefHashtable == null || refDefHashtable.Count == 0)
            return null;

        uint hash = LinkLabelHash(label, labelSize);
        if (!refDefHashtable.TryGetValue(hash, out var bucket))
            return null;

        // Binary search in the sorted bucket.
        int lo = 0, hi = bucket.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int cmp = LinkLabelCmp(bucket[mid].Label, bucket[mid].Label.Length, label, labelSize);
            if (cmp < 0)
                lo = mid + 1;
            else if (cmp > 0)
                hi = mid - 1;
            else
                return bucket[mid];
        }

        // Linear fallback.
        for (int i = 0; i < bucket.Count; i++)
        {
            if (LinkLabelCmp(bucket[i].Label, bucket[i].Label.Length, label, labelSize) == 0)
                return bucket[i];
        }

        return null;
    }

    // ── Link recognition ────────────────────────────────────────────────

    private bool IsLinkLabel(Line[] lines, int nLines, int lineOffset,
                             int beg, out int pEnd, out int pBegLineIndex,
                             out int pEndLineIndex, out int pContentsBeg, out int pContentsEnd)
    {
        pEnd = 0;
        pBegLineIndex = 0;
        pEndLineIndex = 0;
        pContentsBeg = 0;
        pContentsEnd = 0;

        int off = beg;
        int contentsBeg = 0;
        int contentsEnd = 0;
        int lineIndex = 0;
        int len = 0;

        if (text[off] != '[')
            return false;
        off++;

        while (true)
        {
            int lineEnd = lines[lineOffset + lineIndex].End;

            while (off < lineEnd)
            {
                if (text[off] == '\\' && off + 1 < size && (Md4cUnicode.IsPunct(text[off + 1]) || Md4cUnicode.IsNewline(text[off + 1])))
                {
                    if (contentsEnd == 0)
                    {
                        contentsBeg = off;
                        pBegLineIndex = lineIndex;
                    }
                    contentsEnd = off + 2;
                    off += 2;
                }
                else if (text[off] == '[')
                {
                    return false;
                }
                else if (text[off] == ']')
                {
                    if (contentsBeg < contentsEnd)
                    {
                        pContentsBeg = contentsBeg;
                        pContentsEnd = contentsEnd;
                        pEnd = off + 1;
                        pEndLineIndex = lineIndex;
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    uint codepoint = Md4cUnicode.DecodeUnicode(text, off, size, out int charSize);
                    if (!Md4cUnicode.IsUnicodeWhitespace(codepoint))
                    {
                        if (contentsEnd == 0)
                        {
                            contentsBeg = off;
                            pBegLineIndex = lineIndex;
                        }
                        contentsEnd = off + charSize;
                    }

                    off += charSize;
                }

                len++;
                if (len > 999)
                    return false;
            }

            lineIndex++;
            len++;
            if (lineIndex < nLines)
                off = lines[lineOffset + lineIndex].Beg;
            else
                break;
        }

        return false;
    }

    private bool IsLinkDestinationA(int beg, int maxEnd, out int pEnd,
                                     out int pContentsBeg, out int pContentsEnd)
    {
        pEnd = 0;
        pContentsBeg = 0;
        pContentsEnd = 0;
        int off = beg;

        if (off >= maxEnd || text[off] != '<')
            return false;
        off++;

        while (off < maxEnd)
        {
            if (text[off] == '\\' && off + 1 < maxEnd && Md4cUnicode.IsPunct(text[off + 1]))
            {
                off += 2;
                continue;
            }

            if (Md4cUnicode.IsNewline(text[off]) || text[off] == '<')
                return false;

            if (text[off] == '>')
            {
                pContentsBeg = beg + 1;
                pContentsEnd = off;
                pEnd = off + 1;
                return true;
            }

            off++;
        }

        return false;
    }

    private bool IsLinkDestinationB(int beg, int maxEnd, out int pEnd,
                                     out int pContentsBeg, out int pContentsEnd)
    {
        pEnd = 0;
        pContentsBeg = 0;
        pContentsEnd = 0;
        int off = beg;
        int parenthesisLevel = 0;

        while (off < maxEnd)
        {
            if (text[off] == '\\' && off + 1 < maxEnd && Md4cUnicode.IsPunct(text[off + 1]))
            {
                off += 2;
                continue;
            }

            if (Md4cUnicode.IsWhitespace(text[off]) || Md4cUnicode.IsCntrl(text[off]))
                break;

            if (text[off] == '(')
            {
                parenthesisLevel++;
                if (parenthesisLevel > 32)
                    return false;
            }
            else if (text[off] == ')')
            {
                if (parenthesisLevel == 0)
                    break;
                parenthesisLevel--;
            }

            off++;
        }

        if (parenthesisLevel != 0 || off == beg)
            return false;

        pContentsBeg = beg;
        pContentsEnd = off;
        pEnd = off;
        return true;
    }

    private bool IsLinkDestination(int beg, int maxEnd, out int pEnd,
                                    out int pContentsBeg, out int pContentsEnd)
    {
        if (text[beg] == '<')
            return IsLinkDestinationA(beg, maxEnd, out pEnd, out pContentsBeg, out pContentsEnd);
        else
            return IsLinkDestinationB(beg, maxEnd, out pEnd, out pContentsBeg, out pContentsEnd);
    }

    private bool IsLinkTitle(Line[] lines, int nLines, int lineOffset,
                              int beg, out int pEnd, out int pBegLineIndex,
                              out int pEndLineIndex, out int pContentsBeg, out int pContentsEnd)
    {
        pEnd = 0;
        pBegLineIndex = 0;
        pEndLineIndex = 0;
        pContentsBeg = 0;
        pContentsEnd = 0;

        int off = beg;
        char closerChar;
        int lineIndex = 0;

        // Whitespace with up to one line break.
        while (off < lines[lineOffset + lineIndex].End && Md4cUnicode.IsWhitespace(text[off]))
            off++;
        if (off >= lines[lineOffset + lineIndex].End)
        {
            lineIndex++;
            if (lineIndex >= nLines)
                return false;
            off = lines[lineOffset + lineIndex].Beg;
        }
        if (off == beg)
            return false;

        pBegLineIndex = lineIndex;

        switch (text[off])
        {
            case '"': closerChar = '"'; break;
            case '\'': closerChar = '\''; break;
            case '(': closerChar = ')'; break;
            default: return false;
        }
        off++;

        pContentsBeg = off;

        while (lineIndex < nLines)
        {
            int lineEnd = lines[lineOffset + lineIndex].End;

            while (off < lineEnd)
            {
                if (text[off] == '\\' && off + 1 < size && (Md4cUnicode.IsPunct(text[off + 1]) || Md4cUnicode.IsNewline(text[off + 1])))
                {
                    off++;
                }
                else if (text[off] == closerChar)
                {
                    pContentsEnd = off;
                    pEnd = off + 1;
                    pEndLineIndex = lineIndex;
                    return true;
                }
                else if (closerChar == ')' && text[off] == '(')
                {
                    return false;
                }

                off++;
            }

            lineIndex++;
        }

        return false;
    }

    /// <summary>
    /// Returns 0 if not a reference definition, N > 0 for N lines consumed, -1 on error.
    /// </summary>
    private int IsLinkReferenceDefinitionImpl(Line[] lines, int nLines, int lineOffset)
    {
        int off;
        int lineIndex = 0;

        // Link label.
        if (!IsLinkLabel(lines, nLines, lineOffset, lines[lineOffset].Beg,
                         out off, out int labelContentsLineIndex, out lineIndex,
                         out int labelContentsBeg, out int labelContentsEnd))
            return 0;
        bool labelIsMultiline = (labelContentsLineIndex != lineIndex);

        // Colon.
        if (off >= lines[lineOffset + lineIndex].End || text[off] != ':')
            return 0;
        off++;

        // Optional whitespace with up to one line break.
        while (off < lines[lineOffset + lineIndex].End && Md4cUnicode.IsWhitespace(text[off]))
            off++;
        if (off >= lines[lineOffset + lineIndex].End)
        {
            lineIndex++;
            if (lineIndex >= nLines)
                return 0;
            off = lines[lineOffset + lineIndex].Beg;
        }

        // Link destination.
        if (!IsLinkDestination(off, lines[lineOffset + lineIndex].End,
                               out off, out int destContentsBeg, out int destContentsEnd))
            return 0;

        // Optional title.
        int titleContentsBeg, titleContentsEnd;
        int titleContentsLineIndex;
        bool titleIsMultiline = false;

        if (IsLinkTitle(lines, nLines - lineIndex, lineOffset + lineIndex, off,
                        out int titleEnd, out titleContentsLineIndex, out int tmpLineIndex,
                        out titleContentsBeg, out titleContentsEnd) &&
            titleEnd >= lines[lineOffset + lineIndex + tmpLineIndex].End)
        {
            titleIsMultiline = (tmpLineIndex != titleContentsLineIndex);
            titleContentsLineIndex += lineIndex;
            lineIndex += tmpLineIndex;
            off = titleEnd;
        }
        else
        {
            titleIsMultiline = false;
            titleContentsBeg = off;
            titleContentsEnd = off;
            titleContentsLineIndex = 0;
        }

        // Nothing more on the last line.
        if (off < lines[lineOffset + lineIndex].End)
            return 0;

        // Build the reference definition.
        var def = new RefDef();

        if (labelIsMultiline)
        {
            def.Label = MergeLinesAlloc(labelContentsBeg, labelContentsEnd,
                            lines, nLines - labelContentsLineIndex,
                            lineOffset + labelContentsLineIndex, ' ');
        }
        else
        {
            def.Label = text.Substring(labelContentsBeg, labelContentsEnd - labelContentsBeg);
        }

        if (titleContentsBeg < titleContentsEnd)
        {
            if (titleIsMultiline)
            {
                def.Title = MergeLinesAlloc(titleContentsBeg, titleContentsEnd,
                                lines, nLines - titleContentsLineIndex,
                                lineOffset + titleContentsLineIndex, '\n');
            }
            else
            {
                def.Title = text.Substring(titleContentsBeg, titleContentsEnd - titleContentsBeg);
            }
        }
        else
        {
            def.Title = null;
        }

        def.DestBeg = destContentsBeg;
        def.DestEnd = destContentsEnd;

        refDefs.Add(def);
        return lineIndex + 1;
    }

    private int IsLinkReference(Line[] lines, int nLines, int lineOffset,
                                int beg, int end, out LinkAttr attr)
    {
        attr = default;

        if (maxRefDefOutput == 0)
            return 0;

        Debug.Assert(text[beg] == '[' || text[beg] == '!');
        Debug.Assert(text[end - 1] == ']');

        int labelBeg = beg + (text[beg] == '!' ? 2 : 1);
        int labelEnd = end - 1;

        // Find the line corresponding to labelBeg.
        int begLineIndex = LookupLineIndex(labelBeg, lines, nLines, lineOffset);
        bool isMultiline = begLineIndex >= 0 && (labelEnd > lines[lineOffset + begLineIndex].End);

        string label;
        int labelSize;

        if (isMultiline)
        {
            label = MergeLinesAlloc(labelBeg, labelEnd, lines,
                        nLines - begLineIndex, lineOffset + begLineIndex, ' ');
            labelSize = label.Length;
        }
        else
        {
            label = text.Substring(labelBeg, labelEnd - labelBeg);
            labelSize = label.Length;
        }

        var def = LookupRefDef(label, labelSize);
        if (def != null)
        {
            attr.DestBeg = def.Value.DestBeg;
            attr.DestEnd = def.Value.DestEnd;
            attr.Title = def.Value.Title;
        }

        if (def != null)
        {
            long outputSizeEstimation = def.Value.Label.Length +
                (def.Value.Title?.Length ?? 0) +
                def.Value.DestEnd - def.Value.DestBeg;
            if (outputSizeEstimation < maxRefDefOutput)
            {
                maxRefDefOutput -= outputSizeEstimation;
                return 1; // TRUE
            }
            else
            {
                Log("Too many link reference definition instantiations.");
                maxRefDefOutput = 0;
            }
        }

        return 0;
    }

    private int IsInlineLinkSpec(Line[] lines, int nLines, int lineOffset,
                                 int beg, out int pEnd, out LinkAttr attr)
    {
        pEnd = 0;
        attr = default;
        int lineIndex = 0;
        int off = beg;

        // Find the line for off.
        int li = LookupLineIndex(off, lines, nLines, lineOffset);
        if (li >= 0) lineIndex = li;

        Debug.Assert(text[off] == '(');
        off++;

        // Optional whitespace with up to one line break.
        while (off < lines[lineOffset + lineIndex].End && Md4cUnicode.IsWhitespace(text[off]))
            off++;
        if (off >= lines[lineOffset + lineIndex].End && (off >= size || Md4cUnicode.IsNewline(text[off])))
        {
            lineIndex++;
            if (lineIndex >= nLines)
                return 0;
            off = lines[lineOffset + lineIndex].Beg;
        }

        // Link destination may be omitted.
        if (off < size && text[off] == ')')
        {
            attr.DestBeg = off;
            attr.DestEnd = off;
            attr.Title = null;
            off++;
            pEnd = off;
            return 1;
        }

        // Link destination.
        if (!IsLinkDestination(off, lines[lineOffset + lineIndex].End,
                               out off, out attr.DestBeg, out attr.DestEnd))
            return 0;

        // Optional title.
        int titleContentsBeg, titleContentsEnd;
        int titleContentsLineIndex;
        bool titleIsMultiline;

        if (IsLinkTitle(lines, nLines - lineIndex, lineOffset + lineIndex, off,
                        out int titleEnd, out titleContentsLineIndex, out int tmpLineIndex,
                        out titleContentsBeg, out titleContentsEnd))
        {
            titleIsMultiline = (tmpLineIndex != titleContentsLineIndex);
            titleContentsLineIndex += lineIndex;
            lineIndex += tmpLineIndex;
            off = titleEnd;
        }
        else
        {
            titleIsMultiline = false;
            titleContentsBeg = off;
            titleContentsEnd = off;
            titleContentsLineIndex = 0;
        }

        // Optional whitespace followed by ')'.
        while (off < lines[lineOffset + lineIndex].End && Md4cUnicode.IsWhitespace(text[off]))
            off++;
        if (off >= lines[lineOffset + lineIndex].End)
        {
            lineIndex++;
            if (lineIndex >= nLines)
                return 0;
            off = lines[lineOffset + lineIndex].Beg;
        }
        if (text[off] != ')')
            return 0;
        off++;

        if (titleContentsBeg >= titleContentsEnd)
        {
            attr.Title = null;
        }
        else if (!titleIsMultiline)
        {
            attr.Title = text.Substring(titleContentsBeg, titleContentsEnd - titleContentsBeg);
        }
        else
        {
            attr.Title = MergeLinesAlloc(titleContentsBeg, titleContentsEnd,
                            lines, nLines - titleContentsLineIndex,
                            lineOffset + titleContentsLineIndex, '\n');
        }

        pEnd = off;
        return 1;
    }

    // ── Code span recognition ───────────────────────────────────────────

    private bool IsCodeSpan(Line[] lines, int nLines, int lineOffset,
                            int beg, ref Mark opener, ref Mark closer,
                            int[] lastPotentialClosers, ref int reachedParagraphEnd)
    {
        int openerBeg = beg;
        int openerEnd;
        int closerBeg;
        int closerEnd;
        int markLen;
        int lineEnd;
        bool hasSpaceAfterOpener;
        bool hasEolAfterOpener;
        bool hasSpaceBeforeCloser;
        bool hasEolBeforeCloser;
        bool hasOnlySpace = true;
        int lineIndex = 0;

        lineEnd = lines[lineOffset].End;
        openerEnd = openerBeg;
        while (openerEnd < lineEnd && text[openerEnd] == '`')
            openerEnd++;
        hasSpaceAfterOpener = (openerEnd < lineEnd && text[openerEnd] == ' ');
        hasEolAfterOpener = (openerEnd == lineEnd);

        // The caller needs end of opening mark even if we fail.
        opener.End = openerEnd;

        markLen = openerEnd - openerBeg;
        if (markLen > CODESPAN_MARK_MAXLEN)
            return false;

        // Check whether we already know there is no closer of this length.
        if (lastPotentialClosers[markLen - 1] >= lines[lineOffset + nLines - 1].End ||
            (reachedParagraphEnd != 0 && lastPotentialClosers[markLen - 1] < openerEnd))
            return false;

        closerBeg = openerEnd;
        closerEnd = openerEnd;

        // Find closer mark.
        while (true)
        {
            while (closerBeg < lineEnd && text[closerBeg] != '`')
            {
                if (text[closerBeg] != ' ')
                    hasOnlySpace = false;
                closerBeg++;
            }
            closerEnd = closerBeg;
            while (closerEnd < lineEnd && text[closerEnd] == '`')
                closerEnd++;

            if (closerEnd - closerBeg == markLen)
            {
                hasSpaceBeforeCloser = (closerBeg > lines[lineOffset + lineIndex].Beg && text[closerBeg - 1] == ' ');
                hasEolBeforeCloser = (closerBeg == lines[lineOffset + lineIndex].Beg);
                break;
            }

            if (closerEnd - closerBeg > 0)
            {
                hasOnlySpace = false;

                if (closerEnd - closerBeg < CODESPAN_MARK_MAXLEN)
                {
                    if (closerBeg > lastPotentialClosers[closerEnd - closerBeg - 1])
                        lastPotentialClosers[closerEnd - closerBeg - 1] = closerBeg;
                }
            }

            if (closerEnd >= lineEnd)
            {
                lineIndex++;
                if (lineIndex >= nLines)
                {
                    reachedParagraphEnd = 1;
                    return false;
                }
                lineEnd = lines[lineOffset + lineIndex].End;
                closerBeg = lines[lineOffset + lineIndex].Beg;
            }
            else
            {
                closerBeg = closerEnd;
            }
        }

        // Strip one space if both opener/closer have space/eol and not only spaces.
        if (!hasOnlySpace &&
            (hasSpaceAfterOpener || hasEolAfterOpener) &&
            (hasSpaceBeforeCloser || hasEolBeforeCloser))
        {
            if (hasSpaceAfterOpener)
                openerEnd++;
            else
                openerEnd = lines[lineOffset + 1].Beg;

            if (hasSpaceBeforeCloser)
                closerBeg--;
            else
            {
                closerBeg = lines[lineOffset + lineIndex - 1].End;
                while (closerBeg < size && Md4cUnicode.IsBlank(text[closerBeg]))
                    closerBeg++;
            }
        }

        opener.Ch = '`';
        opener.Beg = openerBeg;
        opener.End = openerEnd;
        opener.Flags = MARK_POTENTIAL_OPENER;
        closer.Ch = '`';
        closer.Beg = closerBeg;
        closer.End = closerEnd;
        closer.Flags = MARK_POTENTIAL_CLOSER;
        return true;
    }

    // ── Autolink recognition ────────────────────────────────────────────

    private bool IsAutolinkUri(int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        int off = beg + 1;

        Debug.Assert(text[beg] == '<');

        if (off >= maxEnd || !Md4cUnicode.IsAscii(text[off]))
            return false;
        off++;
        while (true)
        {
            if (off >= maxEnd)
                return false;
            if (off - beg > 32)
                return false;
            if (text[off] == ':' && off - beg >= 3)
                break;
            if (!Md4cUnicode.IsAlNum(text[off]) && text[off] != '+' && text[off] != '-' && text[off] != '.')
                return false;
            off++;
        }

        while (off < maxEnd && text[off] != '>')
        {
            if (Md4cUnicode.IsWhitespace(text[off]) || Md4cUnicode.IsCntrl(text[off]) || text[off] == '<')
                return false;
            off++;
        }

        if (off >= maxEnd)
            return false;

        Debug.Assert(text[off] == '>');
        pEnd = off + 1;
        return true;
    }

    private bool IsAutolinkEmail(int beg, int maxEnd, out int pEnd)
    {
        pEnd = 0;
        int off = beg + 1;

        Debug.Assert(text[beg] == '<');

        // Username (before '@').
        while (off < maxEnd && (Md4cUnicode.IsAlNum(text[off]) || IsAnyOf(text[off], ".!#$%&'*+/=?^_`{|}~-")))
            off++;
        if (off <= beg + 1)
            return false;

        // '@'
        if (off >= maxEnd || text[off] != '@')
            return false;
        off++;

        // Labels delimited with '.'.
        int labelLen = 0;
        while (off < maxEnd)
        {
            if (Md4cUnicode.IsAlNum(text[off]))
                labelLen++;
            else if (text[off] == '-' && labelLen > 0)
                labelLen++;
            else if (text[off] == '.' && labelLen > 0 && text[off - 1] != '-')
                labelLen = 0;
            else
                break;

            if (labelLen > 63)
                return false;

            off++;
        }

        if (labelLen <= 0 || off >= maxEnd || text[off] != '>' || text[off - 1] == '-')
            return false;

        pEnd = off + 1;
        return true;
    }

    private bool IsAutolink(int beg, int maxEnd, out int pEnd, out bool missingMailto)
    {
        pEnd = 0;
        missingMailto = false;

        if (IsAutolinkUri(beg, maxEnd, out pEnd))
        {
            missingMailto = false;
            return true;
        }

        if (IsAutolinkEmail(beg, maxEnd, out pEnd))
        {
            missingMailto = true;
            return true;
        }

        return false;
    }

    // ── Mark collection ─────────────────────────────────────────────────

    private int CollectMarks(Line[] lines, int nLines, int lineOffset, bool tableMode)
    {
        int ret = 0;
        int[] codespanLastPotentialClosers = new int[CODESPAN_MARK_MAXLEN];
        int codespanScannedTillParagraphEnd = 0;

        for (int lineIndex = 0; lineIndex < nLines; lineIndex++)
        {
            ref Line line = ref lines[lineOffset + lineIndex];
            int off = line.Beg;

            while (true)
            {
                // Optimization: skip non-mark characters.
                while (off + 3 < line.End && !IsMarkChar(off) && !IsMarkChar(off + 1) &&
                       !IsMarkChar(off + 2) && !IsMarkChar(off + 3))
                    off += 4;
                while (off < line.End && !IsMarkChar(off))
                    off++;

                if (off >= line.End)
                    break;

                char ch = text[off];

                // Backslash escape.
                if (ch == '\\' && off + 1 < size && (Md4cUnicode.IsPunct(text[off + 1]) || Md4cUnicode.IsNewline(text[off + 1])))
                {
                    if (!Md4cUnicode.IsNewline(text[off + 1]) || lineIndex + 1 < nLines)
                    {
                        int mi = AddMark(ch, off, off + 2, MARK_RESOLVED);
                        if (mi < 0) return -1;
                    }
                    off += 2;
                    continue;
                }

                // Emphasis start/end.
                if (ch == '*' || ch == '_')
                {
                    int tmp = off + 1;
                    int leftLevel, rightLevel;

                    while (tmp < line.End && text[tmp] == ch)
                        tmp++;

                    if (off == line.Beg || IsUnicodeWhitespaceBefore(off))
                        leftLevel = 0;
                    else if (IsUnicodePunctBefore(off))
                        leftLevel = 1;
                    else
                        leftLevel = 2;

                    if (tmp == line.End || IsUnicodeWhitespaceAt(tmp))
                        rightLevel = 0;
                    else if (IsUnicodePunctAt(tmp))
                        rightLevel = 1;
                    else
                        rightLevel = 2;

                    // Intra-word underscore has no special meaning.
                    if (ch == '_' && leftLevel == 2 && rightLevel == 2)
                    {
                        leftLevel = 0;
                        rightLevel = 0;
                    }

                    if (leftLevel != 0 || rightLevel != 0)
                    {
                        byte markFlags = 0;

                        if (leftLevel > 0 && leftLevel >= rightLevel)
                            markFlags |= MARK_POTENTIAL_CLOSER;
                        if (rightLevel > 0 && rightLevel >= leftLevel)
                            markFlags |= MARK_POTENTIAL_OPENER;
                        if (markFlags == (MARK_POTENTIAL_OPENER | MARK_POTENTIAL_CLOSER))
                            markFlags |= MARK_EMPH_OC;

                        switch ((tmp - off) % 3)
                        {
                            case 0: markFlags |= MARK_EMPH_MOD3_0; break;
                            case 1: markFlags |= MARK_EMPH_MOD3_1; break;
                            case 2: markFlags |= MARK_EMPH_MOD3_2; break;
                        }

                        int mi = AddMark(ch, off, tmp, markFlags);
                        if (mi < 0) return -1;

                        off++;
                        while (off < tmp)
                        {
                            int di = AddMark('D', off, off, 0);
                            if (di < 0) return -1;
                            off++;
                        }
                        continue;
                    }

                    off = tmp;
                    continue;
                }

                // Code span.
                if (ch == '`')
                {
                    var openerM = new Mark();
                    var closerM = new Mark();

                    bool isCodeSpan = IsCodeSpan(lines, nLines - lineIndex, lineOffset + lineIndex,
                                off, ref openerM, ref closerM,
                                codespanLastPotentialClosers, ref codespanScannedTillParagraphEnd);
                    if (isCodeSpan)
                    {
                        int oi = AddMark(openerM.Ch, openerM.Beg, openerM.End, openerM.Flags);
                        if (oi < 0) return -1;
                        int ci = AddMark(closerM.Ch, closerM.Beg, closerM.End, closerM.Flags);
                        if (ci < 0) return -1;
                        ResolveRange(nMarks - 2, nMarks - 1);
                        off = closerM.End;

                        // Advance line if needed.
                        if (off > line.End)
                        {
                            int idx = LookupLineIndex(off, lines, nLines, lineOffset);
                            if (idx >= 0) lineIndex = idx;
                            line = ref lines[lineOffset + lineIndex];
                        }
                        continue;
                    }

                    off = openerM.End;
                    continue;
                }

                // Entity start.
                if (ch == '&')
                {
                    int mi = AddMark(ch, off, off + 1, MARK_POTENTIAL_OPENER);
                    if (mi < 0) return -1;
                    off++;
                    continue;
                }

                // Entity end.
                if (ch == ';')
                {
                    if (nMarks > 0 && marks[nMarks - 1].Ch == '&')
                    {
                        int mi = AddMark(ch, off, off + 1, MARK_POTENTIAL_CLOSER);
                        if (mi < 0) return -1;
                    }
                    off++;
                    continue;
                }

                // Raw HTML or autolink.
                if (ch == '<')
                {
                    if ((flags & MdParserFlags.NoHtmlSpans) == 0)
                    {
                        bool isHtml = IsHtmlAny(lines, nLines - lineIndex, lineOffset + lineIndex,
                                        off, lines[lineOffset + nLines - 1].End, out int htmlEnd);
                        if (isHtml)
                        {
                            int oi = AddMark('<', off, off, MARK_OPENER | MARK_RESOLVED);
                            if (oi < 0) return -1;
                            int ci = AddMark('>', htmlEnd, htmlEnd, MARK_CLOSER | MARK_RESOLVED);
                            if (ci < 0) return -1;
                            marks[nMarks - 2].Next = nMarks - 1;
                            marks[nMarks - 1].Prev = nMarks - 2;
                            off = htmlEnd;

                            if (off > line.End)
                            {
                                int idx = LookupLineIndex(off, lines, nLines, lineOffset);
                                if (idx >= 0) lineIndex = idx;
                                line = ref lines[lineOffset + lineIndex];
                            }
                            continue;
                        }
                    }

                    bool isAutolink = IsAutolink(off, lines[lineOffset + nLines - 1].End,
                                        out int autolinkEnd, out bool missingMailto);
                    if (isAutolink)
                    {
                        byte aFlags = MARK_RESOLVED | MARK_AUTOLINK;
                        if (missingMailto)
                            aFlags |= MARK_AUTOLINK_MISSING_MAILTO;

                        int oi = AddMark('<', off, off + 1, (byte)(MARK_OPENER | aFlags));
                        if (oi < 0) return -1;
                        int ci = AddMark('>', autolinkEnd - 1, autolinkEnd, (byte)(MARK_CLOSER | aFlags));
                        if (ci < 0) return -1;
                        marks[nMarks - 2].Next = nMarks - 1;
                        marks[nMarks - 1].Prev = nMarks - 2;
                        off = autolinkEnd;
                        continue;
                    }

                    off++;
                    continue;
                }

                // Link or image.
                if (ch == '[' || (ch == '!' && off + 1 < line.End && text[off + 1] == '['))
                {
                    int tmp = (ch == '[') ? off + 1 : off + 2;
                    int mi = AddMark(ch, off, tmp, MARK_POTENTIAL_OPENER);
                    if (mi < 0) return -1;
                    off = tmp;
                    int d1 = AddMark('D', off, off, 0);
                    if (d1 < 0) return -1;
                    int d2 = AddMark('D', off, off, 0);
                    if (d2 < 0) return -1;
                    continue;
                }
                if (ch == ']')
                {
                    int mi = AddMark(ch, off, off + 1, MARK_POTENTIAL_CLOSER);
                    if (mi < 0) return -1;
                    off++;
                    continue;
                }

                // Permissive e-mail autolink.
                if (ch == '@')
                {
                    if (line.Beg + 1 <= off && Md4cUnicode.IsAlNum(text[off - 1]) &&
                        off + 3 < line.End && Md4cUnicode.IsAlNum(text[off + 1]))
                    {
                        int mi = AddMark(ch, off, off + 1, MARK_POTENTIAL_OPENER);
                        if (mi < 0) return -1;
                        int di = AddMark('D', line.Beg, line.End, 0);
                        if (di < 0) return -1;
                    }
                    off++;
                    continue;
                }

                // Permissive URL autolink.
                if (ch == ':')
                {
                    var schemeMap = s_autolinkSchemes;

                    foreach (var (scheme, suffix) in schemeMap)
                    {
                        int schemeSize = scheme.Length;
                        int suffixSize = suffix.Length;

                        if (line.Beg + schemeSize <= off &&
                            AsciiEq(off - schemeSize, scheme, schemeSize) &&
                            off + 1 + suffixSize < line.End &&
                            AsciiEq(off + 1, suffix, suffixSize))
                        {
                            int mi = AddMark(ch, off - schemeSize, off + 1 + suffixSize, MARK_POTENTIAL_OPENER);
                            if (mi < 0) return -1;
                            int di = AddMark('D', line.Beg, line.End, 0);
                            if (di < 0) return -1;
                            off += 1 + suffixSize;
                            break;
                        }
                    }

                    off++;
                    continue;
                }

                // Permissive WWW autolink.
                if (ch == '.')
                {
                    if (line.Beg + 3 <= off &&
                        AsciiEq(off - 3, "www", 3) &&
                        (off - 3 == line.Beg || IsUnicodeWhitespaceBefore(off - 3) || IsUnicodePunctBefore(off - 3)))
                    {
                        int mi = AddMark(ch, off - 3, off + 1, MARK_POTENTIAL_OPENER);
                        if (mi < 0) return -1;
                        int di = AddMark('D', line.Beg, line.End, 0);
                        if (di < 0) return -1;
                        off++;
                        continue;
                    }
                    off++;
                    continue;
                }

                // Table cell boundary or wiki link delimiter.
                if ((tableMode || (flags & MdParserFlags.WikiLinks) != 0) && ch == '|')
                {
                    int mi = AddMark(ch, off, off + 1, 0);
                    if (mi < 0) return -1;
                    off++;
                    continue;
                }

                // Strikethrough or equation.
                if (ch == '$' || ch == '~')
                {
                    int tmp = off + 1;

                    while (tmp < line.End && text[tmp] == ch)
                        tmp++;

                    if (tmp - off <= 2)
                    {
                        byte mf = MARK_POTENTIAL_OPENER | MARK_POTENTIAL_CLOSER;

                        if (off > line.Beg && !IsUnicodeWhitespaceBefore(off) && !IsUnicodePunctBefore(off))
                            mf &= unchecked((byte)~MARK_POTENTIAL_OPENER);
                        if (tmp < line.End && !IsUnicodeWhitespaceAt(tmp) && !IsUnicodePunctAt(tmp))
                            mf &= unchecked((byte)~MARK_POTENTIAL_CLOSER);
                        if (mf != 0)
                        {
                            int mi = AddMark(ch, off, tmp, mf);
                            if (mi < 0) return -1;
                        }
                    }

                    off = tmp;
                    continue;
                }

                // Collapse whitespace.
                if (Md4cUnicode.IsWhitespace(ch))
                {
                    int tmp = off + 1;

                    while (tmp < line.End && Md4cUnicode.IsWhitespace(text[tmp]))
                        tmp++;

                    if (tmp - off > 1 || ch != ' ')
                    {
                        int mi = AddMark(ch, off, tmp, MARK_RESOLVED);
                        if (mi < 0) return -1;
                    }

                    off = tmp;
                    continue;
                }

                // NULL character.
                if (ch == '\0')
                {
                    int mi = AddMark(ch, off, off + 1, MARK_RESOLVED);
                    if (mi < 0) return -1;
                    off++;
                    continue;
                }

                off++;
            }
        }

        // Sentinel dummy mark at end.
        int sentinel = AddMark((char)127, size, size, MARK_RESOLVED);
        if (sentinel < 0) return -1;

        return ret;
    }

    // ── Inline analysis ─────────────────────────────────────────────────

    private void AnalyzeBracket(int markIndex)
    {
        ref Mark mark = ref marks[markIndex];

        if ((mark.Flags & MARK_POTENTIAL_OPENER) != 0)
        {
            if (BracketOpeners.Top >= 0)
                marks[BracketOpeners.Top].Flags |= MARK_HASNESTEDBRACKETS;

            MarkStackPush(ref BracketOpeners, markIndex);
            return;
        }

        if (BracketOpeners.Top >= 0)
        {
            int openerIndex = MarkStackPop(ref BracketOpeners);
            ref Mark opener = ref marks[openerIndex];

            opener.Next = markIndex;
            mark.Prev = openerIndex;

            if (unresolvedLinkTail >= 0)
                marks[unresolvedLinkTail].Prev = openerIndex;
            else
                unresolvedLinkHead = openerIndex;
            unresolvedLinkTail = openerIndex;
            opener.Prev = -1;
        }
    }

    private int ResolveLinks(Line[] lines, int nLines, int lineOffset)
    {
        int openerIndex = unresolvedLinkHead;
        int lastLinkBeg = 0;
        int lastLinkEnd = 0;
        int lastImgBeg = 0;
        int lastImgEnd = 0;

        while (openerIndex >= 0)
        {
            ref Mark opener = ref marks[openerIndex];
            int closerIndex = opener.Next;
            ref Mark closer = ref marks[closerIndex];
            int nextIndex = opener.Prev;

            Mark? nextOpener = null;
            Mark? nextCloser = null;
            int nextOpenerIndex = -1;

            if (nextIndex >= 0)
            {
                nextOpener = marks[nextIndex];
                nextCloser = marks[marks[nextIndex].Next];
                nextOpenerIndex = nextIndex;
            }

            // Nested brackets check.
            if ((opener.Beg < lastLinkBeg && closer.End < lastLinkEnd) ||
                (opener.Beg < lastImgBeg && closer.End < lastImgEnd) ||
                (opener.Beg < lastLinkEnd && opener.Ch == '['))
            {
                openerIndex = nextIndex;
                continue;
            }

            bool isLink = false;

            // Wiki links.
            if ((flags & MdParserFlags.WikiLinks) != 0 &&
                (opener.End - opener.Beg == 1) &&
                nextOpener != null &&
                nextOpener.Value.Ch == '[' &&
                nextOpener.Value.Beg == opener.Beg - 1 &&
                (nextOpener.Value.End - nextOpener.Value.Beg == 1) &&
                nextCloser != null &&
                nextCloser.Value.Ch == ']' &&
                nextCloser.Value.Beg == closer.Beg + 1 &&
                (nextCloser.Value.End - nextCloser.Value.Beg == 1))
            {
                int delimIndex = -1;
                int destBeg, destEnd;

                isLink = true;

                int searchIdx = openerIndex + 1;
                while (searchIdx < closerIndex)
                {
                    ref Mark m = ref marks[searchIdx];
                    if (m.Ch == '|')
                    {
                        delimIndex = searchIdx;
                        break;
                    }
                    if (m.Ch != 'D')
                    {
                        if (m.Beg - opener.End > 100)
                            break;
                        if (m.Ch != 'D' && (m.Flags & MARK_OPENER) != 0)
                            searchIdx = m.Next;
                    }
                    searchIdx++;
                }

                destBeg = opener.End;
                destEnd = (delimIndex >= 0) ? marks[delimIndex].Beg : closer.Beg;
                if (destEnd - destBeg == 0 || destEnd - destBeg > 100)
                    isLink = false;

                if (isLink)
                {
                    for (int offChk = destBeg; offChk < destEnd; offChk++)
                    {
                        if (Md4cUnicode.IsNewline(text[offChk]))
                        {
                            isLink = false;
                            break;
                        }
                    }
                }

                if (isLink)
                {
                    if (delimIndex >= 0)
                    {
                        if (marks[delimIndex].End < closer.Beg)
                        {
                            Rollback(openerIndex, delimIndex, ROLLBACK_ALL);
                            Rollback(delimIndex, closerIndex, ROLLBACK_CROSSING);
                            marks[delimIndex].Flags |= MARK_RESOLVED;
                            opener.End = marks[delimIndex].Beg;
                        }
                        else
                        {
                            Rollback(openerIndex, closerIndex, ROLLBACK_ALL);
                            closer.Beg = marks[delimIndex].Beg;
                            delimIndex = -1;
                        }
                    }

                    opener.Beg = nextOpener!.Value.Beg;
                    opener.Next = closerIndex;
                    opener.Flags |= MARK_OPENER | MARK_RESOLVED;

                    closer.End = nextCloser!.Value.End;
                    closer.Prev = openerIndex;
                    closer.Flags |= MARK_CLOSER | MARK_RESOLVED;

                    lastLinkBeg = opener.Beg;
                    lastLinkEnd = closer.End;

                    if (delimIndex >= 0)
                        AnalyzeLinkContents(lines, nLines, lineOffset, delimIndex + 1, closerIndex);

                    openerIndex = marks[nextOpenerIndex].Prev;
                    continue;
                }
            }

            isLink = false;

            if (nextOpener != null && nextOpener.Value.Beg == closer.End)
            {
                int nextCloserIndex = marks[nextIndex].Next;
                if (marks[nextCloserIndex].Beg > closer.End + 1)
                {
                    // Full reference link.
                    if ((marks[nextIndex].Flags & MARK_HASNESTEDBRACKETS) == 0)
                    {
                        int lr = IsLinkReference(lines, nLines, lineOffset,
                                    marks[nextIndex].Beg, marks[nextCloserIndex].End, out var attr);
                        if (lr < 0) return -1;
                        isLink = lr != 0;
                        if (isLink)
                        {
                            closer.End = marks[nextCloserIndex].End;
                            nextIndex = marks[nextIndex].Prev;
                            StoreLinkAttr(openerIndex, attr);
                        }
                    }
                }
                else
                {
                    // Shortcut reference link.
                    if ((opener.Flags & MARK_HASNESTEDBRACKETS) == 0)
                    {
                        int lr = IsLinkReference(lines, nLines, lineOffset,
                                    opener.Beg, closer.End, out var attr);
                        if (lr < 0) return -1;
                        isLink = lr != 0;
                        if (isLink)
                        {
                            // Eat the 2nd "[]".
                            closer.End = marks[nextCloserIndex].End;
                            // Do not analyze the label as a standalone link.
                            nextIndex = marks[nextIndex].Prev;
                            StoreLinkAttr(openerIndex, attr);
                        }
                    }
                }
            }
            else
            {
                if (closer.End < size && text[closer.End] == '(')
                {
                    int inlineLinkEnd;
                    int lr = IsInlineLinkSpec(lines, nLines, lineOffset,
                                closer.End, out inlineLinkEnd, out var attr);
                    if (lr < 0) return -1;
                    isLink = lr != 0;

                    // Check closing ')' is not inside already resolved range.
                    if (isLink)
                    {
                        int ii = closerIndex + 1;
                        while (ii < nMarks)
                        {
                            ref Mark m = ref marks[ii];
                            if (m.Beg >= inlineLinkEnd)
                                break;
                            if ((m.Flags & (MARK_OPENER | MARK_RESOLVED)) == (MARK_OPENER | MARK_RESOLVED))
                            {
                                if (marks[m.Next].Beg >= inlineLinkEnd)
                                {
                                    isLink = false;
                                    break;
                                }
                                ii = m.Next + 1;
                            }
                            else
                            {
                                ii++;
                            }
                        }
                    }

                    if (isLink)
                    {
                        closer.End = inlineLinkEnd;
                        StoreLinkAttr(openerIndex, attr);
                    }
                }

                if (!isLink)
                {
                    // Collapsed reference link.
                    if ((opener.Flags & MARK_HASNESTEDBRACKETS) == 0)
                    {
                        int lr = IsLinkReference(lines, nLines, lineOffset,
                                    opener.Beg, closer.End, out var attr);
                        if (lr < 0) return -1;
                        isLink = lr != 0;
                        if (isLink) StoreLinkAttr(openerIndex, attr);
                    }
                }
            }

            if (isLink)
            {
                opener.Flags |= MARK_OPENER | MARK_RESOLVED;
                closer.Flags |= MARK_CLOSER | MARK_RESOLVED;

                if (opener.Ch == '[')
                {
                    lastLinkBeg = opener.Beg;
                    lastLinkEnd = closer.End;
                }
                else
                {
                    lastImgBeg = opener.Beg;
                    lastImgEnd = closer.End;
                }

                AnalyzeLinkContents(lines, nLines, lineOffset, openerIndex + 1, closerIndex);

                // Suppress permissive autolink inside link text.
                if ((flags & MdParserFlags.PermissiveAutolinks) != 0)
                {
                    int firstNestedIdx = openerIndex + 1;
                    while (firstNestedIdx < closerIndex && marks[firstNestedIdx].Ch == 'D')
                        firstNestedIdx++;

                    int lastNestedIdx = closerIndex - 1;
                    while (lastNestedIdx > openerIndex && marks[lastNestedIdx].Ch == 'D')
                        lastNestedIdx--;

                    if (firstNestedIdx < closerIndex && lastNestedIdx > openerIndex &&
                        (marks[firstNestedIdx].Flags & MARK_RESOLVED) != 0 &&
                        marks[firstNestedIdx].Beg == opener.End &&
                        IsAnyOf(marks[firstNestedIdx].Ch, "@:.") &&
                        marks[firstNestedIdx].Next == lastNestedIdx &&
                        marks[lastNestedIdx].End == closer.Beg)
                    {
                        marks[firstNestedIdx].Ch = 'D';
                        marks[firstNestedIdx].Flags &= unchecked((byte)~MARK_RESOLVED);
                        marks[lastNestedIdx].Ch = 'D';
                        marks[lastNestedIdx].Flags &= unchecked((byte)~MARK_RESOLVED);
                    }
                }
            }

            openerIndex = nextIndex;
        }

        return 0;
    }

    private void StoreLinkAttr(int openerIndex, LinkAttr attr)
    {
        Debug.Assert(marks[openerIndex + 1].Ch == 'D');
        marks[openerIndex + 1].Beg = attr.DestBeg;
        marks[openerIndex + 1].End = attr.DestEnd;

        Debug.Assert(marks[openerIndex + 2].Ch == 'D');
        MarkStorePtr(openerIndex + 2, attr.Title ?? "");
        marks[openerIndex + 2].Prev = attr.Title?.Length ?? 0;
    }

    private void AnalyzeEntity(int markIndex)
    {
        ref Mark opener = ref marks[markIndex];

        if (markIndex + 1 >= nMarks)
            return;
        ref Mark closer = ref marks[markIndex + 1];
        if (closer.Ch != ';')
            return;

        if (IsEntity(opener.Beg, closer.End, out int off))
        {
            Debug.Assert(off == closer.End);
            ResolveRange(markIndex, markIndex + 1);
            opener.End = closer.End;
        }
    }

    private void AnalyzeTableCellBoundary(int markIndex)
    {
        ref Mark mark = ref marks[markIndex];
        mark.Flags |= MARK_RESOLVED;
        mark.Next = -1;

        if (tableCellBoundariesHead < 0)
            tableCellBoundariesHead = markIndex;
        else
            marks[tableCellBoundariesTail].Next = markIndex;
        tableCellBoundariesTail = markIndex;
        nTableCellBoundaries++;
    }

    private int SplitEmphMark(int markIndex, int n)
    {
        ref Mark mark = ref marks[markIndex];
        int newMarkIndex = markIndex + (mark.End - mark.Beg - n);
        ref Mark dummy = ref marks[newMarkIndex];

        Debug.Assert(mark.End - mark.Beg > n);
        Debug.Assert(dummy.Ch == 'D');

        dummy.Beg = mark.End - n;
        dummy.End = mark.End;
        dummy.Prev = mark.Prev;
        dummy.Next = mark.Next;
        dummy.Ch = mark.Ch;
        dummy.Flags = mark.Flags;

        mark.End -= n;

        return newMarkIndex;
    }

    private void AnalyzeEmph(int markIndex)
    {
        ref Mark mark = ref marks[markIndex];

        if ((mark.Flags & MARK_POTENTIAL_CLOSER) != 0)
        {
            int openerIndex = -1;
            int nOpenerStacks = 0;
            int[] openerStackIndices = new int[6];
            byte markFlags = mark.Flags;

            // Rule of 3.
            openerStackIndices[nOpenerStacks++] = EmphStackIndex(mark.Ch, MARK_EMPH_MOD3_0 | MARK_EMPH_OC);
            if ((markFlags & MARK_EMPH_MOD3_MASK) != MARK_EMPH_MOD3_2)
                openerStackIndices[nOpenerStacks++] = EmphStackIndex(mark.Ch, MARK_EMPH_MOD3_1 | MARK_EMPH_OC);
            if ((markFlags & MARK_EMPH_MOD3_MASK) != MARK_EMPH_MOD3_1)
                openerStackIndices[nOpenerStacks++] = EmphStackIndex(mark.Ch, MARK_EMPH_MOD3_2 | MARK_EMPH_OC);
            openerStackIndices[nOpenerStacks++] = EmphStackIndex(mark.Ch, MARK_EMPH_MOD3_0);
            if ((markFlags & MARK_EMPH_OC) == 0 || (markFlags & MARK_EMPH_MOD3_MASK) != MARK_EMPH_MOD3_2)
                openerStackIndices[nOpenerStacks++] = EmphStackIndex(mark.Ch, MARK_EMPH_MOD3_1);
            if ((markFlags & MARK_EMPH_OC) == 0 || (markFlags & MARK_EMPH_MOD3_MASK) != MARK_EMPH_MOD3_1)
                openerStackIndices[nOpenerStacks++] = EmphStackIndex(mark.Ch, MARK_EMPH_MOD3_2);

            int bestOpenerIndex = -1;
            int bestOpenerEnd = -1;
            for (int i = 0; i < nOpenerStacks; i++)
            {
                ref MarkStack st = ref openerStacks[openerStackIndices[i]];
                if (st.Top >= 0)
                {
                    ref Mark m = ref marks[st.Top];
                    if (bestOpenerIndex < 0 || m.End > bestOpenerEnd)
                    {
                        bestOpenerIndex = st.Top;
                        bestOpenerEnd = m.End;
                    }
                }
            }

            if (bestOpenerIndex >= 0)
            {
                openerIndex = bestOpenerIndex;
                int openerSize = marks[openerIndex].End - marks[openerIndex].Beg;
                int closerSize = mark.End - mark.Beg;
                ref MarkStack stack = ref OpenerStack(openerIndex);

                if (openerSize > closerSize)
                {
                    openerIndex = SplitEmphMark(openerIndex, closerSize);
                    MarkStackPush(ref stack, openerIndex);
                }
                else if (openerSize < closerSize)
                {
                    SplitEmphMark(markIndex, closerSize - openerSize);
                }

                MarkStackPop(ref stack);
                Rollback(openerIndex, markIndex, ROLLBACK_CROSSING);
                ResolveRange(openerIndex, markIndex);
                return;
            }
        }

        if ((mark.Flags & MARK_POTENTIAL_OPENER) != 0)
            MarkStackPush(ref EmphStack(mark.Ch, mark.Flags), markIndex);
    }

    private int EmphStackIndex(char ch, byte markFlags)
    {
        int baseIdx = ch == '*' ? 0 : 6;
        if ((markFlags & MARK_EMPH_OC) != 0)
            baseIdx += 3;

        int mod3 = (markFlags & MARK_EMPH_MOD3_MASK) switch
        {
            MARK_EMPH_MOD3_0 => 0,
            MARK_EMPH_MOD3_1 => 1,
            MARK_EMPH_MOD3_2 => 2,
            _ => 0,
        };

        return baseIdx + mod3;
    }

    private void AnalyzeTilde(int markIndex)
    {
        ref Mark mark = ref marks[markIndex];
        ref MarkStack stack = ref OpenerStack(markIndex);

        if ((mark.Flags & MARK_POTENTIAL_CLOSER) != 0 && stack.Top >= 0)
        {
            int openerIdx = stack.Top;

            MarkStackPop(ref stack);
            Rollback(openerIdx, markIndex, ROLLBACK_CROSSING);
            ResolveRange(openerIdx, markIndex);
            return;
        }

        if ((mark.Flags & MARK_POTENTIAL_OPENER) != 0)
            MarkStackPush(ref stack, markIndex);
    }

    private void AnalyzeDollar(int markIndex)
    {
        ref Mark mark = ref marks[markIndex];

        if ((mark.Flags & MARK_POTENTIAL_CLOSER) != 0 && DollarOpeners.Top >= 0)
        {
            ref Mark opener = ref marks[DollarOpeners.Top];
            int openerIdx = DollarOpeners.Top;

            if (opener.End - opener.Beg == mark.End - mark.Beg)
            {
                MarkStackPop(ref DollarOpeners);
                Rollback(openerIdx, markIndex, ROLLBACK_ALL);
                ResolveRange(openerIdx, markIndex);
                DollarOpeners.Top = -1;
                return;
            }
        }

        if ((mark.Flags & MARK_POTENTIAL_OPENER) != 0)
            MarkStackPush(ref DollarOpeners, markIndex);
    }

    private int ScanLeftForResolvedMark(int markFrom, int off, out int cursor)
    {
        cursor = -1;
        for (int i = markFrom; i >= 0; i--)
        {
            ref Mark m = ref marks[i];
            if (m.Ch == 'D' || m.Beg > off)
                continue;
            if (m.Beg <= off && off < m.End && (m.Flags & MARK_RESOLVED) != 0)
            {
                cursor = i;
                return i;
            }
            if (m.End <= off)
            {
                cursor = i;
                break;
            }
        }
        return -1;
    }

    private int ScanRightForResolvedMark(int markFrom, int off, out int cursor)
    {
        cursor = nMarks;
        for (int i = markFrom; i < nMarks; i++)
        {
            ref Mark m = ref marks[i];
            if (m.Ch == 'D' || m.End <= off)
                continue;
            if (m.Beg <= off && off < m.End && (m.Flags & MARK_RESOLVED) != 0)
            {
                cursor = i;
                return i;
            }
            if (m.Beg > off)
            {
                cursor = i;
                break;
            }
        }
        return -1;
    }

    private void AnalyzePermissiveAutolink(int markIndex)
    {
        // URL_MAP structure.
        var urlMap = new (char startChar, char delimChar, string allowedNonAlnum, int minComponents, char optionalEndChar)[]
        {
            ('\0', '.', ".-_",       2, '\0'),  // host (mandatory)
            ('/',  '/', "/.-_",      0, '/'),    // path
            ('?',  '&', "&.-+_=()",  1, '\0'),   // query
            ('#',  '\0', ".-+_",     1, '\0'),   // fragment
        };

        ref Mark opener = ref marks[markIndex];
        ref Mark closer = ref marks[markIndex + 1];
        int lineBeg = closer.Beg;
        int lineEnd = closer.End;
        int beg = opener.Beg;
        int end = opener.End;
        int leftCursor = markIndex;
        bool leftBoundaryOk = false;
        int rightCursor = markIndex;
        bool rightBoundaryOk = false;

        Debug.Assert(closer.Ch == 'D');

        if (opener.Ch == '@')
        {
            Debug.Assert(text[opener.Beg] == '@');

            while (beg > lineBeg)
            {
                if (Md4cUnicode.IsAlNum(text[beg - 1]))
                {
                    beg--;
                }
                else if (beg >= lineBeg + 2 && Md4cUnicode.IsAlNum(text[beg - 2]) &&
                         IsAnyOf(text[beg - 1], ".-_+") &&
                         ScanLeftForResolvedMark(leftCursor, beg - 1, out leftCursor) < 0 &&
                         Md4cUnicode.IsAlNum(text[beg]))
                {
                    beg--;
                }
                else
                {
                    break;
                }
            }
            if (beg == opener.Beg)
                return;
        }

        // Verify left boundary.
        if (beg == lineBeg || IsUnicodeWhitespaceBefore(beg) || IsAnyOf(text[beg - 1], "({["))
        {
            leftBoundaryOk = true;
        }
        else if (IsAnyOf(text[beg - 1], "*_~"))
        {
            int leftMark = ScanLeftForResolvedMark(leftCursor, beg - 1, out leftCursor);
            if (leftMark >= 0 && (marks[leftMark].Flags & MARK_OPENER) != 0)
                leftBoundaryOk = true;
        }
        if (!leftBoundaryOk)
            return;

        for (int i = 0; i < urlMap.Length; i++)
        {
            int nComponents = 0;
            int nOpenBrackets = 0;

            if (urlMap[i].startChar != '\0')
            {
                if (end >= lineEnd || text[end] != urlMap[i].startChar)
                    continue;
                if (urlMap[i].minComponents > 0 && (end + 1 >= lineEnd || !Md4cUnicode.IsAlNum(text[end + 1])))
                    continue;
                end++;
            }

            while (end < lineEnd)
            {
                if (Md4cUnicode.IsAlNum(text[end]))
                {
                    if (nComponents == 0)
                        nComponents++;
                    end++;
                }
                else if (end < lineEnd &&
                         IsAnyOf(text[end], urlMap[i].allowedNonAlnum) &&
                         ScanRightForResolvedMark(rightCursor, end, out rightCursor) < 0 &&
                         ((end > lineBeg && (Md4cUnicode.IsAlNum(text[end - 1]) || text[end - 1] == ')')) || text[end] == '(') &&
                         ((end + 1 < lineEnd && (Md4cUnicode.IsAlNum(text[end + 1]) || text[end + 1] == '(')) || text[end] == ')'))
                {
                    if (text[end] == urlMap[i].delimChar)
                        nComponents++;

                    if (text[end] == '(')
                    {
                        nOpenBrackets++;
                    }
                    else if (text[end] == ')')
                    {
                        if (nOpenBrackets <= 0)
                            break;
                        nOpenBrackets--;
                    }

                    end++;
                }
                else
                {
                    break;
                }
            }

            if (end < lineEnd && urlMap[i].optionalEndChar != '\0' && text[end] == urlMap[i].optionalEndChar)
                end++;

            if (nComponents < urlMap[i].minComponents || nOpenBrackets != 0)
                return;

            if (opener.Ch == '@')
                break;
        }

        // Verify right boundary.
        if (end == lineEnd || IsUnicodeWhitespaceAt(end) || IsAnyOf(text[end], ")}].!?,;"))
        {
            rightBoundaryOk = true;
        }
        else
        {
            int rightMark = ScanRightForResolvedMark(rightCursor, end, out rightCursor);
            if (rightMark >= 0 && (marks[rightMark].Flags & MARK_CLOSER) != 0)
                rightBoundaryOk = true;
        }
        if (!rightBoundaryOk)
            return;

        opener.Beg = beg;
        opener.End = beg;
        closer.Beg = end;
        closer.End = end;
        closer.Ch = opener.Ch;
        ResolveRange(markIndex, markIndex + 1);
    }

    private void AnalyzeMarks(Line[] lines, int nLines, int lineOffset,
                              int markBeg, int markEnd, string markChars, int analysisFlags)
    {
        int i = markBeg;
        int lastEnd = lines[lineOffset].Beg;

        while (i < markEnd)
        {
            ref Mark mark = ref marks[i];

            // Skip resolved spans.
            if ((mark.Flags & MARK_RESOLVED) != 0)
            {
                if ((mark.Flags & MARK_OPENER) != 0 &&
                    !((analysisFlags & ANALYZE_NOSKIP_EMPH) != 0 && IsAnyOf(mark.Ch, "*_~")))
                {
                    Debug.Assert(i < mark.Next);
                    i = mark.Next + 1;
                }
                else
                {
                    i++;
                }
                continue;
            }

            // Skip marks we don't care about.
            if (!IsAnyOf(mark.Ch, markChars))
            {
                i++;
                continue;
            }

            // Expanded mark from previous step.
            if (mark.Beg < lastEnd)
            {
                i++;
                continue;
            }

            switch (mark.Ch)
            {
                case '[':
                case '!':
                case ']': AnalyzeBracket(i); break;
                case '&': AnalyzeEntity(i); break;
                case '|': AnalyzeTableCellBoundary(i); break;
                case '_':
                case '*': AnalyzeEmph(i); break;
                case '~': AnalyzeTilde(i); break;
                case '$': AnalyzeDollar(i); break;
                case '.':
                case ':':
                case '@': AnalyzePermissiveAutolink(i); break;
            }

            if ((mark.Flags & MARK_RESOLVED) != 0)
            {
                if ((mark.Flags & MARK_OPENER) != 0)
                    lastEnd = marks[mark.Next].End;
                else
                    lastEnd = mark.End;
            }

            i++;
        }
    }

    private int AnalyzeInlines(Line[] lines, int nLines, int lineOffset, bool tableMode)
    {
        int ret;

        // Reset marks.
        nMarks = 0;

        // Collect all marks.
        ret = CollectMarks(lines, nLines, lineOffset, tableMode);
        if (ret < 0) return ret;

        // (1) Links.
        AnalyzeMarks(lines, nLines, lineOffset, 0, nMarks, "[]!", 0);
        ret = ResolveLinks(lines, nLines, lineOffset);
        if (ret < 0) return ret;
        BracketOpeners.Top = -1;
        unresolvedLinkHead = -1;
        unresolvedLinkTail = -1;

        if (tableMode)
        {
            // (2) Table cell boundaries.
            Debug.Assert(nLines == 1);
            nTableCellBoundaries = 0;
            AnalyzeMarks(lines, nLines, lineOffset, 0, nMarks, "|", 0);
            return 0;
        }

        // (3) Emphasis and strong emphasis; permissive autolinks.
        AnalyzeLinkContents(lines, nLines, lineOffset, 0, nMarks);

        // Add a sentinel mark past end-of-text so ProcessInlines never
        // reads past the marks array.  The sentinel is RESOLVED with
        // Ch = '\x7f' (DEL) which is the "stop" signal in ProcessInlines.
        int endOff = lines[lineOffset + nLines - 1].End + 1;
        AddMark((char)127, endOff, endOff, MARK_RESOLVED);

        return 0;
    }

    private void AnalyzeLinkContents(Line[] lines, int nLines, int lineOffset,
                                     int markBeg, int markEnd)
    {
        AnalyzeMarks(lines, nLines, lineOffset, markBeg, markEnd, "&", 0);
        AnalyzeMarks(lines, nLines, lineOffset, markBeg, markEnd, "*_~$", 0);

        if ((flags & MdParserFlags.PermissiveAutolinks) != 0)
        {
            AnalyzeMarks(lines, nLines, lineOffset, markBeg, markEnd, "@:.", ANALYZE_NOSKIP_EMPH);
        }

        for (int i = 0; i < openerStacks.Length; i++)
            openerStacks[i].Top = -1;
    }

    // ── Span enter/leave helpers ────────────────────────────────────────

    private int EnterLeaveSpanA(bool enter, MdSpanType type,
                                string dest, int destSize, bool isAutolink,
                                string? title, int titleSize)
    {
        var hrefBuild = new AttributeBuild();
        var titleBuild = new AttributeBuild();
        int ret = 0;

        ret = BuildAttribute(dest, destSize,
                    isAutolink ? BUILD_ATTR_NO_ESCAPES : 0,
                    out var href, ref hrefBuild);
        if (ret != 0) return ret;

        MdAttribute titleAttr = default;
        if (title != null && titleSize > 0)
        {
            ret = BuildAttribute(title, titleSize, 0,
                        out titleAttr, ref titleBuild);
            if (ret != 0) return ret;
        }

        // MdSpanADetail and MdSpanImgDetail are compatible.
        if (type == MdSpanType.Img)
        {
            var det = new MdSpanImgDetail { Src = href, Title = titleAttr };
            if (enter)
            { ret = EnterSpan(type, det); if (ret != 0) return ret; }
            else
            { ret = LeaveSpan(type, det); if (ret != 0) return ret; }
        }
        else
        {
            var det = new MdSpanADetail { Href = href, Title = titleAttr, IsAutolink = isAutolink };
            if (enter)
            { ret = EnterSpan(type, det); if (ret != 0) return ret; }
            else
            { ret = LeaveSpan(type, det); if (ret != 0) return ret; }
        }

        return ret;
    }

    private int EnterLeaveSpanWikiLink(bool enter, string target, int targetSize)
    {
        var targetBuild = new AttributeBuild();
        int ret = 0;

        ret = BuildAttribute(target, targetSize, 0, out var targetAttr, ref targetBuild);
        if (ret != 0) return ret;

        var det = new MdSpanWikiLinkDetail { Target = targetAttr };
        if (enter)
        { ret = EnterSpan(MdSpanType.WikiLink, det); if (ret != 0) return ret; }
        else
        { ret = LeaveSpan(MdSpanType.WikiLink, det); if (ret != 0) return ret; }

        return ret;
    }

    // ── Inline processing / rendering ───────────────────────────────────

    private int ProcessInlines(Line[] lines, int nLines, int lineOffset)
    {
        MdTextType textType;
        int lineIndex = 0;
        int markIndex = 0;
        int off = lines[lineOffset].Beg;
        int end = lines[lineOffset + nLines - 1].End;
        int enforceHardbreak = 0;
        int ret = 0;

        // Find first resolved mark.
        while (markIndex < nMarks && (marks[markIndex].Flags & MARK_RESOLVED) == 0)
            markIndex++;

        textType = MdTextType.Normal;

        while (true)
        {
            ref Line line = ref lines[lineOffset + lineIndex];

            // Process text up to next mark or end-of-line.
            int tmp = (line.End < marks[markIndex].Beg) ? line.End : marks[markIndex].Beg;
            if (tmp > off)
            {
                ret = Text(textType, off, tmp - off);
                if (ret != 0) return ret;
                off = tmp;
            }

            // If reached the mark, process it.
            if (off >= marks[markIndex].Beg)
            {
                ref Mark mark = ref marks[markIndex];

                switch (mark.Ch)
                {
                    case '\\':
                        if (Md4cUnicode.IsNewline(text[mark.Beg + 1]))
                            enforceHardbreak = 1;
                        else
                        {
                            ret = Text(textType, mark.Beg + 1, 1);
                            if (ret != 0) return ret;
                        }
                        break;

                    case ' ':
                        ret = TextBuf(textType, " ".AsSpan());
                        if (ret != 0) return ret;
                        break;

                    case '`':
                        if ((mark.Flags & MARK_OPENER) != 0)
                        {
                            ret = EnterSpan(MdSpanType.Code, null);
                            if (ret != 0) return ret;
                            textType = MdTextType.Code;
                        }
                        else
                        {
                            ret = LeaveSpan(MdSpanType.Code, null);
                            if (ret != 0) return ret;
                            textType = MdTextType.Normal;
                        }
                        break;

                    case '_':
                        if ((flags & MdParserFlags.Underline) != 0)
                        {
                            if ((mark.Flags & MARK_OPENER) != 0)
                            {
                                while (off < mark.End)
                                {
                                    ret = EnterSpan(MdSpanType.U, null);
                                    if (ret != 0) return ret;
                                    off++;
                                }
                            }
                            else
                            {
                                while (off < mark.End)
                                {
                                    ret = LeaveSpan(MdSpanType.U, null);
                                    if (ret != 0) return ret;
                                    off++;
                                }
                            }
                            break;
                        }
                        goto case '*';

                    case '*':
                        if ((mark.Flags & MARK_OPENER) != 0)
                        {
                            if ((mark.End - off) % 2 != 0)
                            {
                                ret = EnterSpan(MdSpanType.Em, null);
                                if (ret != 0) return ret;
                                off++;
                            }
                            while (off + 1 < mark.End)
                            {
                                ret = EnterSpan(MdSpanType.Strong, null);
                                if (ret != 0) return ret;
                                off += 2;
                            }
                        }
                        else
                        {
                            while (off + 1 < mark.End)
                            {
                                ret = LeaveSpan(MdSpanType.Strong, null);
                                if (ret != 0) return ret;
                                off += 2;
                            }
                            if ((mark.End - off) % 2 != 0)
                            {
                                ret = LeaveSpan(MdSpanType.Em, null);
                                if (ret != 0) return ret;
                                off++;
                            }
                        }
                        break;

                    case '~':
                        if ((mark.Flags & MARK_OPENER) != 0)
                        { ret = EnterSpan(MdSpanType.Del, null); if (ret != 0) return ret; }
                        else
                        { ret = LeaveSpan(MdSpanType.Del, null); if (ret != 0) return ret; }
                        break;

                    case '$':
                        if ((mark.Flags & MARK_OPENER) != 0)
                        {
                            var spanType = (mark.End - off) % 2 != 0 ? MdSpanType.LatexMath : MdSpanType.LatexMathDisplay;
                            ret = EnterSpan(spanType, null);
                            if (ret != 0) return ret;
                            textType = MdTextType.LatexMath;
                        }
                        else
                        {
                            var spanType = (mark.End - off) % 2 != 0 ? MdSpanType.LatexMath : MdSpanType.LatexMathDisplay;
                            ret = LeaveSpan(spanType, null);
                            if (ret != 0) return ret;
                            textType = MdTextType.Normal;
                        }
                        break;

                    case '[':
                    case '!':
                    case ']':
                    {
                        int openerIdx = (mark.Ch != ']') ? markIndex : mark.Prev;
                        ref Mark openerRef = ref marks[openerIdx];
                        int closerIdx = openerRef.Next;
                        ref Mark closerRef = ref marks[closerIdx];

                        // Wiki link detection.
                        if (openerRef.Ch == '[' && closerRef.Ch == ']' &&
                            openerRef.End - openerRef.Beg >= 2 &&
                            closerRef.End - closerRef.Beg >= 2)
                        {
                            bool hasLabel = (openerRef.End - openerRef.Beg > 2);
                            int targetSz;
                            string targetStr;

                            if (hasLabel)
                            {
                                targetSz = openerRef.End - (openerRef.Beg + 2);
                                targetStr = text.Substring(openerRef.Beg + 2, targetSz);
                            }
                            else
                            {
                                targetSz = closerRef.Beg - openerRef.End;
                                targetStr = text.Substring(openerRef.End, targetSz);
                            }

                            ret = EnterLeaveSpanWikiLink(mark.Ch != ']', targetStr, targetSz);
                            if (ret != 0) return ret;
                            break;
                        }

                        ref Mark destMark = ref marks[openerIdx + 1];
                        Debug.Assert(destMark.Ch == 'D');
                        ref Mark titleMark = ref marks[openerIdx + 2];
                        Debug.Assert(titleMark.Ch == 'D');

                        string destStr = text.Substring(destMark.Beg, destMark.End - destMark.Beg);
                        string? titleStr = MarkGetPtr(openerIdx + 2);
                        int titleSz = titleMark.Prev;

                        ret = EnterLeaveSpanA(
                            mark.Ch != ']',
                            openerRef.Ch == '!' ? MdSpanType.Img : MdSpanType.A,
                            destStr, destStr.Length, false,
                            titleStr, titleSz);
                        if (ret != 0) return ret;

                        // Link/image closer may span multiple lines.
                        if (mark.Ch == ']')
                        {
                            while (mark.End > lines[lineOffset + lineIndex].End)
                                lineIndex++;
                        }

                        break;
                    }

                    case '<':
                    case '>':
                        if ((mark.Flags & MARK_AUTOLINK) == 0)
                        {
                            if ((mark.Flags & MARK_OPENER) != 0)
                                textType = MdTextType.Html;
                            else
                                textType = MdTextType.Normal;
                            break;
                        }
                        goto case '@';

                    case '@':
                    case ':':
                    case '.':
                    {
                        int openerIdx = (mark.Flags & MARK_OPENER) != 0 ? markIndex : mark.Prev;
                        ref Mark openerRef = ref marks[openerIdx];
                        int closerIdx = openerRef.Next;
                        ref Mark closerRef = ref marks[closerIdx];
                        string dest = text.Substring(openerRef.End, closerRef.Beg - openerRef.End);
                        int destSz = dest.Length;

                        if ((mark.Flags & MARK_OPENER) != 0)
                            closerRef.Flags |= MARK_VALIDPERMISSIVEAUTOLINK;

                        if (openerRef.Ch == '@' || openerRef.Ch == '.' ||
                            (openerRef.Ch == '<' && (openerRef.Flags & MARK_AUTOLINK_MISSING_MAILTO) != 0))
                        {
                            string prefix = openerRef.Ch == '.' ? "http://" : "mailto:";
                            dest = prefix + dest;
                            destSz = dest.Length;
                        }

                        if ((closerRef.Flags & MARK_VALIDPERMISSIVEAUTOLINK) != 0)
                        {
                            ret = EnterLeaveSpanA(
                                (mark.Flags & MARK_OPENER) != 0,
                                MdSpanType.A, dest, destSz, true, null, 0);
                            if (ret != 0) return ret;
                        }
                        break;
                    }

                    case '&':
                        ret = Text(MdTextType.Entity, mark.Beg, mark.End - mark.Beg);
                        if (ret != 0) return ret;
                        break;

                    case '\0':
                        ret = TextBuf(MdTextType.NullChar, "\0".AsSpan());
                        if (ret != 0) return ret;
                        break;

                    case (char)127:
                        return ret;
                }

                off = mark.End;

                // Move to next resolved mark.
                markIndex++;
                while (markIndex < nMarks &&
                       ((marks[markIndex].Flags & MARK_RESOLVED) == 0 || marks[markIndex].Beg < off))
                    markIndex++;

                // Refresh line ref — the ']' closer may have advanced lineIndex.
                line = ref lines[lineOffset + lineIndex];
            }

            // End of line handling.
            if (off >= line.End)
            {
                if (off >= end)
                    break;

                if (textType == MdTextType.Code || textType == MdTextType.LatexMath)
                {
                    // Inside code/latex span: output trailing whitespace + newline as space.
                    tmp = off;
                    while (off < size && Md4cUnicode.IsBlank(text[off]))
                        off++;
                    if (off > tmp)
                    {
                        ret = Text(textType, tmp, off - tmp);
                        if (ret != 0) return ret;
                    }

                    if (off == line.End)
                    {
                        ret = TextBuf(textType, " ".AsSpan());
                        if (ret != 0) return ret;
                    }
                }
                else if (textType == MdTextType.Html)
                {
                    // Inside raw HTML: output trailing spaces + literal newline.
                    tmp = off;
                    while (tmp < end && Md4cUnicode.IsBlank(text[tmp]))
                        tmp++;
                    if (tmp > off)
                    {
                        ret = Text(MdTextType.Html, off, tmp - off);
                        if (ret != 0) return ret;
                    }
                    ret = TextBuf(MdTextType.Html, "\n".AsSpan());
                    if (ret != 0) return ret;
                }
                else
                {
                    // Soft or hard break.
                    MdTextType breakType = MdTextType.SoftBr;

                    if (textType == MdTextType.Normal)
                    {
                        if (enforceHardbreak != 0 || (flags & MdParserFlags.HardSoftBreaks) != 0)
                        {
                            breakType = MdTextType.Br;
                        }
                        else
                        {
                            while (off < size && Md4cUnicode.IsBlank(text[off]))
                                off++;
                            if (off >= line.End + 2 && text[off - 2] == ' ' && text[off - 1] == ' ' && Md4cUnicode.IsNewline(text[off]))
                                breakType = MdTextType.Br;
                        }
                    }

                    ret = TextBuf(breakType, "\n".AsSpan());
                    if (ret != 0) return ret;
                }

                // Move to next line.
                lineIndex++;
                if (lineIndex >= nLines)
                    break;
                off = lines[lineOffset + lineIndex].Beg;

                enforceHardbreak = 0;
            }
        }

        return ret;
    }

    // ProcessNormalBlockContents is defined in Md4cParser.Block.cs

    // IsAnyOf is defined in Md4cParser.Block.cs

    // ── Partial method implementations for Block.cs stubs ───────────────

    /// <summary>
    /// Build an MdAttribute from a range within <see cref="text"/>.
    /// Matches the partial stub in Md4cParser.Block.cs.
    /// </summary>
    private int BuildAttribute(int off, int len, int attrFlags, out MdAttribute attr)
    {
        string rawText = (len > 0) ? text.Substring(off, len) : "";
        var build = new AttributeBuild();
        return BuildAttribute(rawText, rawText.Length, attrFlags, out attr, ref build);
    }

    /// <summary>
    /// Check if lines[lineStart..] form a link reference definition.
    /// Returns 0 if not, N > 0 for N lines consumed, -1 on error.
    /// Matches the partial stub in Md4cParser.Block.cs.
    /// </summary>
    private int IsLinkReferenceDefinition(int lineStart, int nLines)
    {
        var lines = blockLines.ToArray();
        return IsLinkReferenceDefinitionImpl(lines, nLines, lineStart);
    }

    /// <summary>
    /// Check if text[beg..end] starts with an HTML tag (single-line mode).
    /// Matches the partial stub in Md4cParser.Block.cs.
    /// </summary>
    private bool IsHtmlTag(int[]? attrMap, int attrMapSize, int beg, int end, out int pEnd)
    {
        // Single-line mode: pass nLines=0 so the function knows there are no line boundaries.
        return IsHtmlTag(Array.Empty<Line>(), 0, 0, beg, end, out pEnd);
    }

    // ── Unicode helpers that work on offsets into text ───────────────────

    private bool IsUnicodeWhitespaceAt(int off)
    {
        uint cp = Md4cUnicode.DecodeUnicode(text, off, size, out _);
        return Md4cUnicode.IsUnicodeWhitespace(cp);
    }

    private bool IsUnicodeWhitespaceBefore(int off)
    {
        if (off <= 0) return true;
        uint cp = Md4cUnicode.DecodeUnicodeBefore(text, off);
        return Md4cUnicode.IsUnicodeWhitespace(cp);
    }

    private bool IsUnicodePunctAt(int off)
    {
        uint cp = Md4cUnicode.DecodeUnicode(text, off, size, out _);
        return Md4cUnicode.IsUnicodePunct(cp);
    }

    private bool IsUnicodePunctBefore(int off)
    {
        if (off <= 0) return false;
        uint cp = Md4cUnicode.DecodeUnicodeBefore(text, off);
        return Md4cUnicode.IsUnicodePunct(cp);
    }
}
