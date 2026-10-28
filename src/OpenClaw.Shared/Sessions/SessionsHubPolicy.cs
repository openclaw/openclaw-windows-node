namespace OpenClaw.Shared.Sessions;

/// <summary>
/// Operator capabilities that gate Sessions page and Worktrees commands.
/// Deletes of archived rows use the archive-then-delete contract (operator.write);
/// other deletes, compact, reset, and every worktree mutation need operator.admin.
/// </summary>
public sealed record SessionsHubPermissions(bool IsConnected, IReadOnlyCollection<string> Scopes, IReadOnlyCollection<string> AdvertisedMethods)
{
    public const string AdminScope = "operator.admin";
    public const string WriteScope = "operator.write";

    public static SessionsHubPermissions Disconnected { get; } = new(false, Array.Empty<string>(), Array.Empty<string>());

    public bool IsAdmin => IsConnected && Has(AdminScope);
    public bool CanWrite => IsConnected && (IsAdmin || Has(WriteScope) || Has("operator.sessions.write"));

    public bool CanDelete(SessionInfo row) => row.Archived ? CanWrite : IsAdmin;

    /// <summary>An empty advertisement is unknown (older Gateway), so the method stays available.</summary>
    public bool Supports(string method) =>
        AdvertisedMethods.Count == 0 || AdvertisedMethods.Contains(method, StringComparer.Ordinal);

    public bool CanManageWorktrees => IsAdmin && Supports("worktrees.list");

    // Unknown advertisements are not enough for this additive RPC. Never fall back to replacing
    // a cached catalog, which could remove an empty group another client just created.
    public bool SupportsGroupCreation => AdvertisedMethods.Contains("sessions.groups.create", StringComparer.Ordinal);
    public bool CanCreateGroup => IsConnected && (IsAdmin || Has(WriteScope)) && SupportsGroupCreation;

    private bool Has(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
}

/// <summary>Immutable multi-select state keyed by session key.</summary>
public sealed class SessionsHubSelection
{
    public static SessionsHubSelection Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    private readonly HashSet<string> _keys;

    private SessionsHubSelection(HashSet<string> keys) => _keys = keys;

    public int Count => _keys.Count;
    public IReadOnlyCollection<string> Keys => _keys;
    public bool Contains(string key) => _keys.Contains(key);

    public SessionsHubSelection Toggle(string key)
    {
        var next = new HashSet<string>(_keys, StringComparer.Ordinal);
        if (!next.Add(key))
            next.Remove(key);
        return new(next);
    }

    public SessionsHubSelection Set(IEnumerable<string> keys, bool selected)
    {
        var next = new HashSet<string>(_keys, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (selected) next.Add(key);
            else next.Remove(key);
        }
        return new(next);
    }

    /// <summary>Drops keys whose rows disappeared (deleted, filtered out, or replaced).</summary>
    public SessionsHubSelection Reconcile(IEnumerable<string> visibleKeys)
    {
        var visible = new HashSet<string>(visibleKeys, StringComparer.Ordinal);
        if (_keys.All(visible.Contains))
            return this;
        return new(new HashSet<string>(_keys.Where(visible.Contains), StringComparer.Ordinal));
    }

    /// <summary>
    /// The keys of <paramref name="pageRows"/> a user can actually see: rows inside a
    /// collapsed group are hidden, so "Select page" must not pick them up.
    /// </summary>
    public static IReadOnlyList<string> ExpandedKeys(
        IEnumerable<SessionInfo> pageRows, IEnumerable<SessionRowGroup> groups, IReadOnlySet<string> collapsedGroupIds)
    {
        var hidden = new HashSet<string>(
            groups.Where(group => collapsedGroupIds.Contains(group.Id)).SelectMany(group => group.Rows).Select(row => row.Key),
            StringComparer.Ordinal);
        return pageRows.Select(row => row.Key).Where(key => !hidden.Contains(key)).ToArray();
    }

    /// <summary>All, some, or none of <paramref name="pageKeys"/> are selected (true/null/false).</summary>
    public bool? PageState(IReadOnlyCollection<string> pageKeys)
    {
        if (pageKeys.Count == 0) return false;
        var selected = pageKeys.Count(_keys.Contains);
        return selected == 0 ? false : selected == pageKeys.Count ? true : null;
    }
}

/// <summary>A planned bulk deletion: what will be requested and what is blocked.</summary>
public sealed record SessionsDeletePlan(IReadOnlyList<SessionDeleteRequest> Requests, IReadOnlyList<SessionInfo> Blocked);

public static class SessionsHubDeletion
{
    /// <summary>
    /// Builds one compare-and-delete request per row. Main sessions and rows the
    /// operator cannot delete are blocked rather than silently skipped.
    /// </summary>
    public static SessionsDeletePlan Plan(
        IEnumerable<SessionInfo> rows, SessionsHubPermissions permissions, Func<SessionInfo, bool> isMain)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(isMain);
        var requests = new List<SessionDeleteRequest>();
        var blocked = new List<SessionInfo>();
        foreach (var row in rows.DistinctBy(row => row.Key))
        {
            if (isMain(row) || !permissions.CanDelete(row))
            {
                blocked.Add(row);
                continue;
            }
            requests.Add(new SessionDeleteRequest(row.Key)
            {
                AgentId = SessionsHubProjection.AgentId(row),
                ExpectedSessionId = string.IsNullOrWhiteSpace(row.SessionId) ? null : row.SessionId,
                ArchivedOnly = row.Archived,
            });
        }
        return new SessionsDeletePlan(requests, blocked);
    }

    /// <summary>Delete-all-archived covers only archived rows of the current roster.</summary>
    public static SessionsDeletePlan PlanDeleteAllArchived(
        IEnumerable<SessionInfo> rows, SessionsHubPermissions permissions, Func<SessionInfo, bool> isMain) =>
        Plan(rows.Where(row => row.Archived), permissions, isMain);

    /// <summary>Undo restores the archive and, when the row was pinned, its pin.</summary>
    public static SessionPatch UndoArchivePatch(bool wasPinned)
    {
        var patch = new SessionPatch { Archived = false };
        if (wasPinned)
            patch.Pinned = true;
        return patch;
    }
}

public static class WorktreesProjection
{
    public static IReadOnlyList<WorktreeRecord> Sort(IEnumerable<WorktreeRecord> rows) =>
        rows.OrderByDescending(row => row.LastActiveAt).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Short repository name for display; the full root stays in the tooltip.</summary>
    public static string RepoName(string repoRoot)
    {
        var parts = repoRoot.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : repoRoot;
    }

    /// <summary>Upstream worktree names: lowercase letters, digits, and hyphens, up to 64 characters.</summary>
    public static bool IsValidName(string? name) =>
        string.IsNullOrEmpty(name)
        || (name.Length <= 64
            && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
            && name.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-'));
}
