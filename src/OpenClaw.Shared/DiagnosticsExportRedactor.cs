using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>
/// Redacts sensitive data from diagnostics text before it is shown in the
/// shareable bundle preview. This intentionally over-redacts: diagnostics need
/// enough shape to debug failures, not enough detail to replay credentials.
/// </summary>
public static class DiagnosticsExportRedactor
{
    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex AuthorizationBearerPattern = new(
        @"(?i)(Authorization\s*:\s*Bearer\s+)([^\s""',;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex JsonSecretFieldPattern = new(
        @"""(?<key>[^""]*(?:token|secret|bearer|authorization|password|api[_-]?key|setup[_-]?code|private[_-]?key|nonce|device[_-]?id|session[_-]?key|request[_-]?id|raw[_-]?error[_-]?response|webhook|signing|nsec|bot[_-]?token|client[_-]?secret|cookie|set[_-]?cookie|x[_-]?api[_-]?key|browser[_-]?password|relay[_-]?url)[^""]*)""\s*:\s*""(?<value>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex KeyValueSecretPattern = new(
        @"\b(?<prefix>[A-Za-z0-9_.-]*(?:token|password|secret|api[_-]?key|setup[_-]?code|authorization|private[_-]?key|dpapi|nonce|device[_-]?id|session[_-]?key|request[_-]?id|raw[_-]?error[_-]?response|webhook|signing|nsec|bot[_-]?token|client[_-]?secret|cookie|set[_-]?cookie|x[_-]?api[_-]?key|browser[_-]?password|relay[_-]?url)[A-Za-z0-9_.-]*\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|[^\s,;}\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CommandLineSecretOptionPattern = new(
        @"(?i)(?<prefix>(?:^|\s)--(?:token|mcp-token|bootstrap-token|setup-code|password|secret|api-key|webhook|signing-secret|bot-token|client-secret|cookie|nsec)\s+)(?<value>""[^""]*""|'[^']*'|[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex HeaderSecretPattern = new(
        @"(?im)^(?<prefix>\s*(?:Cookie|Set-Cookie|X-Api-Key|X-OpenClaw-Token|Proxy-Authorization)\s*:\s*)(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex NostrPrivateKeyPattern = new(
        @"\bnsec1[023456789acdefghjklmnpqrstuvwxyz]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SlackSigningSecretPattern = new(
        @"\b[a-f0-9]{32}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DpapiBlobPattern = new(
        @"\bdpapi:[A-Za-z0-9+/=_-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UrlPattern = new(
        @"\b[a-z][a-z0-9+.-]*://[^\s<>""')]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SignedHandshakePattern = new(
        @"(?i)(signed:\s*)v3\|[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex Ed25519DeviceIdentityPattern = new(
        @"(?i)(Loaded Ed25519 device identity:\s*)[^\s,;}\]""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex AgentSessionKeyPattern = new(
        @"\bagent:[A-Za-z0-9_.-]+:[^\s,;}\]""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex LabelledNodeIdPattern = new(
        @"(?i)(\bnode:\s*)[A-Za-z0-9._-]{8,}(?:\.\.\.)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ChatCorrelationIdPattern = new(
        @"(?i)(\b(?:id|OpenClawId)\s*=\s*['""]?)[A-Fa-f0-9]{8,16}(['""]?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex WindowsUserPathPattern = new(
        @"\b[A-Za-z]:\\Users\\[^\\\r\n""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EscapedWindowsUserPathPattern = new(
        @"\b[A-Za-z]:\\\\Users\\\\[^\\\r\n""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UnixUserPathPattern = new(
        @"/Users/[^/\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EmailPattern = new(
        @"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UserAtHostPattern = new(
        @"\b(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9._-]+)(?=[:\s]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex IpPattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex HexTokenPattern = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{32,}(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex LongBase64Pattern = new(
        @"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{43,}={0,2}(?![A-Za-z0-9+/_-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var sanitized = RedactPath(text);
        sanitized = PrivateKeyPattern.Replace(sanitized, "[REDACTED_PRIVATE_KEY]");
        sanitized = AuthorizationBearerPattern.Replace(sanitized, "$1[REDACTED]");
        sanitized = HeaderSecretPattern.Replace(sanitized, "${prefix}[REDACTED]");
        sanitized = JsonSecretFieldPattern.Replace(
            sanitized,
            match => $"\"{match.Groups["key"].Value}\":\"[REDACTED]\"");
        sanitized = KeyValueSecretPattern.Replace(
            sanitized,
            match => $"{match.Groups["prefix"].Value}[REDACTED]");
        sanitized = CommandLineSecretOptionPattern.Replace(
            sanitized,
            match => $"{match.Groups["prefix"].Value}[REDACTED]");
        sanitized = DpapiBlobPattern.Replace(sanitized, "dpapi:[REDACTED]");
        sanitized = SignedHandshakePattern.Replace(sanitized, "$1[REDACTED_HANDSHAKE]");
        sanitized = Ed25519DeviceIdentityPattern.Replace(sanitized, "$1[REDACTED_DEVICE_ID]");
        sanitized = AgentSessionKeyPattern.Replace(sanitized, "[REDACTED_SESSION_KEY]");
        sanitized = LabelledNodeIdPattern.Replace(sanitized, "$1[REDACTED_NODE_ID]");
        sanitized = ChatCorrelationIdPattern.Replace(sanitized, "$1[REDACTED_ID]$2");
        sanitized = NostrPrivateKeyPattern.Replace(sanitized, "[REDACTED_NSEC]");
        sanitized = JwtPattern.Replace(sanitized, "[REDACTED_JWT]");
        sanitized = UrlPattern.Replace(sanitized, match => SanitizeUrl(match.Value));
        sanitized = EmailPattern.Replace(sanitized, "<email>");
        sanitized = UserAtHostPattern.Replace(sanitized, "<user>@<host>");
        sanitized = IpPattern.Replace(sanitized, "<ip>");
        sanitized = GuidPattern.Replace(sanitized, "[REDACTED_ID]");
        sanitized = HexTokenPattern.Replace(sanitized, "[REDACTED_TOKEN]");
        sanitized = SlackSigningSecretPattern.Replace(sanitized, "[REDACTED_TOKEN]");
        return LongBase64Pattern.Replace(sanitized, "[REDACTED_TOKEN]");
    }

    public static string RedactPath(string? pathOrText)
    {
        if (string.IsNullOrEmpty(pathOrText))
            return pathOrText ?? string.Empty;

        var redacted = pathOrText;
        foreach (var (folder, replacement) in KnownFolderReplacements())
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            redacted = redacted.Replace(folder, replacement, StringComparison.OrdinalIgnoreCase);
        }

        redacted = WindowsUserPathPattern.Replace(redacted, match =>
        {
            var value = match.Value;
            var prefixLength = value.IndexOf(@"\Users\", StringComparison.OrdinalIgnoreCase);
            return prefixLength >= 0
                ? value[..(prefixLength + @"\Users\".Length)] + "<user>"
                : "%USERPROFILE%";
        });

        redacted = EscapedWindowsUserPathPattern.Replace(redacted, match =>
        {
            var value = match.Value;
            var prefixLength = value.IndexOf(@"\\Users\\", StringComparison.OrdinalIgnoreCase);
            return prefixLength >= 0
                ? value[..(prefixLength + @"\\Users\\".Length)] + "<user>"
                : "%USERPROFILE%";
        });

        return UnixUserPathPattern.Replace(redacted, "$HOME");
    }

    private static IEnumerable<(string Folder, string Replacement)> KnownFolderReplacements()
    {
        yield return (
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "%USERPROFILE%");
        yield return (
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "%APPDATA%");
        yield return (
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "%LOCALAPPDATA%");
        yield return (
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            @"%USERPROFILE%\Documents");
    }

    private static string SanitizeUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return "<url>";

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath;
        var firstSegment = string.Empty;
        if (!string.IsNullOrWhiteSpace(path) && path != "/")
        {
            var secondSlash = path.IndexOf('/', 1);
            firstSegment = secondSlash < 0 ? path : path[..secondSlash] + "/…";
        }

        return $"{uri.Scheme}://<host>{port}{firstSegment}";
    }
}
