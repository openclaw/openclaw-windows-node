using System.Globalization;
using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Escapes invisible and direction-changing Unicode code points before command text is
/// rendered in an approval prompt, so an agent cannot make the approved text look like
/// a different command (BiDi overrides, zero-width characters, fake line breaks,
/// non-ASCII spaces that spoof token boundaries).
/// </summary>
public static class ExecApprovalCommandDisplaySanitizer
{
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sanitized = new StringBuilder(text.Length);
        // Iterate by runes, not chars: Format-category code points exist outside the
        // BMP and a per-char loop would misclassify surrogate pairs.
        foreach (var rune in text.EnumerateRunes())
        {
            if (ShouldEscape(rune))
                sanitized.Append("\\u{").Append(rune.Value.ToString("X")).Append('}');
            else
                sanitized.Append(rune.ToString());
        }
        return sanitized.ToString();
    }

    private static bool ShouldEscape(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator)
        {
            return true;
        }

        // Non-ASCII space separators (NBSP, narrow NBSP, ideographic space, …) render
        // like a plain space but are handled differently by shells/parsers.
        if (category == UnicodeCategory.SpaceSeparator && rune.Value != 0x20)
            return true;

        // Hangul filler characters render as blank but are not classified as spaces.
        return rune.Value is 0x115F or 0x1160 or 0x3164 or 0xFFA0;
    }
}
