using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public static class TokenSanitizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex AuthorizationBearerPattern = new(
        @"(?i)(Authorization\s*:\s*Bearer\s+)([^\s""',;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex JsonSecretFieldPattern = new(
        @"""(?<key>[^""]*(?:token|secret|bearer|authorization)[^""]*)""\s*:\s*""(?<value>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex BareGatewayHexTokenPattern = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex LongBase64UrlPattern = new(
        @"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex PathWindowsUserPattern = new(
        @"\b[A-Za-z]:\\Users\\[^\\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex PathUnixUserPattern = new(
        @"/Users/[^/\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Captures the scheme separately so the replacement can drop any user:pass@ userinfo entirely
    // rather than leaving credentials adjacent to the redacted <host>.
    // Host group accepts bracketed IPv6 literals (e.g. [::1]) in addition to ordinary hostnames.
    private static readonly Regex UrlHostPattern = new(
        @"\b(?<scheme>[a-z][a-z0-9+.-]*)://(?:[^@\s/]+@)?(?<host>\[[^\]\s]+\]|[^:/\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex IpAddressPattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Catches IPv6 literals outside of URLs (e.g. fe80::1234, 2001:db8::1, ::1, full 8-group form, ::ffff:N.N.N.N).
    // Seven alternatives, ordered so .NET's leftmost-first NFA backtracking picks the most-specific
    // form (e.g. embedded-IPv4) before the plain hex form when both could match at the same position:
    //   1. Bracketed — requires IPv6 structure inside (`::` or 4+ colons) so [14:58:46], [abc], [face] are NOT matched.
    //   2. Compressed (contains `::`) with embedded IPv4 tail (e.g. 2001:db8::ffff:192.0.2.1).
    //   3. Compressed (contains `::`) hex-only (e.g. fe80::1234). Timestamps lack `::` so HH:MM:SS is safe.
    //   4. Leading `::` followed by hex groups and an embedded IPv4 (e.g. ::ffff:192.0.2.1, ::192.0.2.1).
    //   5. Leading `::` hex-only (e.g. ::1).
    //   6. Full 8-group mixed form (6 hex groups + IPv4, no `::`).
    //   7. Full 8-group hex form (7 colons, no `::`).
    // Alts 3 and 5 use a trailing negative lookahead `(?![A-Fa-f0-9:]|\.\d)` to prevent partial-match
    // leaks when the candidate is followed by an invalid IPv4 tail (e.g. `a::ffff:192.0.2.1b`).
    // The lookahead deliberately allows `.` not followed by a digit so that sentence punctuation
    // ("Server at fe80::1.") still redacts. Each candidate is then validated via
    // System.Net.IPAddress.TryParse in <see cref="RedactIfValidIpV6"/>, so non-IPv6 substrings
    // that happen to match the regex (e.g. [1:2:3:4:5]) are NOT redacted.
    internal static readonly Regex IpV6Pattern = new(
        @"\[(?=[^\]]*(?:::|(?:[^:\]]*:){4,}))[A-Fa-f0-9.:%]+\]|\b[A-Fa-f0-9]{1,4}(?::[A-Fa-f0-9]{1,4})*::(?:[A-Fa-f0-9]{1,4}:)*(?:\d{1,3}\.){3}\d{1,3}\b|\b[A-Fa-f0-9]{1,4}(?::[A-Fa-f0-9]{1,4})*::(?:[A-Fa-f0-9]{1,4}(?::[A-Fa-f0-9]{1,4})*)?(?:%[-A-Za-z0-9._~]+)?(?![A-Fa-f0-9:]|\.\d)|(?<![\w.:])::(?:[A-Fa-f0-9]{1,4}:)*(?:\d{1,3}\.){3}\d{1,3}\b|(?<![\w.:])::[A-Fa-f0-9]{1,4}(?::[A-Fa-f0-9]{1,4})*(?:%[-A-Za-z0-9._~]+)?(?![A-Fa-f0-9:]|\.\d)|\b(?:[A-Fa-f0-9]{1,4}:){6}(?:\d{1,3}\.){3}\d{1,3}\b|\b[A-Fa-f0-9]{1,4}(?::[A-Fa-f0-9]{1,4}){7}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex EmailPattern = new(
        @"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex UserAtHostPattern = new(
        @"\b(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9._-]+)(?=[:\s]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex HostAfterToPattern = new(
        @"(?<=\bto\s)[A-Za-z0-9._-]+(?=:\d{1,5}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex LeadingHostPattern = new(
        @"^\s*[A-Za-z0-9._-]+(?=:\d{1,5}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Returned for the entire message when any regex pass exceeds RegexTimeout. Fail-closed:
    // an adversarial input that causes catastrophic backtracking in one pass must not bypass
    // any downstream redaction pass by leaving the un-sanitized text intact.
    public const string SanitizerTimeoutSentinel = "[REDACTED_SANITIZER_TIMEOUT]";

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? string.Empty;

        try
        {
            // Note: "$1[REDACTED]" uses Regex substitution syntax ($1 = capture group 1).
            var sanitized = AuthorizationBearerPattern.Replace(message, "$1[REDACTED]");
            sanitized = JsonSecretFieldPattern.Replace(
                sanitized,
                match => $"\"{match.Groups["key"].Value}\":\"[REDACTED]\"");
            sanitized = BareGatewayHexTokenPattern.Replace(sanitized, "[REDACTED_TOKEN]");
            return LongBase64UrlPattern.Replace(sanitized, "[REDACTED_TOKEN]");
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail-closed: never leak un-redacted input on adversarial timeout.
            return SanitizerTimeoutSentinel;
        }
    }

    public static string SanitizeLogMessage(string? message)
    {
        var sanitized = Sanitize(message);
        if (string.IsNullOrEmpty(sanitized) || sanitized == SanitizerTimeoutSentinel)
            return sanitized;

        try
        {
            sanitized = RedactLocalPaths(sanitized);
            // Reconstruct the URL prefix from the captured scheme so any user:pass@ userinfo is dropped.
            sanitized = UrlHostPattern.Replace(
                sanitized,
                match => $"{match.Groups["scheme"].Value}://<host>");
            sanitized = IpV6Pattern.Replace(sanitized, RedactIfValidIpV6);
            sanitized = IpAddressPattern.Replace(sanitized, "<ip>");
            sanitized = EmailPattern.Replace(sanitized, "<email>");
            sanitized = UserAtHostPattern.Replace(sanitized, "<user>@<host>");
            sanitized = HostAfterToPattern.Replace(sanitized, "<host>");
            return LeadingHostPattern.Replace(sanitized, "<host>");
        }
        catch (RegexMatchTimeoutException)
        {
            return SanitizerTimeoutSentinel;
        }
    }

    // Validates an IPv6 regex match with System.Net.IPAddress so non-IPv6 substrings
    // that happen to fit the pattern (e.g. [1:2:3:4:5]) are left intact rather than
    // redacted to the misleading <ipv6> marker. Textual zone-ids (fe80::1%eth0) are
    // stripped before parsing because IPAddress.TryParse rejects them but accepts
    // numeric scope-ids (fe80::1%12).
    internal static string RedactIfValidIpV6(Match match)
    {
        var value = match.Value;
        var candidate = value.Length >= 2 && value[0] == '[' && value[^1] == ']'
            ? value.Substring(1, value.Length - 2)
            : value;
        var pct = candidate.IndexOf('%');
        if (pct >= 0)
            candidate = candidate[..pct];
        return IPAddress.TryParse(candidate, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? "<ipv6>"
            : value;
    }

    private static string RedactLocalPaths(string message)
    {
        var redacted = message;
        foreach (var (folder, replacement) in KnownLocalFolders()
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Folder))
                     .OrderByDescending(pair => pair.Folder.Length))
        {
            redacted = redacted.Replace(folder, replacement, StringComparison.OrdinalIgnoreCase);
        }

        redacted = PathWindowsUserPattern.Replace(redacted, "%USERPROFILE%");
        return PathUnixUserPattern.Replace(redacted, "$HOME");
    }

    private static IEnumerable<(string Folder, string Replacement)> KnownLocalFolders()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine("%USERPROFILE%", "Documents"));
    }
}
