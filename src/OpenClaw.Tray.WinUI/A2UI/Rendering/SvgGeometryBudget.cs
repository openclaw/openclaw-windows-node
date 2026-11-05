// Pre-decode geometry budget for A2UI SVG images.
//
// D2D's SvgImageSource parse can block the UI thread past MediaResolver.SvgRenderTimeout on
// pathological geometry (the timeout abandons only the await, not the native parse), so
// MediaResolver rejects oversized geometry before it reaches the decoder.
//
// Usage:
//   if (SvgGeometryBudget.Exceeds(svgBytes)) { /* log and show the broken-image fallback */ }

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Pure byte-level budget on the total length of SVG <c>d</c> and <c>points</c> attribute
/// values. Kept free of WinUI types so it can be unit-tested without a XAML runtime.
/// </summary>
internal static class SvgGeometryBudget
{
    /// <summary>Maximum summed characters across all <c>d</c> and <c>points</c> attribute values.</summary>
    internal const int MaxAttributeChars = 64 * 1024;

    /// <summary>
    /// True when the summed length of all <c>d</c> and <c>points</c> attribute values exceeds
    /// <see cref="MaxAttributeChars"/>. A single forward ASCII scan with no allocation that walks
    /// XML structure: comments, CDATA, processing instructions and DOCTYPE are skipped, and
    /// attributes are only read inside element start tags, so attribute-like text in comments or
    /// other attributes' values (<c>data-d=</c>, <c>id='d='</c>) never counts.
    /// </summary>
    internal static bool Exceeds(byte[] bytes)
    {
        long total = 0;
        int i = 0;
        while (i < bytes.Length)
        {
            int lt = Array.IndexOf(bytes, (byte)'<', i);
            if (lt < 0) return false;
            i = lt + 1;

            if (StartsWith(bytes, i, "!--")) { i = SkipPast(bytes, i + 3, "-->"); continue; }
            if (StartsWith(bytes, i, "![CDATA[")) { i = SkipPast(bytes, i + 8, "]]>"); continue; }
            if (StartsWith(bytes, i, "?")) { i = SkipPast(bytes, i + 1, "?>"); continue; }
            if (StartsWith(bytes, i, "!")) { i = SkipPast(bytes, i + 1, ">"); continue; }

            // Element start/end tag: skip the name, then walk name="value" pairs to '>'.
            while (i < bytes.Length && !IsXmlSpace(bytes[i]) && bytes[i] != (byte)'>' && bytes[i] != (byte)'/') i++;
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
                        return true;
                }
                i = end + 1;
            }
        }
        return false;
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
