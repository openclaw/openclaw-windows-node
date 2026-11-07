// Pre-decode byte policy for A2UI SVG images.
//
// D2D's SvgImageSource parse can block the UI thread past MediaResolver.SvgRenderTimeout on
// pathological input (the timeout abandons only the await, not the native parse), so
// MediaResolver rejects unsupported encodings, oversized path data and excessive element counts
// before the decoder sees the bytes. The policy also strips the DOCTYPE, which must happen before
// the byte scan.
//
// Usage:
//   if (!SvgGeometryBudget.TryPrepareSvgForDecode(bytes, out var svgBytes, out var rejection))
//   { /* log and show the broken-image fallback */ }

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Pure byte-level policy for SVG bytes on their way to the decoder: unsupported-encoding
/// rejection, DOCTYPE strip, a budget on the total length of <c>d</c> and <c>points</c>
/// attribute values, and a budget on the number of element start tags. Kept free of WinUI types
/// so it can be unit-tested without a XAML runtime.
/// </summary>
internal static class SvgGeometryBudget
{
    /// <summary>Maximum summed characters across all <c>d</c> and <c>points</c> attribute values.</summary>
    internal const int MaxAttributeChars = 64 * 1024;

    /// <summary>
    /// Maximum number of element start tags. Path data is not the only expensive input: a document
    /// of many cheap shapes (<c>&lt;rect/&gt;</c>, <c>&lt;use/&gt;</c>) decodes in linear time, so a
    /// 2 MiB data URL of small elements can exceed the render cap without a single path attribute.
    /// The limit is far above UI imagery (icons carry tens of elements, charts dozens to hundreds)
    /// and rejects only documents whose node count alone is pathological.
    /// </summary>
    internal const int MaxElements = 8 * 1024;

    /// <summary>Which budget, if any, the bytes exceed.</summary>
    internal enum SvgBudgetLimit
    {
        None = 0,
        AttributeChars,
        ElementCount,
    }

    /// <summary>True when <see cref="FindExceededLimit"/> reports any exceeded budget.</summary>
    internal static bool Exceeds(byte[] bytes) => FindExceededLimit(bytes) != SvgBudgetLimit.None;

