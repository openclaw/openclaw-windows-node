namespace OpenClaw.Shared;

/// <summary>Parameters for creating a distinct gateway session.</summary>
public sealed class SessionCreateRequest
{
    public string? Key { get; init; }
    public string? AgentId { get; init; }
    public string? ParentSessionKey { get; init; }
    public bool EmitCommandHooks { get; init; } = true;
    public bool? SucceedsParent { get; init; }
}

/// <summary>Typed result of <c>sessions.create</c>.</summary>
public sealed class SessionCreateResult
{
    public bool Ok { get; init; }
    public string? Key { get; init; }
    public string? SessionId { get; init; }
    public string? Error { get; init; }

    /// <summary>False when the connected gateway does not implement <c>sessions.create</c>.</summary>
    public bool IsSupported { get; init; } = true;
}

/// <summary>Terminal result of <c>sessions.reset</c>.</summary>
public sealed class SessionResetResult
{
    public bool Ok { get; init; }
    public string? Key { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
}

/// <summary>Terminal result of model-backed <c>sessions.compact</c>.</summary>
public sealed class SessionCompactResult
{
    public bool Ok { get; init; }
    public string? Key { get; init; }
    public bool Compacted { get; init; }
    public string? Reason { get; init; }
    public long? TokensBefore { get; init; }
    public long? TokensAfter { get; init; }
    public string? Error { get; init; }
}
