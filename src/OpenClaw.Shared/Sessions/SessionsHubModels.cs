using System.Globalization;

namespace OpenClaw.Shared.Sessions;

/// <summary>Archive membership selected by the Sessions page status filter.</summary>
public enum SessionArchiveFilter
{
    Active,
    Archived,
    All,
}

/// <summary>
/// One Sessions page roster request. Mirrors the upstream <c>sessions.list</c>
/// filter params; <see cref="ToSearchScope"/> reuses the same membership filters
/// for <c>sessions.search</c> so transcript search covers the visible roster.
/// </summary>
public sealed record SessionListQuery
{
    public const int DefaultLimit = 50;

    public string? AgentId { get; init; }
    public string? Search { get; init; }
    public SessionArchiveFilter Archive { get; init; } = SessionArchiveFilter.Active;
    /// <summary>Applied only to the Active view; archived recency is not meaningful.</summary>
    public int? ActiveMinutes { get; init; }
    public bool IncludeGlobal { get; init; } = true;
    public bool IncludeUnknown { get; init; }
    public int Limit { get; init; } = DefaultLimit;
    public int Offset { get; init; }

    /// <param name="includePageSource">
    /// Sends <c>source</c> and <c>excludeDock</c>; Gateways before 2026.10.5 reject them.
    /// </param>
    public Dictionary<string, object?> ToParameters(bool includePageSource = true)
    {
        var parameters = ToSearchScope(excludeDock: includePageSource);
        if (includePageSource)
            parameters["source"] = "sessions-page";
        parameters["limit"] = Limit > 0 ? Limit : DefaultLimit;
        if (Offset > 0)
            parameters["offset"] = Offset;
        parameters["includeDerivedTitles"] = true;
        return parameters;
    }

    /// <param name="excludeDock">Hides dock sessions like the roster does; Gateways before 2026.10.5 reject it.</param>
    public Dictionary<string, object?> ToSearchScope(bool excludeDock = false)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["includeGlobal"] = IncludeGlobal,
            ["includeUnknown"] = IncludeUnknown,
        };
        if (excludeDock)
            parameters["excludeDock"] = true;
        if (!string.IsNullOrWhiteSpace(AgentId))
            parameters["agentId"] = AgentId.Trim();
        if (!string.IsNullOrWhiteSpace(Search))
            parameters["search"] = Search.Trim();
        if (Archive == SessionArchiveFilter.Active && ActiveMinutes is > 0)
            parameters["activeMinutes"] = ActiveMinutes.Value;
        if (Archive == SessionArchiveFilter.Archived)
            parameters["archived"] = true;
        else if (Archive == SessionArchiveFilter.All)
            parameters["archived"] = "all";
        return parameters;
    }

    /// <summary>Strict positive-integer form input; anything else means "not set".</summary>
    public static int? ParsePositiveInteger(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
}

/// <summary>One <c>sessions.list</c> page of detached rows.</summary>
public sealed class SessionListPage
{
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    public int? TotalCount { get; init; }
    public bool HasMore { get; init; }
    public int? NextOffset { get; init; }
    public bool IsSupported { get; init; } = true;
}

/// <summary>One full-text transcript match (<c>sessions.search</c>).</summary>
public sealed record SessionSearchHit(
    string SessionKey,
    string? MessageId,
    string Role,
    long Timestamp,
    string Snippet);

public sealed class SessionSearchResult
{
    public IReadOnlyList<SessionSearchHit> Hits { get; init; } = Array.Empty<SessionSearchHit>();
    /// <summary>Visible rows referenced by the hits, when the Gateway includes them.</summary>
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    /// <summary>The first-use index is still being built; results may be incomplete.</summary>
    public bool Indexing { get; init; }
    public bool Truncated { get; init; }
    public int ArchivedTranscriptsExcluded { get; init; }
    public bool IsSupported { get; init; } = true;
}

/// <summary>Gateway-owned custom group catalog in display order.</summary>
public sealed class SessionGroupCatalog
{
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
    public bool IsSupported { get; init; } = true;
}

/// <summary>A managed worktree a deletion left in place, with the Gateway's reason.</summary>
public sealed record PreservedWorktreeInfo(string Id, string Branch, string Path, string Reason);

/// <summary>Response-aware <c>sessions.delete</c> request.</summary>
public sealed record SessionDeleteRequest(string Key)
{
    public string? AgentId { get; init; }
    /// <summary>Compare-and-delete guard so a replaced session is never deleted.</summary>
    public string? ExpectedSessionId { get; init; }
    /// <summary>Archive-then-delete: allowed with operator.write; without it delete needs operator.admin.</summary>
    public bool ArchivedOnly { get; init; }
}

public sealed record SessionDeleteOutcome(string Key, bool Deleted, PreservedWorktreeInfo? PreservedWorktree);

/// <summary>A Gateway user profile that can own sessions (<c>users.list</c>).</summary>
public sealed record GatewayUserProfile(string Id, string? DisplayName, string? Email)
{
    public string Label => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
        : !string.IsNullOrWhiteSpace(Email) ? Email! : Id;
}

/// <summary>Managed worktree record (<c>worktrees.list</c>).</summary>
public sealed record WorktreeRecord(
    string Id,
    string Name,
    string RepoRoot,
    string? Path,
    string Branch,
    string? BaseRef,
    string OwnerKind,
    string? OwnerId,
    long CreatedAt,
    long LastActiveAt,
    long? RemovedAt)
{
    public bool IsRemoved => RemovedAt is > 0;
}

public sealed record WorktreeBranch(string Name);

public sealed class WorktreeBranchList
{
    public IReadOnlyList<WorktreeBranch> Branches { get; init; } = Array.Empty<WorktreeBranch>();
    public string? DefaultBranch { get; init; }
    public string? HeadBranch { get; init; }
    /// <summary><c>git</c>, <c>not_git</c> or <c>unavailable</c>, when reported.</summary>
    public string? RepositoryStatus { get; init; }
    public bool BranchesUnavailable { get; init; }
}

public sealed record WorktreeRemoveResult(bool Removed, string? SnapshotError);

/// <summary>Maintenance receipt for <c>worktrees.gc</c>; queued/running receipts carry a job ID to poll.</summary>
public sealed record WorktreeGcResult(
    string? JobId,
    string? State,
    string? Outcome,
    string? Error,
    IReadOnlyList<string> Removed,
    int OrphansDeleted,
    int SnapshotsPruned,
    IReadOnlyList<string> IssueReasons)
{
    public bool IsPending => State is "queued" or "running";
}
