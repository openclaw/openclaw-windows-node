using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

/// <summary>
/// Pure helper for building gateway chat URLs.
/// Extracted from GatewayChatHelper so the URL logic is testable without WinUI dependencies.
/// </summary>
public static class GatewayChatUrlBuilder
{
    /// <summary>
    /// Build the HTTP(S) chat URL from a WebSocket gateway URL.
    /// Converts ws:// → http://, wss:// → https://, appends token and optional session key.
    /// </summary>
    public static bool TryBuildChatUrl(
        string gatewayUrl,
        string token,
        out string url,
        out string errorMessage,
        string? sessionKey = null)
    {
        url = string.Empty;
        errorMessage = string.Empty;

        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedGatewayUrl) ||
            !Uri.TryCreate(normalizedGatewayUrl, UriKind.Absolute, out var gatewayUri))
        {
            errorMessage = $"Invalid gateway URL: {gatewayUrl}";
            return false;
        }

        var webScheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";

        if (webScheme == "http" && !IsLocalHost(gatewayUri))
        {
            errorMessage = "Non-local gateways require a secure (wss://) connection for web chat.";
            return false;
        }

        var builder = new UriBuilder(gatewayUri)
        {
            Scheme = webScheme,
            Port = gatewayUri.Port
        };

        var baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
        url = $"{baseUrl}?token={Uri.EscapeDataString(token)}";

        if (!string.IsNullOrEmpty(sessionKey))
            url += $"&session={Uri.EscapeDataString(sessionKey)}";

        return true;
    }

    /// <summary>
    /// Checks whether the given URI points to a loopback address.
    /// </summary>
    public static bool IsLocalHost(Uri uri)
    {
        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
