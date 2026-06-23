using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

/// <summary>The kind of content a single wizard message line carries.</summary>
public enum WizardLineKind
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>Contains an http(s) URL (e.g. an OAuth/device-authorization link).</summary>
    Url,

    /// <summary>A "Code: XXXX" style device/pairing code the user must enter elsewhere.</summary>
    Code,
}

/// <summary>A classified wizard message line with an optional highlighted URL or code.</summary>
public sealed record WizardLineSegment(
    WizardLineKind Kind,
    string Text,
    string Prefix,
    string Highlight,
    string Suffix);

/// <summary>Detects actionable URLs and manual entry codes in wizard messages.</summary>
public static class WizardMessageFormatting
{
    // "Code: ABCD-EFGH" / "user_code = ABC123" style manual entry codes.
    private static readonly Regex s_codeRegex = new(
        @"^((?:Code|code|user_code|USER_CODE|User Code)\s*[:=]\s*)([A-Z0-9]{2,8}(?:-[A-Z0-9]{2,8})+|[A-Z0-9]{4,12})\b",
        RegexOptions.Compiled);

    private static readonly Regex s_urlRegex = new(
        @"https?://[^\s\)\""]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Classifies a line as plain text, URL, or device-code text.</summary>
    public static WizardLineSegment ClassifyLine(string? line)
    {
        var trimmed = (line ?? string.Empty).TrimEnd('\r');

        var codeMatch = s_codeRegex.Match(trimmed);
        if (codeMatch.Success)
        {
            return new WizardLineSegment(
                WizardLineKind.Code,
                trimmed,
                Prefix: codeMatch.Groups[1].Value,
                Highlight: codeMatch.Groups[2].Value,
                Suffix: string.Empty);
        }

        var urlMatch = s_urlRegex.Match(trimmed);
        if (urlMatch.Success)
        {
            var url = urlMatch.Value.TrimEnd('.', ',');
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                var index = trimmed.IndexOf(urlMatch.Value, StringComparison.Ordinal);
                var prefix = index > 0 ? trimmed[..index] : string.Empty;
                var suffix = trimmed[(index + urlMatch.Value.Length)..];
                return new WizardLineSegment(WizardLineKind.Url, trimmed, prefix, url, suffix);
            }
        }

        return new WizardLineSegment(WizardLineKind.Text, trimmed, trimmed, string.Empty, string.Empty);
    }

    /// <summary>Returns all distinct absolute http(s) URLs found in a message.</summary>
    public static IReadOnlyList<string> ExtractUrls(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Array.Empty<string>();

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in s_urlRegex.Matches(message))
        {
            var url = match.Value.TrimEnd('.', ',');
            if (Uri.TryCreate(url, UriKind.Absolute, out _) && seen.Add(url))
                urls.Add(url);
        }

        return urls;
    }

    /// <summary>True when the message contains at least one actionable auth URL.</summary>
    public static bool ContainsAuthUrl(string? message) => ExtractUrls(message).Count > 0;
}
