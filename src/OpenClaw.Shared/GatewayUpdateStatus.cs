using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// The Gateway's authoritative update track for its installed OpenClaw runtime.
/// </summary>
public sealed class GatewayUpdateStatus
{
    public string? EffectiveChannel { get; init; }
}

public static class GatewayUpdateStatusParser
{
    /// <summary>
    /// Parses the optional <c>effectiveChannel</c> field from <c>update.status</c>.
    /// Missing or malformed fields remain unverified so callers can fail open.
    /// </summary>
    public static GatewayUpdateStatus Parse(JsonElement payload) => new()
    {
        EffectiveChannel = payload.ValueKind == JsonValueKind.Object &&
                           payload.TryGetProperty("effectiveChannel", out var channel) &&
                           channel.ValueKind == JsonValueKind.String
            ? channel.GetString()
            : null
    };
}
