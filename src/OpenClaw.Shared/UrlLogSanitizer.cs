using System;
using System.Text;

namespace OpenClaw.Shared;

/// <summary>
/// Reduce a URL to the parts that are safe to write to disk-backed logs.
/// Query strings routinely carry tokens, codes, signatures, email addresses,
/// and PII; the log-rotation policy on a developer machine is "never", so
/// anything we put in the log file effectively lives forever.
///
/// The shape is "scheme://host[:port]/<first-segment>/…" — enough to triage,
/// not enough to replay an OAuth callback or recover a credential. URLs that
/// fail to parse are returned as the literal "&lt;unparseable URL&gt;" rather
/// than echoed back, so a deliberately malformed string can't slip through.
/// </summary>
public static class UrlLogSanitizer
{
    public static string Sanitize(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "<empty>";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<unparseable URL>";

        var origin = uri.GetLeftPart(UriPartial.Authority);
        return origin + ReduceLoggedPath(uri.AbsolutePath);
    }

    internal static string ReduceLoggedPath(string absolutePath)
    {
        // Unescape once so %2F, %3F, and %23 become real boundaries. The same
        // decode turns %0A, %00, %1B, NEL, and the Unicode line separators into
        // raw controls. Strip those before the segment cut. A newline left in
        // this string is written by canvas logs, and the later URL regex stops
        // at it, so the rest of the line is never redacted. One decode only:
        // %250A stays as the characters %0A.
        var path = StripLogControls(UnescapePath(absolutePath));
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";

        var firstSlash = path.IndexOf('/', 1);
        if (firstSlash < 0)
            return path;
        return path[..firstSlash] + "/…";
    }

    // Same set as TokenSanitizer's log-injection filter: C0, DEL, NEL, U+2028, U+2029.
    private static string StripLogControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!IsLogControl(ch))
                builder.Append(ch);
        }

        return builder.Length == value.Length ? value : builder.ToString();
    }

    private static bool IsLogControl(char ch)
        => ch <= '\u001F' || ch is '\u007F' or '\u0085' or '\u2028' or '\u2029';

    private static string UnescapePath(string absolutePath)
    {
        try
        {
            return Uri.UnescapeDataString(absolutePath);
        }
        catch (UriFormatException)
        {
            return absolutePath;
        }
    }
}
