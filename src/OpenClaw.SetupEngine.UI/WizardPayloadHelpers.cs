using System.Text.Json;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Static helpers for sanitizing upstream openclaw wizard payloads and deciding
/// when to auto-open URLs in the user's browser. Kept free of XAML/Windows.System
/// dependencies so they can run in xUnit on any host.
/// </summary>
internal static class WizardPayloadHelpers
{
    /// <summary>
    /// Reads the <c>message</c> field of a wizard step. Upstream is supposed to
    /// send a string; the Gemini CLI OAuth plugin (and possibly others) nests a
    /// note object instead, e.g.
    /// <code>{"type":"text","message":{"type":"note","title":"...","message":"..."}}</code>
    /// Returns the string as-is, "Title\n\nMessage" for a nested object, or
    /// empty (safer than rendering raw JSON in the UI).
    /// </summary>
    public static string ExtractStepMessage(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!step.TryGetProperty("message", out var msg)) return string.Empty;

        return msg.ValueKind switch
        {
            JsonValueKind.String => msg.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Object => FlattenNestedNote(msg),
            _ => string.Empty,
        };
    }

    private static string FlattenNestedNote(JsonElement nested)
    {
        var title = nested.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? (t.GetString() ?? string.Empty)
            : string.Empty;
        var message = nested.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? (m.GetString() ?? string.Empty)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(message))
            return $"{title}\n\n{message}";
        if (!string.IsNullOrWhiteSpace(title))
            return title;
        if (!string.IsNullOrWhiteSpace(message))
            return message;
        return string.Empty;
    }
}

/// <summary>
/// Decides whether the tray should auto-launch a URL in the user's default
/// browser. Conservative — only ones that look like OAuth/device-code flows
/// the user is actively asked to complete (docs links and similar are skipped).
/// </summary>
internal static class WizardUrlLauncher
{
    private static readonly string[] KnownAuthHosts =
    {
        "auth.openai.com",
        "auth.x.ai",
        "accounts.google.com",
        "login.anthropic.com",
        "console.anthropic.com",
        "claude.ai",
        "github.com",
        "auth.openclaw.ai",
    };

    /// <summary>
    /// Mutates <paramref name="seen"/> to dedupe; returns true the first time a
    /// URL is observed and recognized as an auth/oauth URL, false otherwise
    /// (already launched, not http(s), or doesn't look like an OAuth flow).
    /// </summary>
    public static bool ShouldLaunch(HashSet<string> seen, Uri uri)
    {
        if (uri is null) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!LooksLikeOAuthUrl(uri)) return false;
        return seen.Add(uri.AbsoluteUri);
    }

    public static bool LooksLikeOAuthUrl(Uri uri)
    {
        if (uri is null) return false;

        foreach (var host in KnownAuthHosts)
        {
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)) return true;
        }

        var absolute = uri.AbsoluteUri.ToLowerInvariant();
        if (absolute.Contains("/oauth")) return true;
        if (absolute.Contains("/authorize")) return true;
        if (absolute.Contains("response_type=code")) return true;
        if (absolute.Contains("device/code")) return true;
        if (absolute.Contains("device_code")) return true;

        return false;
    }
}
