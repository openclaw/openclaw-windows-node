using System.Text.Json;
using OpenClaw.Shared.Sessions;

namespace OpenClaw.Shared;

/// <summary>
/// Sessions page and Worktrees RPCs. Reads return typed "unsupported" results on
/// older Gateways; mutations throw <see cref="NotSupportedException"/> for an
/// unknown method and <see cref="InvalidOperationException"/> carrying the
/// Gateway's message for any other rejection.
/// </summary>
public partial class OpenClawGatewayClient
{
    // ── Row fields used by the Sessions page ──

    private static void PopulateSessionHubFields(SessionInfo session, JsonElement item, bool authoritative)
    {
        if (item.TryGetProperty("kind", out _)) session.Kind = GetString(item, "kind");
        if (item.TryGetProperty("category", out _)) session.Category = NonBlank(GetString(item, "category"));
        else if (authoritative) session.Category = null;
        if (item.TryGetProperty("reasoningLevel", out _)) session.ReasoningLevel = NonBlank(GetString(item, "reasoningLevel"));
        else if (authoritative) session.ReasoningLevel = null;
        if (item.TryGetProperty("fastMode", out var fast)) session.FastMode = ReadFastMode(fast);
        else if (authoritative) session.FastMode = null;
        if (item.TryGetProperty("color", out _)) session.Color = NonBlank(GetString(item, "color"));
        else if (authoritative) session.Color = null;
        if (item.TryGetProperty("icon", out _)) session.Icon = NonBlank(GetString(item, "icon"));
        else if (authoritative) session.Icon = null;
        if (item.TryGetProperty("owner", out var owner)) session.Owner = ParseSessionOwner(owner);
        else if (authoritative) session.Owner = null;
        if (item.TryGetProperty("placement", out var placement) && placement.ValueKind == JsonValueKind.Object)
        {
            session.PlacementState = GetString(placement, "state");
            session.PlacementRecoveryAction = GetString(placement, "recoveryAction");
        }
        else if (authoritative)
        {
            session.PlacementState = null;
            session.PlacementRecoveryAction = null;
        }
        if (item.TryGetProperty("archivedAt", out var archivedAt) && archivedAt.ValueKind == JsonValueKind.Number
            && archivedAt.TryGetDouble(out var archivedAtValue))
            session.ArchivedAt = (long)archivedAtValue;
        else if (authoritative)
            session.ArchivedAt = null;
        if (item.TryGetProperty("archiveReason", out _)) session.ArchiveReason = GetString(item, "archiveReason");
        else if (authoritative) session.ArchiveReason = null;
        if (item.TryGetProperty("lastRunError", out _)) session.LastRunError = GetString(item, "lastRunError");
        else if (authoritative) session.LastRunError = null;
    }

    internal static string? ReadFastMode(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "on",
        JsonValueKind.False => "off",
        JsonValueKind.String => NonBlank(value.GetString()),
        _ => null,
    };

