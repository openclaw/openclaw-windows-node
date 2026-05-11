namespace OpenClaw.Shared;

/// <summary>
/// Result of parsing an openclaw:// deep link URI.
/// </summary>
public record DeepLinkResult(string Path, string Query, Dictionary<string, string> Parameters);

/// <summary>
/// Pure parser for openclaw:// deep link URIs.
/// </summary>
public static class DeepLinkParser
{
    private const string Scheme = "openclaw://";

    public static DeepLinkResult? ParseDeepLink(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = uri[Scheme.Length..];
        var queryIndex = remainder.IndexOf('?');
        var query = queryIndex >= 0 ? remainder[(queryIndex + 1)..] : "";
        // Trim trailing slash AFTER splitting off the query so the
        // Windows-canonicalized form `openclaw://send/?args=...` (slash
        // BEFORE the `?`) yields path "send", not "send/".
        var path = (queryIndex >= 0 ? remainder[..queryIndex] : remainder).TrimEnd('/');

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
            {
                parameters[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
            }
        }

        return new DeepLinkResult(path, query, parameters);
    }

    public static string? GetQueryParam(string? query, string key)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(key))
            return null;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }
}
