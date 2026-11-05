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
    /// <see cref="MaxAttributeChars"/>. A single forward ASCII scan with no allocation; an
    /// attribute name only matches after XML whitespace, so <c>id=</c> or <c>data-d=</c> never count.
    /// </summary>
    internal static bool Exceeds(byte[] bytes)
    {
        long total = 0;
        for (int i = 1; i < bytes.Length; i++)
        {
            if (!IsXmlSpace(bytes[i - 1]))
                continue;

            int j;
            if (bytes[i] == (byte)'d')
                j = i + 1;
            else if (MatchesAscii(bytes, i, "points"))
                j = i + 6;
            else
                continue;

            while (j < bytes.Length && IsXmlSpace(bytes[j])) j++;
            if (j >= bytes.Length || bytes[j] != (byte)'=')
                continue;
            j++;
            while (j < bytes.Length && IsXmlSpace(bytes[j])) j++;
            if (j >= bytes.Length || (bytes[j] != (byte)'"' && bytes[j] != (byte)'\''))
                continue;

            int start = j + 1;
            int end = Array.IndexOf(bytes, bytes[j], start);
            if (end < 0) end = bytes.Length;
            total += end - start;
            if (total > MaxAttributeChars)
                return true;
            i = end;
        }
        return false;
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
