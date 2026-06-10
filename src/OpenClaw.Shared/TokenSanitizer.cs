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

    // Allows 1, 2, or 4 consecutive backslashes between segments so the pattern matches raw
    // `C:\Users\alice\…`, JSON-escaped `C:\\Users\\alice\\…` (DiagnosticsJsonlService writes after
    // JsonSerializer.Serialize doubles every backslash), and nested-serialized
    // `C:\\\\Users\\\\alice\\\\…` (record-of-pre-serialized-JSON, where backslashes quadruple).
    // Without this, local usernames could leak into disk-backed JSONL in either escape scenario.
    private static readonly Regex PathWindowsUserPattern = new(
        @"\b[A-Za-z]:\\{1,4}Users\\{1,4}[^\\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex PathUnixUserPattern = new(
        @"/Users/[^/\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Captures the scheme separately so the replacement can drop any user:pass@ userinfo entirely
    // rather than leaving credentials adjacent to the redacted <host>. The match is greedy through
    // path/query/fragment up to common log delimiters (whitespace, quote, backtick, angle brackets,
    // braces, pipe, caret) so credential-bearing URL tails are fed to RedactUrlMatch and collapsed
    // via the UrlLogSanitizer contract (scheme://<host>[:port]/<first-segment>[/…]). Backslashes
    // are deliberately consumed: malformed URLs with `\` (e.g. "https://host\reset?token=...")
    // would otherwise leave the credential tail verbatim; allowing them lets Uri.TryCreate reject
    // the URL and trigger the fail-closed `scheme://<host>` fallback.
    private static readonly Regex UrlHostPattern = new(
        @"\b(?<scheme>[a-z][a-z0-9+.-]*)://[^\s""'`<>{}|^]+",
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
            // Reconstruct the URL as scheme://<host>[:port]/<first-segment>[/…] so credential-bearing
            // userinfo, query strings, fragments, and deeper path segments are dropped. Mirrors the
            // UrlLogSanitizer contract used elsewhere for disk-backed log safety.
            sanitized = UrlHostPattern.Replace(sanitized, RedactUrlMatch);
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

    // Matches C0 control characters (NUL through US), DEL, and the Unicode line/paragraph
    // separators that downstream non-.NET log parsers (Python str.splitlines, JS, Go bufio)
    // treat as line breaks. Used to strip log-injection bytes that Uri.UnescapeDataString
    // may decode from percent-encoded sequences (%0A, %0D, %00, %1B, %C2%85, %E2%80%A8, …)
    // before they reach the disk-backed log line.
    private static readonly Regex ControlCharPattern = new(
        @"[\x00-\x1F\x7F\u0085\u2028\u2029]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Trailing characters that are almost always sentence punctuation rather than part of the URL.
    // Peeled iteratively so "Connect to https://example.com/api." round-trips with the period
    // preserved after redaction. Brackets/parens are deliberately NOT peeled: the regex excludes
    // ),>,},|,^ from URL matches, and `]` inside a bracketed IPv6 host is balanced by the opener.
    private static readonly char[] UrlTrailingPunct = { '.', ',', ';', ':', '!', '?' };

    // Folds a URL match into "scheme://<host>[:port]/<first-segment>[/…]" form so credential-bearing
    // query, fragment, and deeper path segments are dropped. Mirrors the UrlLogSanitizer contract.
    // Unparseable URLs fall back to "scheme://<host>" so a malformed string never echoes verbatim.
    internal static string RedactUrlMatch(Match match)
    {
        var raw = match.Value;
        var schemePart = match.Groups["scheme"].Value;

        // Peel trailing sentence punctuation that almost certainly wasn't part of the URL,
        // and re-append it after redaction so log lines like "see https://example.com." keep
        // the period at end of sentence.
        var end = raw.Length;
        while (end > 0 && Array.IndexOf(UrlTrailingPunct, raw[end - 1]) >= 0)
            end--;
        var urlPart = raw[..end];
        var trailing = raw[end..];

        if (!Uri.TryCreate(urlPart, UriKind.Absolute, out var uri))
            return $"{schemePart}://<host>{trailing}";

        var portPart = uri.Port > 0 && !uri.IsDefaultPort ? $":{uri.Port}" : string.Empty;
        // Unescape so percent-encoded slashes (%2F, %2f) become real path boundaries — otherwise
        // an attacker-controlled URL like https://host/path%2Fsecret%2Ftoken would keep the full
        // encoded tail as a single "first segment" and leak past the truncation.
        // Then strip control characters: UnescapeDataString also decodes %0A/%0D/%00/%1B/%09 into
        // raw bytes, which would forge log entries or break JSONL framing if written verbatim.
        var path = ControlCharPattern.Replace(Uri.UnescapeDataString(uri.AbsolutePath), string.Empty);

        if (string.IsNullOrEmpty(path) || path == "/")
            return $"{schemePart}://<host>{portPart}/{trailing}";

        var firstSlash = path.IndexOf('/', 1);
        var firstSegment = firstSlash < 0 ? path : path[..firstSlash];
        var tail = firstSlash < 0 ? string.Empty : "/…";
        return $"{schemePart}://<host>{portPart}{firstSegment}{tail}{trailing}";
    }

    private static string RedactLocalPaths(string message)
    {
        var redacted = message;
        foreach (var (folder, replacement) in KnownLocalFolders()
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Folder))
                     .OrderByDescending(pair => pair.Folder.Length))
        {
            redacted = redacted.Replace(folder, replacement, StringComparison.OrdinalIgnoreCase);
            // Also redact JSON-escaped variants so well-known folder paths serialized into JSONL
            // get redacted alongside raw text. JsonSerializer doubles each backslash on serialize;
            // nested-serialized payloads (record-of-pre-serialized-JSON) double them again.
            var jsonEscaped = folder.Replace("\\", "\\\\");
            if (!string.Equals(jsonEscaped, folder, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(jsonEscaped, replacement, StringComparison.OrdinalIgnoreCase);
                var doubleJsonEscaped = jsonEscaped.Replace("\\", "\\\\");
                if (!string.Equals(doubleJsonEscaped, jsonEscaped, StringComparison.Ordinal))
                {
                    redacted = redacted.Replace(doubleJsonEscaped, replacement, StringComparison.OrdinalIgnoreCase);
                }
            }
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
