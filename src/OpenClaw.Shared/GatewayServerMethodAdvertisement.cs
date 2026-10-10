using System.Text.Json;

namespace OpenClaw.Shared;

public static class GatewayServerMethodAdvertisement
{
    public static string[] Parse(JsonElement hello)
    {
        if (hello.ValueKind != JsonValueKind.Object ||
            !hello.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object ||
            !features.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array)
            return [];
        return methods.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
            .Select(m => m.GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
