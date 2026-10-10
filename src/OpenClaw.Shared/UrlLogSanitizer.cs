using System;

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
        var path = UnescapePath(absolutePath);
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
