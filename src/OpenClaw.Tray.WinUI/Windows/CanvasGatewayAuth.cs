namespace OpenClawTray.Windows;

public static class CanvasGatewayAuth
{
    private const string CanvasVirtualHostOrigin = "https://openclaw-canvas.local";

    public static bool ShouldAttachGatewayBearer(
        string? documentUri,
        string? requestUri,
        string? trustedGatewayOrigin,
        string? initiatorUri = null,
        string? pendingNativeNavigationUrl = null)
    {
        if (string.IsNullOrEmpty(requestUri) || string.IsNullOrEmpty(trustedGatewayOrigin))
            return false;

        if (!IsOriginMatch(requestUri, trustedGatewayOrigin))
            return false;

        // A missing Referer must not inherit the top-level document.
        // The only no-Referer request that may carry the bearer is the exact
        // URL this window is navigating to. HTML loaded with NavigateToString
        // does not set that URL.
        if (string.IsNullOrEmpty(initiatorUri))
            return IsSameNavigationTarget(requestUri, pendingNativeNavigationUrl);

        if (IsAboutBlank(initiatorUri))
            return false;

        return IsOriginMatch(initiatorUri, trustedGatewayOrigin) ||
            IsOriginMatch(initiatorUri, CanvasVirtualHostOrigin);
    }

    private static bool IsAboutBlank(string? uri) =>
        !string.IsNullOrEmpty(uri) &&
        (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
         uri.StartsWith("about:blank?", StringComparison.OrdinalIgnoreCase) ||
         uri.StartsWith("about:blank#", StringComparison.OrdinalIgnoreCase));

    private static bool IsSameNavigationTarget(string requestUri, string? pendingNativeNavigationUrl)
    {
        if (string.IsNullOrEmpty(pendingNativeNavigationUrl))
            return false;

        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var request) ||
            !Uri.TryCreate(pendingNativeNavigationUrl, UriKind.Absolute, out var pending))
        {
            return false;
        }

        // Fragments are not sent on the WebView resource request.
        return string.Equals(
            request.GetLeftPart(UriPartial.Query),
            pending.GetLeftPart(UriPartial.Query),
            StringComparison.OrdinalIgnoreCase);
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
