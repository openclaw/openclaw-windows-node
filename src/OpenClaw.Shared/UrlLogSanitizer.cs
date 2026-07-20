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
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/") return origin + "/";

        // Keep only the first segment so a /reset-password/<token> style path
        // doesn't leak the bearer-equivalent secret in the segment itself.
        var firstSlash = path.IndexOf('/', 1);
        var firstSegment = firstSlash < 0 ? path : path.Substring(0, firstSlash);
        return origin + firstSegment + (firstSlash < 0 ? string.Empty : "/…");
    }
}
