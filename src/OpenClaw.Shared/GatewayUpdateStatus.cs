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
    /// Parses the additive <c>effectiveChannel</c> field from <c>update.status</c>.
    /// Older Gateways omit the field, which preserves the companion updater path.
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
