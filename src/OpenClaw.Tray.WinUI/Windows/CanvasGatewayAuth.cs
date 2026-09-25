namespace OpenClawTray.Windows;

public static class CanvasGatewayAuth
{
    private const string CanvasVirtualHostOrigin = "https://openclaw-canvas.local";

    public static bool ShouldAttachGatewayBearer(
        string? documentUri,
        string? requestUri,
        string? trustedGatewayOrigin,
        string? initiatorUri = null)
    {
        if (string.IsNullOrEmpty(requestUri) || string.IsNullOrEmpty(trustedGatewayOrigin))
            return false;

        if (!IsOriginMatch(requestUri, trustedGatewayOrigin))
            return false;

        // The top-level document is not the request initiator. An untrusted
        // frame on a trusted page must not receive the gateway bearer.
        // about:blank is the document while the first A2UI navigation is in flight.
        var principal = string.IsNullOrEmpty(initiatorUri) ? documentUri : initiatorUri;
        if (string.IsNullOrEmpty(principal))
            return false;

        if (principal.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            principal.StartsWith("about:blank?", StringComparison.OrdinalIgnoreCase) ||
            principal.StartsWith("about:blank#", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsOriginMatch(principal, trustedGatewayOrigin) ||
            IsOriginMatch(principal, CanvasVirtualHostOrigin);
    }

    private static bool IsOriginMatch(string uri, string origin)
    {
        return uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase) &&
            (uri.Length == origin.Length ||
             uri[origin.Length] == '/' ||
             uri[origin.Length] == '?' ||
             uri[origin.Length] == '#');
    }
}
