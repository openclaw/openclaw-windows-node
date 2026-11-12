// Canonical legacy roster filtering and authoritative collection admission.
// The Gateway client shares these rules between live roster projection and
// detached verification. Example: TryGetAuthoritativeRowCount(payload, rows, out count).
using System.Text.Json;

namespace OpenClaw.Shared;

internal static class SessionRosterCollection
{
    internal static bool IsLegacySessionKey(string key) =>
        key is not ("recent" or "count" or "path" or "defaults" or "ts") &&
        (key.Equals("global", StringComparison.OrdinalIgnoreCase) ||
         key.Contains(':') || key.Contains("agent") || key.Contains("session"));

    internal static IEnumerable<JsonProperty> EnumerateLegacyRows(JsonElement rows)
    {
        foreach (var property in rows.EnumerateObject())
        {
            if (!IsLegacySessionKey(property.Name) || property.Value.ValueKind == JsonValueKind.Number)
                continue;
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString() ?? string.Empty;
                if (text.StartsWith("/") || text.Contains("/."))
                    continue;
            }
            yield return property;
        }
    }

    internal static bool TryGetAuthoritativeRowCount(JsonElement payload, JsonElement rows, out int count)
    {
        count = 0;
        if (rows.ValueKind == JsonValueKind.Array)
        {
            count = rows.GetArrayLength();
            return true;
        }
        if (rows.ValueKind != JsonValueKind.Object)
            return false;

        if (!rows.EnumerateObject().Any())
        {
            return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("sessions", out _);
        }

        var entries = EnumerateLegacyRows(rows).ToArray();
        if (entries.Length == 0 || entries.Any(property =>
                property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.String)))
            return false;

        count = entries.Length;
        return true;
    }
}
