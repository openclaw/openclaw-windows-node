namespace OpenClawTray.Helpers;

public static class GatewayDashboardUrlBuilder
{
    public static string Build(
        string gatewayUrl,
        string? path,
        string? sharedGatewayToken,
        bool appendSharedGatewayToken,
        bool trustTailscaleAuth = false)
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

        if (trustTailscaleAuth)
        {
            fragment = RemoveSharedTokenFields(fragment);
        }
        else if (appendSharedGatewayToken && !string.IsNullOrEmpty(sharedGatewayToken))
        {
            var fields = GetFragmentFields(fragment)
                .Where(field => !IsSharedTokenField(field));
            fragment = "#" + string.Join("&", fields.Append("token=" + Uri.EscapeDataString(sharedGatewayToken)));
        }

        return url + fragment;
    }

    internal static bool HasSharedTokenFragment(string gatewayUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayUrl);
        var endpoint = new Uri(gatewayUrl
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute);
        return GetFragmentFields(endpoint.Fragment).Any(IsSharedTokenField);
    }

    private static string RemoveSharedTokenFields(string fragment)
    {
        var fields = GetFragmentFields(fragment).Where(field => !IsSharedTokenField(field)).ToArray();
        return fields.Length == 0 ? string.Empty : "#" + string.Join("&", fields);
    }

    private static IEnumerable<string> GetFragmentFields(string fragment) =>
        fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsSharedTokenField(string field)
    {
        var separator = field.IndexOf('=');
        var key = separator < 0 ? field : field[..separator];
        return Uri.UnescapeDataString(key).Equals("token", StringComparison.OrdinalIgnoreCase);
    }
}