    internal static SessionOwnerInfo? ParseSessionOwner(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object)
            return null;
        var actor = owner.TryGetProperty("actor", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested : owner;
        var type = GetString(actor, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;
        // Ownership policy and sessions.assignOwner use actor.id; identity is display-only and
        // is read only when it names the same kind of participant.
        var id = NonBlank(GetString(actor, "id"));
        if (id is null && actor.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object
            && GetString(identity, "type") == (type == "human" ? "profile" : type))
            id = NonBlank(GetString(identity, "id"));
        return new SessionOwnerInfo(type, id, NonBlank(GetString(actor, "label")));
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── sessions.list (filtered page) ──

    public async Task<SessionListPage> ListSessionsPageAsync(SessionListQuery query, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(query);
        JsonElement? payload;
        var includePageSource = !_sessionsListRejectsPageSource;
        try
        {
            payload = await TryRequestPayloadAsync("sessions.list", query.ToParameters(includePageSource), timeoutMs).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (includePageSource && IsRejectedPageSourceParam(ex.Message))
        {
            // Gateways before 2026.10.5 validate sessions.list as a closed object without
            // source/excludeDock. Remember that for this client and retry without them.
            _sessionsListRejectsPageSource = true;
            payload = await TryRequestPayloadAsync("sessions.list", query.ToParameters(includePageSource: false), timeoutMs).ConfigureAwait(false);
        }
        if (payload is null)
            return new SessionListPage { IsSupported = false };
        return ParseSessionListPage(payload.Value);
    }

    private volatile bool _sessionsListRejectsPageSource;

    internal static bool IsRejectedPageSourceParam(string? message) =>
        message is not null
        && message.Contains("invalid sessions.list params", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("'source'", StringComparison.Ordinal) || message.Contains("'excludeDock'", StringComparison.Ordinal));

    internal SessionListPage ParseSessionListPage(JsonElement payload)
    {
        var sessions = ParseDetachedSessionRows(payload);
        if (payload.ValueKind != JsonValueKind.Object)
            return new SessionListPage { Sessions = sessions };
        var total = GetLongOrNull(payload, "totalCount");
        var next = GetLongOrNull(payload, "nextOffset");
        return new SessionListPage
        {
            Sessions = sessions,
            TotalCount = total is { } t ? (int)Math.Min(t, int.MaxValue) : null,
            HasMore = GetOptionalBool(payload, "hasMore") == true,
            NextOffset = next is { } n ? (int)Math.Min(n, int.MaxValue) : null,
        };
    }

    // ── sessions.search ──

    public async Task<SessionSearchResult> SearchSessionTranscriptsAsync(
        string query, SessionListQuery scope, int limit = 25, int timeoutMs = 20000)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return new SessionSearchResult();
        var payload = await TryRequestPayloadAsync("sessions.search", new Dictionary<string, object?>
        {
            ["query"] = trimmed,
            ["limit"] = Math.Clamp(limit, 1, 25),
            ["scope"] = scope.ToSearchScope(excludeDock: !_sessionsListRejectsPageSource),
        }, timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return new SessionSearchResult { IsSupported = false };
        return ParseSessionSearchResult(payload.Value);
    }

    internal SessionSearchResult ParseSessionSearchResult(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new SessionSearchResult();
        var hits = new List<SessionSearchHit>();
        if (TryGetArray(payload, "results") is { } results)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var key = NonBlank(GetString(item, "sessionKey"));
                if (key is null)
                    continue;
                hits.Add(new SessionSearchHit(
                    key,
                    GetString(item, "messageId"),
                    GetString(item, "role") ?? "assistant",
                    GetLongOrNull(item, "timestamp") ?? 0,
                    GetString(item, "snippet") ?? ""));
            }
        }
        var sessions = payload.TryGetProperty("sessions", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? ParseDetachedSessionRows(payload)
            : Array.Empty<SessionInfo>();
        return new SessionSearchResult
        {
            Hits = hits,
            Sessions = sessions,
            Indexing = GetOptionalBool(payload, "indexing") == true,
            Truncated = GetOptionalBool(payload, "truncated") == true,
            ArchivedTranscriptsExcluded = (int)(GetLongOrNull(payload, "archivedTranscriptsExcluded") ?? 0),
        };
    }

    // ── sessions.groups.* ──

    public async Task<SessionGroupCatalog> ListSessionGroupsAsync(int timeoutMs = 15000)
    {
        var payload = await TryRequestPayloadAsync("sessions.groups.list", new { }, timeoutMs).ConfigureAwait(false);
        return payload is null ? new SessionGroupCatalog { IsSupported = false } : ParseSessionGroupCatalog(payload.Value);
    }

    public async Task<SessionGroupCatalog> PutSessionGroupsAsync(
        IReadOnlyList<string> names, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(names);
        var parameters = new Dictionary<string, object?> { ["names"] = names.ToArray() };
        return ParseSessionGroupCatalog(await MutateAsync("sessions.groups.put", parameters, timeoutMs).ConfigureAwait(false));
    }

    public async Task<SessionGroupCatalog> CreateSessionGroupAsync(string name, int timeoutMs = 15000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var payload = await MutateAsync("sessions.groups.create", new { name = name.Trim() }, timeoutMs).ConfigureAwait(false);
        return ParseSessionGroupCatalog(payload);
    }

    public async Task<SessionGroupCatalog> RenameSessionGroupAsync(string name, string to, int timeoutMs = 15000)
    {
        var payload = await MutateAsync("sessions.groups.rename", new { name, to }, timeoutMs).ConfigureAwait(false);
        return ParseSessionGroupCatalog(payload);
    }

    public async Task<SessionGroupCatalog> DeleteSessionGroupAsync(string name, int timeoutMs = 15000)
    {
        var payload = await MutateAsync("sessions.groups.delete", new { name }, timeoutMs).ConfigureAwait(false);
        return ParseSessionGroupCatalog(payload);
    }

    internal static SessionGroupCatalog ParseSessionGroupCatalog(JsonElement payload)
    {
        if (TryGetArray(payload, "groups") is not { } groups)
            return new SessionGroupCatalog();
        var names = groups.EnumerateArray()
            .Where(group => group.ValueKind == JsonValueKind.Object)
            .Select(group => (Name: NonBlank(GetString(group, "name")), Position: GetLongOrNull(group, "position") ?? long.MaxValue))
            .Where(group => group.Name is not null)
            .OrderBy(group => group.Position)
            .Select(group => group.Name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SessionGroupCatalog { Names = names };
    }

    // ── sessions.delete (response-aware, bulk-safe) ──

    public async Task<SessionDeleteOutcome> DeleteSessionDetailedAsync(SessionDeleteRequest request, int timeoutMs = 30000)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        var parameters = new Dictionary<string, object?> { ["key"] = request.Key, ["deleteTranscript"] = true };
        if (!string.IsNullOrWhiteSpace(request.AgentId)) parameters["agentId"] = request.AgentId;
        if (!string.IsNullOrWhiteSpace(request.ExpectedSessionId)) parameters["expectedSessionId"] = request.ExpectedSessionId;
        if (request.ArchivedOnly) parameters["archivedOnly"] = true;
        var payload = await MutateAsync("sessions.delete", parameters, timeoutMs).ConfigureAwait(false);
        return ParseSessionDeleteOutcome(payload, request.Key);
    }

    internal static SessionDeleteOutcome ParseSessionDeleteOutcome(JsonElement payload, string key)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The Gateway returned an invalid session deletion response.");
        if (payload.TryGetProperty("ok", out var ok) && ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException(GetString(payload, "reason") ?? "The Gateway rejected the session deletion.");
        PreservedWorktreeInfo? preserved = null;
        if (payload.TryGetProperty("worktreePreserved", out var worktree) && worktree.ValueKind == JsonValueKind.Object)
        {
            preserved = new PreservedWorktreeInfo(
                GetString(worktree, "id") ?? "",
                GetString(worktree, "branch") ?? "",
                GetString(worktree, "path") ?? "",
                GetString(worktree, "reason") ?? "cleanup-failed");
        }
        return new SessionDeleteOutcome(
            GetString(payload, "key") ?? key,
            GetOptionalBool(payload, "deleted") ?? true,
            preserved);
    }

    // ── sessions.assignOwner / sessions.reclaim ──

    public async Task<SessionOwnerInfo?> AssignSessionOwnerAsync(
        string key, string? agentId, string ownerType, string ownerId, int timeoutMs = 15000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var parameters = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["owner"] = new Dictionary<string, object?> { ["type"] = ownerType, ["id"] = ownerId },
        };
        if (!string.IsNullOrWhiteSpace(agentId)) parameters["agentId"] = agentId;
        var payload = await MutateAsync("sessions.assignOwner", parameters, timeoutMs).ConfigureAwait(false);
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("owner", out var owner)
            ? ParseSessionOwner(owner) : null;
    }

    /// <summary>Stops a session's cloud worker and returns ownership to the Gateway (<c>sessions.reclaim</c>).</summary>
    public async Task ReclaimSessionAsync(string key, string? agentId, int timeoutMs = 120000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var parameters = new Dictionary<string, object?> { ["key"] = key };
        if (!string.IsNullOrWhiteSpace(agentId)) parameters["agentId"] = agentId;
        await MutateAsync("sessions.reclaim", parameters, timeoutMs).ConfigureAwait(false);
    }

    // ── users.list / users.self ──

    public async Task<IReadOnlyList<GatewayUserProfile>> ListUserProfilesAsync(int timeoutMs = 15000)
    {
        var payload = await TryRequestPayloadAsync("users.list", new { }, timeoutMs).ConfigureAwait(false);
        return payload is null ? Array.Empty<GatewayUserProfile>() : ParseUserProfiles(payload.Value);
    }

    public async Task<string?> GetSelfProfileIdAsync(int timeoutMs = 12000)
    {
        var payload = await TryRequestPayloadAsync("users.self", new { }, timeoutMs).ConfigureAwait(false);
        return payload is { ValueKind: JsonValueKind.Object } self
               && self.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
            ? NonBlank(GetString(profile, "id"))
            : null;
    }

    internal static IReadOnlyList<GatewayUserProfile> ParseUserProfiles(JsonElement payload)
    {
        if (TryGetArray(payload, "profiles") is not { } profiles)
            return Array.Empty<GatewayUserProfile>();
        return profiles.EnumerateArray()
            .Where(profile => profile.ValueKind == JsonValueKind.Object)
            .Where(profile => profile.TryGetProperty("mergedInto", out var merged) is false
                || merged.ValueKind != JsonValueKind.String)
            .Select(profile => new GatewayUserProfile(
                GetString(profile, "id") ?? "",
                NonBlank(GetString(profile, "displayName")),
                GetStringArrayList(profile, "emails").FirstOrDefault()))
            .Where(profile => profile.Id.Length > 0)
            .ToArray();
    }

    // ── worktrees.* ──

    public async Task<IReadOnlyList<WorktreeRecord>?> ListWorktreesAsync(int timeoutMs = 15000)
    {
        var payload = await TryRequestPayloadAsync("worktrees.list", new { }, timeoutMs).ConfigureAwait(false);
        return payload is null ? null : ParseWorktrees(payload.Value);
    }

    public async Task<WorktreeBranchList> ListWorktreeBranchesAsync(string repoRoot, int timeoutMs = 15000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var payload = await MutateAsync("worktrees.branches",
            new { repoRoot, includeRepositoryStatus = true }, timeoutMs).ConfigureAwait(false);
        return ParseWorktreeBranches(payload);
    }

    public async Task<WorktreeRecord?> CreateWorktreeAsync(
        string repoRoot, string? name, string? baseRef, int timeoutMs = 120000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var parameters = new Dictionary<string, object?> { ["repoRoot"] = repoRoot.Trim() };
        if (!string.IsNullOrWhiteSpace(name)) parameters["name"] = name.Trim();
        if (!string.IsNullOrWhiteSpace(baseRef)) parameters["baseRef"] = baseRef.Trim();
        return ParseWorktree(await MutateAsync("worktrees.create", parameters, timeoutMs).ConfigureAwait(false));
    }

    public async Task<WorktreeRemoveResult> RemoveWorktreeAsync(string id, bool force, int timeoutMs = 120000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var parameters = new Dictionary<string, object?> { ["id"] = id };
        if (force) parameters["force"] = true;
        var payload = await MutateAsync("worktrees.remove", parameters, timeoutMs).ConfigureAwait(false);
        return new WorktreeRemoveResult(
            GetOptionalBool(payload, "removed") == true,
            GetStringSafe(payload, "snapshotError"));
    }

    public async Task<WorktreeRecord?> RestoreWorktreeAsync(string id, int timeoutMs = 120000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ParseWorktree(await MutateAsync("worktrees.restore", new { id }, timeoutMs).ConfigureAwait(false));
    }

    public async Task<WorktreeGcResult> RunWorktreeGcAsync(string? jobId = null, int timeoutMs = 60000)
    {
        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(jobId)) parameters["jobId"] = jobId;
        return ParseWorktreeGc(await MutateAsync("worktrees.gc", parameters, timeoutMs).ConfigureAwait(false));
    }

