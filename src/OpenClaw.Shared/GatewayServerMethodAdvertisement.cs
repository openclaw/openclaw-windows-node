using System.Text.Json;

namespace OpenClaw.Shared;

public static class GatewayServerMethodAdvertisement
{
    public static string[] Parse(JsonElement hello) => ParseFeatureStrings(hello, "methods");

    public static string[] ParseCapabilities(JsonElement hello) =>
        ParseFeatureStrings(hello, "capabilities");

    private static string[] ParseFeatureStrings(JsonElement hello, string property)
    {
        if (hello.ValueKind != JsonValueKind.Object ||
            !hello.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object ||
            !features.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(value => value.GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
