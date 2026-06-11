using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

internal static class CanvasGatewayUrlRewriter
{
    public static string? ToHttpOrigin(string? gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return null;

        var uri = new Uri(GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl));
        var httpScheme = uri.Scheme == "wss" ? "https" : "http";
        return $"{httpScheme}://{uri.Host}:{uri.Port}";
    }

    public static string Rewrite(string url, string? effectiveGatewayOrigin, string? configuredGatewayOrigin)
    {
        if (string.IsNullOrEmpty(effectiveGatewayOrigin))
            return url;

        if (url.StartsWith("/", StringComparison.Ordinal))
            return effectiveGatewayOrigin + url;

        var uri = new Uri(url);
        var urlOrigin = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

        if (IsGatewayOrigin(urlOrigin, effectiveGatewayOrigin, configuredGatewayOrigin) &&
            !urlOrigin.Equals(effectiveGatewayOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return effectiveGatewayOrigin + uri.PathAndQuery;
        }

        return url;
    }

    private static bool IsGatewayOrigin(string urlOrigin, string effectiveGatewayOrigin, string? configuredGatewayOrigin)
    {
        return urlOrigin.Equals(effectiveGatewayOrigin, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(configuredGatewayOrigin) &&
                urlOrigin.Equals(configuredGatewayOrigin, StringComparison.OrdinalIgnoreCase));
    }
}