    internal static IReadOnlyList<WorktreeRecord> ParseWorktrees(JsonElement payload) =>
        TryGetArray(payload, "worktrees") is { } rows
            ? rows.EnumerateArray().Select(ParseWorktree).OfType<WorktreeRecord>().ToArray()
            : Array.Empty<WorktreeRecord>();

    internal static WorktreeRecord? ParseWorktree(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var id = NonBlank(GetString(item, "id"));
        if (id is null)
            return null;
        return new WorktreeRecord(
            id,
            GetString(item, "name") ?? id,
            GetString(item, "repoRoot") ?? "",
            GetString(item, "path"),
            GetString(item, "branch") ?? "",
            GetString(item, "baseRef"),
            GetString(item, "ownerKind") ?? "manual",
            NonBlank(GetString(item, "ownerId")),
            GetLongOrNull(item, "createdAt") ?? 0,
            GetLongOrNull(item, "lastActiveAt") ?? 0,
            GetLongOrNull(item, "removedAt"));
    }

    internal static WorktreeBranchList ParseWorktreeBranches(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new WorktreeBranchList();
        var branches = TryGetArray(payload, "branches") is { } rows
            ? rows.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Object)
                .Select(row => new WorktreeBranch(GetString(row, "name") ?? ""))
                .Where(branch => branch.Name.Length > 0)
                .ToArray()
            : Array.Empty<WorktreeBranch>();
        return new WorktreeBranchList
        {
            Branches = branches,
            DefaultBranch = NonBlank(GetString(payload, "defaultBranch")),
            HeadBranch = NonBlank(GetString(payload, "headBranch")),
            RepositoryStatus = NonBlank(GetString(payload, "repositoryStatus")),
            BranchesUnavailable = GetOptionalBool(payload, "branchesUnavailable") == true,
        };
    }

    internal static WorktreeGcResult ParseWorktreeGc(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new WorktreeGcResult(null, null, null, null, Array.Empty<string>(), 0, 0, Array.Empty<string>());
        var issues = TryGetArray(payload, "issues") is { } rows
            ? rows.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Object)
                .Select(row => GetString(row, "reason"))
                .OfType<string>()
                .Where(reason => reason.Length > 0)
                .ToArray()
            : Array.Empty<string>();
        return new WorktreeGcResult(
            NonBlank(GetString(payload, "jobId")),
            NonBlank(GetString(payload, "state")),
            NonBlank(GetString(payload, "outcome")),
            NonBlank(GetString(payload, "error")),
            GetStringArrayList(payload, "removed"),
            (int)(GetLongOrNull(payload, "orphansDeleted") ?? 0),
            (int)(GetLongOrNull(payload, "snapshotsPruned") ?? 0),
            issues);
    }

    // ── Shared mutation transport ──

    private async Task<JsonElement> MutateAsync(string method, object? parameters, int timeoutMs)
    {
        try
        {
            return await SendResponseRequestAsync(method, parameters, timeoutMs).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            throw new NotSupportedException($"{method} is not supported by this Gateway.", ex);
        }
    }
}
