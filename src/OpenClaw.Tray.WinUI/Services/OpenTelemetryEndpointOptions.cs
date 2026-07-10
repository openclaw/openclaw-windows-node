namespace OpenClawTray.Services;

internal sealed record OpenTelemetryEndpointOptions(string? Endpoint, string Protocol)
{
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint);

    public static OpenTelemetryEndpointOptions FromSettings(SettingsManager? settings) =>
        settings == null
            ? Disabled
            : Create(settings.OpenTelemetryEndpoint, settings.OpenTelemetryProtocol);

    public static OpenTelemetryEndpointOptions Create(string? endpoint, string? protocol) =>
        new(NormalizeEndpoint(endpoint), OpenTelemetryEndpointProtocol.Normalize(protocol));

    public static OpenTelemetryEndpointOptions Disabled { get; } =
        new(null, OpenTelemetryEndpointProtocol.Grpc);

    public bool TryGetEndpointUri(out Uri? uri)
    {
        if (!IsEnabled)
        {
            uri = null;
            return false;
        }

        return TryCreateEndpointUri(Endpoint, out uri);
    }

    internal static bool TryCreateEndpointUri(string? endpoint, out Uri? uri)
    {
        uri = null;
        var normalized = NormalizeEndpoint(endpoint);
        if (normalized == null)
            return false;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string? NormalizeEndpoint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
