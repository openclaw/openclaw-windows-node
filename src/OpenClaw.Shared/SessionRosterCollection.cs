// Canonical legacy roster filtering and authoritative collection admission.
// The Gateway client shares these rules between live roster projection and
// detached verification. Example: TryGetAuthoritativeRowCount(payload, rows, out count).
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Preserves legacy session maps without treating absent rosters as empty authority.</summary>
internal static class SessionRosterCollection
{
    internal static bool IsLegacySessionKey(string key) =>
        key is not ("recent" or "count" or "path" or "defaults" or "ts") &&
        (key.Equals("global", StringComparison.OrdinalIgnoreCase) ||
         key.Contains(':') || key.Contains("agent") || key.Contains("session"));

    /// <summary>Enumerates exactly the map entries used by the live roster parser.</summary>
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

    /// <summary>Counts canonical rows; the detached parser must still validate every row and key.</summary>
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
            // A wrapped empty map is a supported empty roster. A bare empty
            // object has no collection and cannot clear previously trusted state.
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
