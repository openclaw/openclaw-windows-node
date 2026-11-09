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
    private const int MaxInput = 256 * 1024;
    private const int MaxOutput = 16 * 1024;
    private const string TruncationMarker = "…[truncated]";
    private const string OversizedMarker = "[exec approval command exceeds display size limit; full text suppressed]";
    private const string WarningOversizedMarker = "[exec approval warning exceeds display size limit; full text suppressed]";
    private const string BypassMask = "***";

    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return SanitizeInternal(text).Text;
    }

    public static (
        string Text,
        bool Truncated,
        bool Oversized,
        bool Redacted,
        bool UnsafeConcealment) SanitizeWithStatus(string text)
    {
        var result = SanitizeInternal(text);
        return (
            result.Text,
            result.Truncated,
            result.Oversized,
            result.Redacted,
            result.UnsafeConcealment);
    }

    public static string SanitizeWarningText(string text)
    {
        return SanitizeInternal(
            NormalizeDisplayLineBreaks(text),
            preserveLineBreaks: true,
            oversizedMarker: WarningOversizedMarker).Text;
    }

    private static SanitizedDisplayText SanitizeInternal(
        string text,
        bool preserveLineBreaks = false,
        string? oversizedMarker = null)
    {
        if (text.Length > MaxInput)
        {
            return new SanitizedDisplayText(
                oversizedMarker ?? OversizedMarker,
                Truncated: false,
                Oversized: true,
                Redacted: false,
                UnsafeConcealment: false);
        }

        var rawRedacted = ExecApprovalSecretRedactor.Redact(text);
        var strippedView = BuildStrippedView(text);
        var strippedRedacted = ExecApprovalSecretRedactor.Redact(strippedView.Text);
        var redacted =
            !string.Equals(rawRedacted, text, StringComparison.Ordinal)
            || !string.Equals(
                strippedRedacted,
                strippedView.Text,
                StringComparison.Ordinal);
        var rawMask = redacted
            ? ExecApprovalSecretRedactor.ComputeRedactionBitmap(text)
            : Array.Empty<bool>();
        var strippedMask = redacted
            ? ExecApprovalSecretRedactor.ComputeRedactionBitmap(strippedView.Text)
            : Array.Empty<bool>();
        var rawReviewSafe = ExecApprovalSecretRedactor.RedactReviewSafeUrlQueryValues(text);
        var strippedReviewSafe =
            ExecApprovalSecretRedactor.RedactReviewSafeUrlQueryValues(strippedView.Text);
        var unsafeConcealment = redacted
            && (!string.Equals(rawRedacted, rawReviewSafe, StringComparison.Ordinal)
                || !string.Equals(
                    strippedRedacted,
                    strippedReviewSafe,
                    StringComparison.Ordinal));

        if (strippedRedacted == strippedView.Text)
            return TruncateForDisplay(
                EscapeInvisibles(rawRedacted, preserveLineBreaks),
                redacted,
                unsafeConcealment);

        var bypassDetected = false;
        for (var i = 0; i < strippedMask.Length; i++)
        {
            if (strippedMask[i] && !rawMask[strippedView.StrippedToOriginal[i]])
            {
                bypassDetected = true;
                break;
            }
        }

        if (!bypassDetected)
            return TruncateForDisplay(
                EscapeInvisibles(rawRedacted, preserveLineBreaks),
                redacted,
                unsafeConcealment);

        var unionMask = (bool[])rawMask.Clone();
        for (var i = 0; i < strippedMask.Length; i++)
        {
            if (strippedMask[i])
                unionMask[strippedView.StrippedToOriginal[i]] = true;
        }

        return TruncateForDisplay(
            RenderUnionMask(text, unionMask, preserveLineBreaks),
            redacted: true,
            unsafeConcealment);
    }

    private static string NormalizeDisplayLineBreaks(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\u2028", "\n", StringComparison.Ordinal)
            .Replace("\u2029", "\n", StringComparison.Ordinal);
    }

    private static string EscapeInvisibles(string text, bool preserveLineBreaks)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sanitized = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var codePoint = ReadCodePoint(text, i, out var codeUnitLength);
            if (preserveLineBreaks && codePoint == '\n')
            {
                sanitized.Append('\n');
            }
            else if (IsInvisible(codePoint))
            {
                AppendCodePointEscape(sanitized, codePoint);
            }
            else
            {
                sanitized.Append(text, i, codeUnitLength);
            }

            i += codeUnitLength;
        }

        return sanitized.ToString();
    }

    private static StrippedView BuildStrippedView(string original)
    {
        var stripped = new StringBuilder(original.Length);
        var strippedToOriginal = new List<int>(original.Length);

        for (var i = 0; i < original.Length;)
        {
            var codePoint = ReadCodePoint(original, i, out var codeUnitLength);
            if (!IsInvisible(codePoint))
            {
                stripped.Append(original, i, codeUnitLength);
                for (var k = 0; k < codeUnitLength; k++)
                    strippedToOriginal.Add(i + k);
            }

            i += codeUnitLength;
        }

        return new StrippedView(stripped.ToString(), strippedToOriginal.ToArray());
    }

    private static string RenderUnionMask(string text, bool[] unionMask, bool preserveLineBreaks)
    {
        var rendered = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (unionMask[i])
            {
                var j = i;
                while (j < text.Length && unionMask[j])
                    j++;

                rendered.Append(BypassMask);
                i = j;
                continue;
            }

            var codePoint = ReadCodePoint(text, i, out var codeUnitLength);
            if (preserveLineBreaks && codePoint == '\n')
            {
                rendered.Append('\n');
            }
            else if (IsInvisible(codePoint))
            {
                AppendCodePointEscape(rendered, codePoint);
            }
            else
            {
                rendered.Append(text, i, codeUnitLength);
            }

            i += codeUnitLength;
        }

        return rendered.ToString();
    }

    private static SanitizedDisplayText TruncateForDisplay(
        string text,
        bool redacted = false,
        bool unsafeConcealment = false)
    {
        if (text.Length <= MaxOutput)
        {
            return new SanitizedDisplayText(
                text,
                Truncated: false,
                Oversized: false,
                Redacted: redacted,
                UnsafeConcealment: unsafeConcealment);
        }

        return new SanitizedDisplayText(
            TruncateUtf16Safe(text, MaxOutput) + TruncationMarker,
            Truncated: true,
            Oversized: false,
            Redacted: redacted,
            UnsafeConcealment: unsafeConcealment);
    }

    private static string TruncateUtf16Safe(string input, int maxLength)
    {
        var limit = Math.Max(0, maxLength);
        if (input.Length <= limit)
            return input;

        var end = limit;
        if (end > 0
            && end < input.Length
            && char.IsHighSurrogate(input[end - 1])
            && char.IsLowSurrogate(input[end]))
        {
            end--;
        }

        return input[..end];
    }

    private static bool IsInvisible(int codePoint)
    {
        var category = GetUnicodeCategory(codePoint);
        if (category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator)
        {
            return true;
        }

        if (category == UnicodeCategory.SpaceSeparator && codePoint != 0x20)
            return true;

        return codePoint is 0x115F or 0x1160 or 0x3164 or 0xFFA0;
    }

    private static UnicodeCategory GetUnicodeCategory(int codePoint)
    {
        if (codePoint is >= 0xD800 and <= 0xDFFF)
            return UnicodeCategory.Surrogate;

        return Rune.GetUnicodeCategory(new Rune(codePoint));
    }

    private static int ReadCodePoint(string text, int index, out int codeUnitLength)
    {
        var current = text[index];
        if (char.IsHighSurrogate(current)
            && index + 1 < text.Length
            && char.IsLowSurrogate(text[index + 1]))
        {
            codeUnitLength = 2;
            return char.ConvertToUtf32(current, text[index + 1]);
        }

        codeUnitLength = 1;
        return current;
    }

    private static void AppendCodePointEscape(StringBuilder builder, int codePoint)
    {
        builder.Append("\\u{").Append(codePoint.ToString("X")).Append('}');
    }

    private sealed record SanitizedDisplayText(
        string Text,
        bool Truncated,
        bool Oversized,
        bool Redacted,
        bool UnsafeConcealment);

    private sealed record StrippedView(string Text, int[] StrippedToOriginal);
}
