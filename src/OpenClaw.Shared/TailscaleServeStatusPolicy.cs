using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record TailscaleServeStatusResult(
    bool RoutesToGateway,
    bool FunnelEnabled,
    int? ForegroundProxyPort = null);

public static class TailscaleServeStatusPolicy
{
    public static bool TryParse(
        string status,
        int port,
        Uri? expectedEndpoint,
        out TailscaleServeStatusResult parsed)
    {
        parsed = new TailscaleServeStatusResult(false, false);
        if (port is <= 0 or > 65535)
            return false;

        try
        {
            using var document = JsonDocument.Parse(status);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryCollectServeConfigs(root, out var configs))
            {
                return false;
            }

            var routesToGateway = false;
            var funnelEnabled = false;
            int? matchedProxyPort = null;
            int? foregroundProxyPort = null;
            var foregroundMatchCount = 0;
            var unsafeMatchingRoute = false;
            foreach (var config in configs)
            {
                if (!HasValidWebShape(config.Value) ||
                    !TryReadFunnelState(config.Value, out var configFunnelEnabled))
                {
                    return false;
                }
                funnelEnabled |= configFunnelEnabled;

                var routeMatch = TryGetGatewayWebProxyPort(
                    config.Value,
                    expectedEndpoint,
                    out var proxyPort,
                    out var isLiteralIpv4Loopback);
                if (routeMatch == GatewayWebProxyMatch.Invalid)
                {
                    unsafeMatchingRoute = true;
                    continue;
                }

                if (routeMatch == GatewayWebProxyMatch.Valid &&
                    config.IsForeground &&
                    !isLiteralIpv4Loopback)
                {
                    unsafeMatchingRoute = true;
                    continue;
                }

                if (routeMatch == GatewayWebProxyMatch.Valid)
                {
                    if (matchedProxyPort is { } existingPort && existingPort != proxyPort)
                        return false;

                    matchedProxyPort = proxyPort;
                    routesToGateway |= proxyPort == port;
                    if (config.IsForeground)
                    {
                        foregroundMatchCount++;
                        if (foregroundMatchCount > 1)
                            return false;
                        foregroundProxyPort = proxyPort;
                    }
                }
            }

            if (unsafeMatchingRoute)
            {
                routesToGateway = false;
                foregroundProxyPort = null;
            }

            parsed = new TailscaleServeStatusResult(
                RoutesToGateway: routesToGateway,
                FunnelEnabled: funnelEnabled,
                ForegroundProxyPort: foregroundProxyPort);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryCollectServeConfigs(
        JsonElement root,
        out IReadOnlyList<ServeConfig> configs)
    {
        var collected = new List<ServeConfig> { new(root, IsForeground: false) };
        if (!root.TryGetProperty("Foreground", out var foreground))
        {
            configs = collected;
            return true;
        }

        if (foreground.ValueKind != JsonValueKind.Object)
        {
            configs = [];
            return false;
        }

        foreach (var entry in foreground.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                configs = [];
                return false;
            }

            collected.Add(new ServeConfig(entry.Value, IsForeground: true));
        }

        configs = collected;
        return true;
    }

