using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record TailscaleServeStatusResult(bool RoutesToGateway, bool FunnelEnabled);

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
                !HasValidWebShape(root) ||
                !TryReadFunnelState(root, out var funnelEnabled))
            {
                return false;
            }

            parsed = new TailscaleServeStatusResult(
                RoutesToGateway: HasGatewayWebProxy(root, port, expectedEndpoint),
                FunnelEnabled: funnelEnabled);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static bool HasGatewayWebProxy(JsonElement root, int port, Uri? expectedEndpoint)
    {
        if (!root.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var webEndpoint in web.EnumerateObject())
        {
            if (!EndpointMatches(webEndpoint.Name, expectedEndpoint) ||
                webEndpoint.Value.ValueKind != JsonValueKind.Object ||
                !webEndpoint.Value.TryGetProperty("Handlers", out var handlers) ||
                handlers.ValueKind != JsonValueKind.Object ||
                !handlers.TryGetProperty("/", out var rootHandler) ||
                rootHandler.ValueKind != JsonValueKind.Object ||
                !rootHandler.TryGetProperty("Proxy", out var proxy) ||
                proxy.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (IsLoopbackGatewayProxy(proxy.GetString(), port))
                return true;
        }

        return false;
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

    private static bool IsLoopbackGatewayProxy(string? proxy, int port) =>
        proxy is not null &&
        Uri.TryCreate(proxy, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == port &&
        HasCanonicalRootPathAndNoSuffix(proxy) &&
        !HasUserInfoDelimiter(proxy) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

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
