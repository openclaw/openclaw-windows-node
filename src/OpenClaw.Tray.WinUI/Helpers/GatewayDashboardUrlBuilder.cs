namespace OpenClawTray.Helpers;

public static class GatewayDashboardUrlBuilder
{
    public static string Build(
        string gatewayUrl,
        string? path,
        string? sharedGatewayToken,
        bool appendSharedGatewayToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayUrl);

        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var uri) ||
            !IsDashboardScheme(uri.Scheme))
            throw new ArgumentException(
                "Gateway URL must be an absolute http, https, ws, or wss URL.",
                nameof(gatewayUrl));

        var route = path?.Trim() ?? string.Empty;
        var fragmentStart = route.IndexOf('#');
        if (fragmentStart >= 0)
            route = route[..fragmentStart];

        var queryStart = route.IndexOf('?');
        var routePath = queryStart < 0 ? route : route[..queryStart];
        var routeQuery = WithoutTokenQuery(queryStart < 0 ? string.Empty : route[queryStart..]);
        var baseQuery = WithoutTokenQuery(uri.Query);
        var query = routeQuery + (baseQuery.Length == 0
            ? string.Empty
            : routeQuery.Length == 0 ? baseQuery : "&" + baseQuery[1..]);

        var scheme = ToHttpScheme(uri.Scheme);
        var url = $"{scheme}://{FormatHost(uri)}{FormatPort(scheme, uri.Port)}{JoinPath(uri.AbsolutePath, routePath)}{query}";

        if (appendSharedGatewayToken && !string.IsNullOrEmpty(sharedGatewayToken))
            url += $"#token={Uri.EscapeDataString(sharedGatewayToken)}";

        return url;
    }

    private static bool IsDashboardScheme(string scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);

    private static string ToHttpScheme(string scheme)
    {
        if (scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return "https";
        }

        return "http";
    }

    private static string FormatHost(Uri uri)
    {
        if (uri.HostNameType == UriHostNameType.IPv6)
            return $"[{uri.IdnHost}]";

        return uri.IdnHost;
    }

    private static string FormatPort(string scheme, int port)
    {
        if (port <= 0)
            return string.Empty;

        if ((scheme == "http" && port == 80) || (scheme == "https" && port == 443))
            return string.Empty;

        return $":{port}";
    }

    private static string JoinPath(string absolutePath, string? route)
    {
        var path = string.IsNullOrEmpty(absolutePath) ? "/" : absolutePath;
        if (!string.IsNullOrWhiteSpace(route))
            path = $"{path.TrimEnd('/')}/{route.Trim().TrimStart('/')}";

        if (path.Length > 1)
            path = path.TrimEnd('/');

        return path == "/" ? string.Empty : path;
    }

    private static string WithoutTokenQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
            return string.Empty;

        var body = query[0] == '?' ? query[1..] : query;
        var kept = new List<string>();
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var nameEnd = part.IndexOf('=');
            var name = nameEnd >= 0 ? part[..nameEnd] : part;
            if (Uri.UnescapeDataString(name).Equals("token", StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(part);
        }

        return kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);
    }
}