    /// <summary>
    /// Reports the first exceeded budget: summed length of <c>d</c> and <c>points</c> attribute
    /// values above <see cref="MaxAttributeChars"/>, or more than <see cref="MaxElements"/> element
    /// start tags. A single forward ASCII scan with no allocation that walks XML structure:
    /// comments, CDATA, processing instructions and DOCTYPE are skipped, attributes are only read
    /// inside element start tags, so attribute-like text in comments or other attributes' values
    /// (<c>data-d=</c>, <c>id='d='</c>) never counts, and end tags are not elements.
    /// Callers must pass bytes with the DOCTYPE already removed: this scan ends a declaration at
    /// its first <c>&gt;</c>, so a DTD internal subset would not match what the decoder parses.
    /// <see cref="TryPrepareSvgForDecode"/> guarantees that by rejecting any input where a
    /// declaration survives the strip.
    /// </summary>
    internal static SvgBudgetLimit FindExceededLimit(byte[] bytes)
    {
        long total = 0;
        int elements = 0;
        int i = 0;
        while (i < bytes.Length)
        {
            int lt = Array.IndexOf(bytes, (byte)'<', i);
            if (lt < 0) return SvgBudgetLimit.None;
            i = lt + 1;

            if (StartsWith(bytes, i, "!--")) { i = SkipPast(bytes, i + 3, "-->"); continue; }
            if (StartsWith(bytes, i, "![CDATA[")) { i = SkipPast(bytes, i + 8, "]]>"); continue; }
            if (StartsWith(bytes, i, "?")) { i = SkipPast(bytes, i + 1, "?>"); continue; }
            if (StartsWith(bytes, i, "!")) { i = SkipPast(bytes, i + 1, ">"); continue; }

            // Element start/end tag: skip the name, then walk name="value" pairs to '>'.
            int tagNameStart = i;
            while (i < bytes.Length && !IsXmlSpace(bytes[i]) && bytes[i] != (byte)'>' && bytes[i] != (byte)'/') i++;
            if (i > tagNameStart && ++elements > MaxElements)
                return SvgBudgetLimit.ElementCount;
            while (i < bytes.Length)
            {
                while (i < bytes.Length && (IsXmlSpace(bytes[i]) || bytes[i] == (byte)'/')) i++;
                if (i >= bytes.Length || bytes[i] == (byte)'>') { i++; break; }

                int nameStart = i;
                while (i < bytes.Length && !IsXmlSpace(bytes[i]) && bytes[i] != (byte)'=' && bytes[i] != (byte)'>' && bytes[i] != (byte)'/') i++;
                int nameLen = i - nameStart;
                bool counted = (nameLen == 1 && bytes[nameStart] == (byte)'d')
                    || (nameLen == 6 && MatchesAscii(bytes, nameStart, "points"));

                while (i < bytes.Length && IsXmlSpace(bytes[i])) i++;
                if (i >= bytes.Length || bytes[i] != (byte)'=') continue;
                i++;
                while (i < bytes.Length && IsXmlSpace(bytes[i])) i++;
                if (i >= bytes.Length || (bytes[i] != (byte)'"' && bytes[i] != (byte)'\'')) continue;

                int start = i + 1;
                int end = Array.IndexOf(bytes, bytes[i], start);
                if (end < 0) end = bytes.Length;
                if (counted)
                {
                    total += end - start;
                    if (total > MaxAttributeChars)
                        return SvgBudgetLimit.AttributeChars;
                }
                i = end + 1;
            }
        }
        return SvgBudgetLimit.None;
    }

