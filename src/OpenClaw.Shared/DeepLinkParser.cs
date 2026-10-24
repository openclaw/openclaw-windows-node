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
    public static DeepLinkResult? ParseDeepLink(string? uri)
        => ParseDeepLink(uri, "openclaw");

    public static DeepLinkResult? ParseDeepLink(string? uri, string scheme)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        var prefix = $"{scheme}://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = uri[prefix.Length..];
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
                if (!TryDecode(kv[0], out var key) || !TryDecode(kv[1], out var value))
                    return null;
                parameters[key] = value;
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
                return TryDecode(kv[1], out var value) ? value : null;
            }
        }

        return null;
    }

    private static bool TryDecode(string value, out string decoded)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;
            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                decoded = string.Empty;
                return false;
            }
            index += 2;
        }

        decoded = Uri.UnescapeDataString(value);
        return true;
    }
}
