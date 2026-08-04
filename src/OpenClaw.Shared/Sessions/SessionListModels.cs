using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Stable <c>sessions.list</c> request contract from OpenClaw v2026.5.22.
/// Keep this DTO aligned with the pinned protocol snapshot.
/// </summary>
public sealed class SessionListRequest
{
    [JsonPropertyName("agentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }

    [JsonPropertyName("limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; init; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; init; }

    [JsonPropertyName("search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Search { get; init; }

    [JsonPropertyName("configuredAgentsOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConfiguredAgentsOnly { get; init; }

    internal Dictionary<string, object?> ToParameters()
    {
        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(AgentId)) parameters["agentId"] = AgentId;
        if (Limit.HasValue) parameters["limit"] = Limit.Value;
        if (Offset.HasValue) parameters["offset"] = Offset.Value;
        if (!string.IsNullOrWhiteSpace(Search)) parameters["search"] = Search;
        if (ConfiguredAgentsOnly.HasValue) parameters["configuredAgentsOnly"] = ConfiguredAgentsOnly.Value;
        return parameters;
    }

    internal object? ToLegacyParameters() =>
        string.IsNullOrWhiteSpace(AgentId) ? null : new { agentId = AgentId };
}

/// <summary>
/// Stable <c>sessions.list</c> response contract from OpenClaw v2026.5.22.
/// Nullable metadata deliberately tolerates older and additive gateway shapes.
/// </summary>
public sealed class SessionListResult
{
    [JsonPropertyName("sessions")]
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("limitApplied")]
    public int? LimitApplied { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }

    [JsonPropertyName("hasMore")]
    public bool? HasMore { get; init; }

    [JsonIgnore]
    public bool IsLegacyResponse { get; init; }
}

/// <summary>UI-neutral query accepted by the session discovery boundary.</summary>
public sealed class SessionQuery
{
    public string? AgentId { get; init; }
    public string? Search { get; init; }
    public bool ConfiguredAgentsOnly { get; init; }
    public bool IncludeBackground { get; init; }
    public IReadOnlyList<SessionInfo> PinnedSessions { get; init; } = Array.Empty<SessionInfo>();
}

public enum SessionSearchExecutionMode
{
    None,
    Server,
    LegacyLocal,
}

/// <summary>One coherent, bounded session discovery snapshot.</summary>
public sealed class SessionQuerySnapshot
{
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    public string? Search { get; init; }
    public int ConnectionGeneration { get; init; }
    public int PagesRead { get; init; }
    public bool IsLegacyResponse { get; init; }
    public SessionSearchExecutionMode SearchExecutionMode { get; init; }

    internal IReadOnlyList<SessionInfo> MaterializedSessions { get; init; } = Array.Empty<SessionInfo>();
    internal long RequestIdentity { get; init; }
}