    private static bool HasValidWebShape(JsonElement root)
    {
        if (!root.TryGetProperty("Web", out var web))
            return true;
        if (web.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var endpoint in web.EnumerateObject())
        {
            if (endpoint.Value.ValueKind != JsonValueKind.Object)
                return false;
            if (endpoint.Value.TryGetProperty("Handlers", out var handlers) &&
                handlers.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (endpoint.Value.TryGetProperty("Handlers", out handlers))
            {
                foreach (var handler in handlers.EnumerateObject())
                {
                    if (handler.Value.ValueKind != JsonValueKind.Object ||
                        (handler.Value.TryGetProperty("Proxy", out var proxy) &&
                         proxy.ValueKind != JsonValueKind.String))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static GatewayWebProxyMatch TryGetGatewayWebProxyPort(
        JsonElement root,
        Uri? expectedEndpoint,
        out int proxyPort,
        out bool isLiteralIpv4Loopback)
    {
        proxyPort = 0;
        isLiteralIpv4Loopback = false;
        if (!root.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
            return GatewayWebProxyMatch.None;

        var found = false;
        foreach (var webEndpoint in web.EnumerateObject())
        {
            if (!EndpointMatches(webEndpoint.Name, expectedEndpoint))
                continue;

            if (found ||
                webEndpoint.Value.ValueKind != JsonValueKind.Object ||
                !webEndpoint.Value.TryGetProperty("Handlers", out var handlers) ||
                handlers.ValueKind != JsonValueKind.Object ||
                !handlers.TryGetProperty("/", out var rootHandler) ||
                rootHandler.ValueKind != JsonValueKind.Object ||
                !rootHandler.TryGetProperty("Proxy", out var proxy) ||
                proxy.ValueKind != JsonValueKind.String)
            {
                return GatewayWebProxyMatch.Invalid;
            }

            if (!TryReadLoopbackGatewayProxyPort(
                proxy.GetString(),
                out proxyPort,
                out isLiteralIpv4Loopback))
            {
                return GatewayWebProxyMatch.Invalid;
            }

            found = true;
        }

        return found ? GatewayWebProxyMatch.Valid : GatewayWebProxyMatch.None;
    }

    private static bool EndpointMatches(string endpoint, Uri? expectedEndpoint)
    {
        if (expectedEndpoint is null)
            return true;

        var candidate = endpoint.Contains("://", StringComparison.Ordinal)
            ? endpoint
            : $"https://{endpoint}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !HasUserInfoDelimiter(candidate) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            HasCanonicalRootPathAndNoSuffix(candidate) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            string.Equals(
                uri.Host.TrimEnd('.'),
                expectedEndpoint.Host.TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase) &&
            uri.Port == expectedEndpoint.Port;
    }

    private static bool TryReadFunnelState(JsonElement root, out bool enabled)
    {
        enabled = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals("AllowFunnel") && !property.NameEquals("Funnel"))
                continue;
            if (!TryContainsEnabledFunnelValue(property.Value, out var propertyEnabled))
                return false;
            enabled |= propertyEnabled;
        }

        return true;
    }

    private static bool TryContainsEnabledFunnelValue(JsonElement value, out bool enabled)
    {
        enabled = false;
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                enabled = true;
                return true;
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                enabled = !string.IsNullOrWhiteSpace(value.GetString());
                return true;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryContainsEnabledFunnelValue(item, out var itemEnabled))
                        return false;
                    enabled |= itemEnabled;
                }
                return true;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (!TryContainsEnabledFunnelValue(property.Value, out var propertyEnabled))
                        return false;
                    enabled |= propertyEnabled;
                }
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadLoopbackGatewayProxyPort(
        string? proxy,
        out int port,
        out bool isLiteralIpv4Loopback)
    {
        port = 0;
        isLiteralIpv4Loopback = false;
        if (proxy is null ||
            !Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Port is <= 0 or > 65535 ||
            !HasCanonicalRootPathAndNoSuffix(proxy) ||
            HasUserInfoDelimiter(proxy) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        port = uri.Port;
        isLiteralIpv4Loopback = uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private readonly record struct ServeConfig(JsonElement Value, bool IsForeground);

    private enum GatewayWebProxyMatch
    {
        None,
        Valid,
        Invalid,
    }

    private static bool HasUserInfoDelimiter(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return false;
        authorityStart += 3;

        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
            authorityEnd = value.Length;
        return value.AsSpan(authorityStart, authorityEnd - authorityStart).Contains('@');
    }

    private static bool HasCanonicalRootPathAndNoSuffix(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return false;
        authorityStart += 3;

        var suffixStart = value.IndexOfAny(['/', '\\', '?', '#'], authorityStart);
        return suffixStart < 0 || value.AsSpan(suffixStart).SequenceEqual("/");
    }
}
