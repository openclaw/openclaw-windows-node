using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>Agent heartbeat outcome, not a transport ping or last received packet.</summary>
public sealed record GatewayHeartbeatEvent
{
    [JsonRequired, JsonPropertyName("ts")]
    public long Timestamp { get; init; }
    [JsonRequired, JsonPropertyName("status")]
    public required string Status { get; init; }
    [JsonPropertyName("to")]
    public string? To { get; init; }
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }
    [JsonPropertyName("preview")]
    public string? Preview { get; init; }
    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; init; }
    [JsonPropertyName("hasMedia")]
    public bool? HasMedia { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("message")]
    public string? Message { get; init; }
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }
    [JsonPropertyName("silent")]
    public bool? Silent { get; init; }
    [JsonPropertyName("indicatorType")]
    public string? IndicatorType { get; init; }

    public static GatewayHeartbeatEvent? Parse(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Null)
            return null;
        if (payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("Invalid Gateway heartbeat payload.");
        var result = payload.Deserialize<GatewayHeartbeatEvent>()
            ?? throw new JsonException("Missing Gateway heartbeat payload.");
        if (string.IsNullOrWhiteSpace(result.Status) ||
            result.Timestamp < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
            result.Timestamp > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            result.DurationMs is { } duration && !double.IsFinite(duration))
            throw new JsonException("Invalid Gateway heartbeat fields.");
        return result;
    }
}
