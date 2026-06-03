using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private static readonly Regex PathWindowsUserPattern = new(
        @"\b[A-Za-z]:\\Users\\[^\\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PathUnixUserPattern = new(
        @"/Users/[^/\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlHostPattern = new(
        @"\b[a-z][a-z0-9+.-]*://(?:[^@\s/]+@)?(?<host>[^:/\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex IpAddressPattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new(
        @"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UserAtHostPattern = new(
        @"\b(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9._-]+)(?=[:\s]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HostAfterToPattern = new(
        @"(?<=\bto\s)[A-Za-z0-9._-]+(?=:\d{1,5}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LeadingHostPattern = new(
        @"^\s*[A-Za-z0-9._-]+(?=:\d{1,5}\b)",
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

    public static string SanitizeLogMessage(string? message)
    {
        var sanitized = Sanitize(message);
        if (string.IsNullOrEmpty(sanitized))
            return sanitized;

        sanitized = RedactLocalPaths(sanitized);
        sanitized = UrlHostPattern.Replace(
            sanitized,
            match => match.Value.Replace(match.Groups["host"].Value, "<host>"));
        sanitized = IpAddressPattern.Replace(sanitized, "<ip>");
        sanitized = EmailPattern.Replace(sanitized, "<email>");
        sanitized = UserAtHostPattern.Replace(sanitized, "<user>@<host>");
        sanitized = HostAfterToPattern.Replace(sanitized, "<host>");
        return LeadingHostPattern.Replace(sanitized, "<host>");
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
