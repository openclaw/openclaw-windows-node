namespace OpenClawTray.Services;

internal static class OpenTelemetryEndpointProtocol
{
    public const string Grpc = "grpc";
    public const string HttpProtobuf = "http/protobuf";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, HttpProtobuf, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "otlp/http", StringComparison.OrdinalIgnoreCase))
        {
            return HttpProtobuf;
        }

        return Grpc;
    }

    public static string ToDisplayName(string? value) =>
        Normalize(value) == HttpProtobuf ? "OTLP/HTTP" : "OTLP/gRPC";

    public static string ToTelemetryValue(string? value) => Normalize(value);
}
