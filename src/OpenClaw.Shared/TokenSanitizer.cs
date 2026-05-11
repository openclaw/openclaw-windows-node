using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public static class TokenSanitizer
{
    private static readonly Regex AuthorizationBearerPattern = new(
        @"(?i)(Authorization\s*:\s*Bearer\s+)([^\s""',;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonSecretFieldPattern = new(
        @"""(?<key>[^""]*(?:token|secret|bearer|authorization)[^""]*)""\s*:\s*""(?<value>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BareGatewayHexTokenPattern = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LongBase64UrlPattern = new(
        @"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? string.Empty;

        var sanitized = AuthorizationBearerPattern.Replace(message, "$1[REDACTED]");
        sanitized = JsonSecretFieldPattern.Replace(
            sanitized,
            match => $"\"{match.Groups["key"].Value}\":\"[REDACTED]\"");
        sanitized = BareGatewayHexTokenPattern.Replace(sanitized, "[REDACTED_TOKEN]");
        return LongBase64UrlPattern.Replace(sanitized, "[REDACTED_TOKEN]");
    }
}
