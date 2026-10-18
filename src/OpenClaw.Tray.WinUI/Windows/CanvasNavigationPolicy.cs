using System.Text.RegularExpressions;

namespace OpenClawTray.Windows;

/// <summary>
/// Canvas present URL checks that do not need a WebView.
/// </summary>
public static class CanvasNavigationPolicy
{
    private static readonly Regex DangerousUrlPattern = new(
        @"^(file|javascript|data|vbscript):|" +
        @"^https?://(localhost|127\.|10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|169\.254\.)|" +
        @"^https?://\[(::1|0:0:0:0:0:0:0:1|::)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPresentableDataUrl(string? url)
    {
        // data:text/html is not a canvas document. Only text/plain (and the
        // RFC 2397 default of text/plain) can be presented. Callers must not
        // treat a rejected data URL as an exception.
        if (string.IsNullOrEmpty(url) ||
            !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = url.IndexOf(',');
        if (commaIndex < 5)
            return false;

        var header = url.Substring(5, commaIndex - 5);
        if (string.IsNullOrWhiteSpace(header))
        {
            // Defaults to text/plain;charset=US-ASCII per RFC 2397.
            return true;
        }

        var mediaType = header.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (string.IsNullOrEmpty(mediaType))
            return true;

        return mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNavigationTargetAllowed(string url, string? trustedGatewayOrigin = null)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return IsPresentableDataUrl(url);

        if (url.StartsWith("https://openclaw-canvas.local/", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("https://openclaw-canvas.local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(trustedGatewayOrigin) &&
            url.StartsWith(trustedGatewayOrigin, StringComparison.OrdinalIgnoreCase) &&
            (url.Length == trustedGatewayOrigin.Length ||
             url[trustedGatewayOrigin.Length] == '/' ||
             url[trustedGatewayOrigin.Length] == '?' ||
             url[trustedGatewayOrigin.Length] == '#'))
        {
            return true;
        }

        // Host-normalizing private/loopback guard. DangerousUrlPattern only blocks the
        // literal dotted-decimal spelling, so encoded IPv4, IPv6 loopback, and CGNAT slip through.
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) &&
            OpenClaw.Shared.CanvasUrlSafety.IsPrivateOrLoopbackHost(parsedUri.Host))
        {
            return false;
        }

        return !DangerousUrlPattern.IsMatch(url);
    }
}
