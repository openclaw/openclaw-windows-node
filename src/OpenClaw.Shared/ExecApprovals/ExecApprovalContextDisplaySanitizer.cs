using System.Globalization;
using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Sanitizes agent-supplied approval context while preserving intentional
/// line breaks. Unlike command text, explanatory context is expected to be
/// multi-line; other control, format, and separator characters are escaped.
/// </summary>
public static class ExecApprovalContextDisplaySanitizer
{
    public static string Sanitize(string? value, int maxLength = 1_200)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
            return "";

        var output = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var rune in value.EnumerateRunes())
        {
            var piece = rune.Value is '\r' or '\n' or '\t'
                ? rune.ToString()
                : Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control or
                    UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator
                    ? $@"\u{{{rune.Value:X}}}"
                    : rune.ToString();

            if (output.Length + piece.Length <= maxLength)
            {
                output.Append(piece);
                continue;
            }

            if (output.Length >= maxLength)
                output.Length = maxLength - 1;
            output.Append('…');
            break;
        }

        return output.ToString().Trim();
    }
}
