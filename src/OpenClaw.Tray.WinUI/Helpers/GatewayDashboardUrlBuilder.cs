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
        var routeFragment = fragmentStart >= 0 ? route[fragmentStart..] : string.Empty;
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

        // The SPA prefers the first query session, even when empty, over a fragment session.
        var session = FindSessionParameter(query) is null
            ? FindSessionParameter(routeFragment) ?? FindSessionParameter(uri.Fragment)
            : null;
        var fragment = new List<string>();
        if (session is not null)
            fragment.Add(session);
        AppendPreservedFragmentFields(fragment, routeFragment);
        AppendPreservedFragmentFields(fragment, uri.Fragment);
        if (appendSharedGatewayToken && !string.IsNullOrEmpty(sharedGatewayToken))
            fragment.Add($"token={Uri.EscapeDataString(sharedGatewayToken)}");
        else if (appendSharedGatewayToken)
        {
            var existingToken = FindNamedParameter(routeFragment, "token")
                ?? FindNamedParameter(uri.Fragment, "token");
            if (existingToken is not null)
                fragment.Add(existingToken);
        }

        return fragment.Count == 0 ? url : $"{url}#{string.Join('&', fragment)}";
    }

    public static bool TryBuild(
        string gatewayUrl,
        string? path,
        string? sharedGatewayToken,
        bool appendSharedGatewayToken,
        out string url,
        out string error)
    {
        try
        {
            url = Build(gatewayUrl, path, sharedGatewayToken, appendSharedGatewayToken);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            url = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static bool IsDashboardScheme(string scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);

    private static string? FindSessionParameter(string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
            return null;

        var body = parameters[0] is '?' or '#' ? parameters[1..] : parameters;
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var nameEnd = part.IndexOf('=');
            var name = nameEnd >= 0 ? part[..nameEnd] : part;
            if (Uri.UnescapeDataString(name).Equals("session", StringComparison.Ordinal))
                return nameEnd >= 0 ? $"session{part[nameEnd..]}" : "session=";
        }

        return null;
    }

    private static void AppendPreservedFragmentFields(List<string> fragment, string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
            return;

        var body = parameters[0] is '?' or '#' ? parameters[1..] : parameters;
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var nameEnd = part.IndexOf('=');
            if (nameEnd < 0)
                continue;

            var name = Uri.UnescapeDataString(part[..nameEnd]);
            if (!name.Equals("view", StringComparison.OrdinalIgnoreCase))
                continue;

            fragment.Add(part);
        }
    }

    private static string? FindNamedParameter(string parameters, string parameterName)
    {
        if (string.IsNullOrEmpty(parameters))
            return null;

        var body = parameters[0] is '?' or '#' ? parameters[1..] : parameters;
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var nameEnd = part.IndexOf('=');
            var name = nameEnd >= 0 ? part[..nameEnd] : part;
            if (Uri.UnescapeDataString(name).Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                return part;
        }

        return null;
    }

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
