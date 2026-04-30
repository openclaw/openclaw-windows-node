using System;

namespace OpenClaw.Shared;

/// <summary>
/// Strict validator for agent-supplied URLs that the node will hand off to a
/// browser via shell-execute. Defense-in-depth around <c>canvas.navigate</c>:
/// the gateway should already only emit http(s), but treating that as
/// authoritative would let a misbehaving / compromised agent ask the node to
/// shell-execute <c>file:</c>, <c>javascript:</c>, app-protocol URIs, or
/// credential-stuffed URLs that visually masquerade as legitimate.
/// </summary>
public static class HttpUrlValidator
{
    /// <summary>
    /// Parse <paramref name="raw"/> and accept only absolute http/https URLs
    /// with a non-empty host and no userinfo. On success, <paramref name="canonical"/>
    /// is the re-serialized form (<see cref="Uri.AbsoluteUri"/>) — what the
    /// caller should hand to the OS, not the raw input string.
    /// </summary>
    public static bool TryParse(string? raw, out string? canonical, out string? error)
    {
        canonical = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "url is empty";
            return false;
        }

        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "url is not an absolute URI";
            return false;
        }

        // Scheme check is ordinal-ignore-case: Uri lowercases the scheme on
        // parse, but explicit comparison documents intent and survives any
        // future Uri changes.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"scheme '{uri.Scheme}' is not allowed (only http/https)";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "url has no host";
            return false;
        }

        // Reject userinfo: https://attacker@evil.com is technically valid HTTP
        // but is a phishing pattern (the visible "attacker" looks like a host
        // to non-experts). Browsers warn on these too.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "url contains userinfo (user:password@) which is not allowed";
            return false;
        }

        canonical = uri.AbsoluteUri;
        return true;
    }
}
