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
        uri = null;
        if (!IsEnabled)
            return false;

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string? NormalizeEndpoint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
