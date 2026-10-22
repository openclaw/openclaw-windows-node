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

        var endpoint = new Uri(gatewayUrl
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute);
        if (endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Dashboard requires an HTTP endpoint.", nameof(gatewayUrl));

        var baseUrl = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var url = string.IsNullOrWhiteSpace(path)
            ? baseUrl + endpoint.Query
            : $"{baseUrl}/{path.TrimStart('/')}";
        var fragment = endpoint.Fragment;

        if (appendSharedGatewayToken && !string.IsNullOrEmpty(sharedGatewayToken))
        {
            var fields = fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(field => !Uri.UnescapeDataString(field.Split('=', 2)[0])
                    .Equals("token", StringComparison.OrdinalIgnoreCase));
            fragment = "#" + string.Join("&", fields.Append("token=" + Uri.EscapeDataString(sharedGatewayToken)));
        }

        return url + fragment;
    }
}