    /// <summary>
    /// True for input that is not ASCII-compatible XML (UTF-16/UTF-32 BOM, or a NUL in the first
    /// bytes). <see cref="Exceeds"/> and the DOCTYPE strip only understand ASCII-compatible
    /// encodings, so other encodings are rejected rather than scanned incorrectly.
    /// </summary>
    internal static bool IsUnsupportedEncoding(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return true; // UTF-16 LE/BE and UTF-32 LE BOM (FF FE 00 00)
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return true; // UTF-32 BE BOM
        return Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8)) >= 0;
    }

    /// <summary>
    /// Pre-decode policy over the exact bytes the decoder receives: reject encodings the ASCII
    /// scan cannot read, strip the DOCTYPE, reject the input if any declaration survives that
    /// strip, then enforce the attribute and element budgets. The strip must run before the
    /// budget: <see cref="FindExceededLimit"/> ends a <c>&lt;!</c> declaration at its first
    /// <c>&gt;</c>, while <see cref="StripDoctype"/> honors a DTD internal subset, so scanning
    /// unstripped bytes would count text the decoder never sees (and could miss text it does).
    /// The leftover check fails closed: <see cref="StripDoctype"/> removes the first textual
    /// declaration, which a comment or processing instruction before the real one can displace.
    /// An SVG that keeps a literal <c>&lt;!DOCTYPE</c> in its own comment is therefore rejected
    /// with the same reason.
    /// </summary>
    /// <param name="bytes">Raw SVG bytes.</param>
    /// <param name="sanitized">Bytes to decode; equal to <paramref name="bytes"/> when nothing was stripped.</param>
    /// <param name="rejection">Failure reason for the caller's log line; empty on success.</param>
    internal static bool TryPrepareSvgForDecode(byte[] bytes, out byte[] sanitized, out string rejection)
    {
        sanitized = bytes;
        if (IsUnsupportedEncoding(bytes))
        {
            rejection = "non-UTF-8 encoding (UTF-16/UTF-32) is unsupported";
            return false;
        }

        sanitized = StripDoctype(bytes);
        if (IndexOfAsciiCi(sanitized, 0, "<!DOCTYPE") >= 0)
        {
            rejection = "DOCTYPE could not be stripped";
            return false;
        }

        switch (FindExceededLimit(sanitized))
        {
            case SvgBudgetLimit.AttributeChars:
                rejection = $"geometry attributes exceed {MaxAttributeChars} characters";
                return false;
            case SvgBudgetLimit.ElementCount:
                rejection = $"element count exceeds {MaxElements}";
                return false;
        }

        rejection = string.Empty;
        return true;
    }

    /// <summary>
    /// Remove the first <c>&lt;!DOCTYPE …&gt;</c> declaration (including any internal subset
    /// in <c>[…]</c>) from the byte stream. SVG content has no need for a DOCTYPE — browsers
    /// ignore it — but stripping it pre-empts XXE / entity-expansion attacks regardless of
    /// what the downstream XML parser does.
    /// </summary>
    internal static byte[] StripDoctype(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return bytes ?? Array.Empty<byte>();
        int idx = IndexOfAsciiCi(bytes, 0, "<!DOCTYPE");
        if (idx < 0) return bytes;
        int cursor = idx + "<!DOCTYPE".Length;
        int firstGt = IndexOfByte(bytes, cursor, (byte)'>');
        if (firstGt < 0) return bytes;
        int bracket = IndexOfByteInRange(bytes, cursor, firstGt, (byte)'[');
        int end;
        if (bracket < 0)
        {
            end = firstGt;
        }
        else
        {
            int closeBracket = IndexOfByte(bytes, bracket + 1, (byte)']');
            if (closeBracket < 0) return bytes;
            end = IndexOfByte(bytes, closeBracket + 1, (byte)'>');
            if (end < 0) return bytes;
        }
        var result = new byte[bytes.Length - (end - idx + 1)];
        Buffer.BlockCopy(bytes, 0, result, 0, idx);
        Buffer.BlockCopy(bytes, end + 1, result, idx, bytes.Length - end - 1);
        return result;
    }

    private static int IndexOfByte(byte[] haystack, int start, byte needle)
    {
        for (int i = start; i < haystack.Length; i++)
            if (haystack[i] == needle) return i;
        return -1;
    }

    private static int IndexOfByteInRange(byte[] haystack, int start, int endExclusive, byte needle)
    {
        int limit = Math.Min(endExclusive, haystack.Length);
        for (int i = start; i < limit; i++)
            if (haystack[i] == needle) return i;
        return -1;
    }

    private static int IndexOfAsciiCi(byte[] haystack, int start, string asciiNeedle)
    {
        if (asciiNeedle.Length == 0) return start;
        int last = haystack.Length - asciiNeedle.Length;
        for (int i = start; i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < asciiNeedle.Length; j++)
            {
                byte b = haystack[i + j];
                char c = asciiNeedle[j];
                byte upper = (b >= (byte)'a' && b <= (byte)'z') ? (byte)(b - 0x20) : b;
                char nUpper = (c >= 'a' && c <= 'z') ? (char)(c - 0x20) : c;
                if (upper != (byte)nUpper) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static bool StartsWith(byte[] bytes, int offset, string value) => MatchesAscii(bytes, offset, value);

    /// <summary>Index just past the next <paramref name="terminator"/> at or after <paramref name="from"/>, or the end of input.</summary>
    private static int SkipPast(byte[] bytes, int from, string terminator)
    {
        for (int i = from; i + terminator.Length <= bytes.Length; i++)
        {
            if (MatchesAscii(bytes, i, terminator)) return i + terminator.Length;
        }
        return bytes.Length;
    }

    private static bool IsXmlSpace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;

    private static bool MatchesAscii(byte[] bytes, int offset, string value)
    {
        if (offset + value.Length > bytes.Length) return false;
        for (int k = 0; k < value.Length; k++)
        {
            if (bytes[offset + k] != (byte)value[k]) return false;
        }
        return true;
    }
}
