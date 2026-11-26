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
        return FormatHttpOrigin(httpScheme, uri.Host, uri.Port);
    }

    /// <summary>
    /// True when both values are http(s) URLs with the same scheme, host, and port.
    /// Default ports match whether or not the URL text includes <c>:443</c> or <c>:80</c>.
    /// </summary>
    public static bool SharesHttpOrigin(string? url, string? origin)
    {
        if (!TryReadHttpOrigin(url, out var left) || !TryReadHttpOrigin(origin, out var right))
            return false;

        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;
    }

    public static string Rewrite(string url, string? effectiveGatewayOrigin, string? configuredGatewayOrigin)
    {
        if (string.IsNullOrEmpty(effectiveGatewayOrigin))
            return url;

        if (url.StartsWith("/", StringComparison.Ordinal))
            return effectiveGatewayOrigin + url;

        var uri = new Uri(url);
        if (uri.Scheme is not ("http" or "https"))
            return url;

        if (SharesHttpOrigin(url, configuredGatewayOrigin) &&
            !SharesHttpOrigin(url, effectiveGatewayOrigin))
        {
            return effectiveGatewayOrigin + uri.PathAndQuery;
        }

        return url;
    }

    private static string FormatHttpOrigin(string scheme, string host, int port)
    {
        var formattedHost = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        var omitDefaultPort = (scheme == "https" && port == 443) || (scheme == "http" && port == 80);
        return omitDefaultPort
            ? $"{scheme}://{formattedHost}"
            : $"{scheme}://{formattedHost}:{port}";
    }

    private static bool TryReadHttpOrigin(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
