namespace OpenClaw.Shared.Sessions;

/// <summary>Client-side grouping offered by the Sessions page.</summary>
public enum SessionsGroupBy
{
    None,
    Category,
    Person,
    Channel,
    Kind,
    Agent,
    Date,
}

public enum SessionsSortColumn
{
    Updated,
    Name,
}

/// <summary>One rendered group. <see cref="Id"/> is empty for the ungrouped bucket.</summary>
public sealed record SessionRowGroup(string Id, IReadOnlyList<SessionInfo> Rows)
{
    public bool IsUngrouped => Id.Length == 0;
}

/// <summary>
/// Pure Sessions page projections: grouping, sorting, paging, and the facts
/// grouping and row presentation read from each row. No WinUI or localization.
/// </summary>
public static class SessionsHubProjection
{
    public const string UngroupedId = "";
    public static readonly IReadOnlyList<int> PageSizes = [10, 25, 50, 100];
    public const int DefaultPageSize = 25;

    public static readonly IReadOnlyList<string> DateBucketOrder = ["today", "yesterday", "week", "older", UngroupedId];

    private static readonly HashSet<string> RunLifecycleReasons = new(StringComparer.Ordinal)
    {
        "agent.run.started",
        "agent.input.settled",
    };

    /// <summary>
    /// True when a <c>sessions.changed</c> push only reports a run starting or ending for a session the
    /// tracked list already holds. The tracked refresh carries that status, so the Active roster needs
    /// no extra reload. Any other reason, a missing key, or an untracked (for example global) session
    /// may change rows the tracked list does not hold.
    /// </summary>
    public static bool IsTrackedRunChange(SessionListChange change, IEnumerable<SessionInfo> tracked)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(tracked);
        return change.Reason is { } reason && RunLifecycleReasons.Contains(reason)
            && change.SessionKey is { Length: > 0 } key && tracked.Any(session => session.Key == key);
    }

    /// <summary>
    /// Identifies what the Sessions page shows from tracked sessions. Activity ticks only
    /// change <c>CurrentActivity</c> and <c>LastSeen</c>, so they keep the same signature
    /// and never force a roster reload.
    /// </summary>
    public static string LiveSignature(IEnumerable<SessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return string.Join("\n", sessions.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => string.Join("\u001f",
            s.Key, s.SessionId, s.Status, s.HasActiveRun, s.AbortedLastRun, s.UpdatedAt?.Ticks, s.Label, s.DisplayName,
            s.DerivedTitle, s.Archived, s.ArchivedAt, s.Pinned, s.Unread, s.Category, s.Color, s.Icon, s.Kind, s.Channel,
            s.Owner?.Type, s.Owner?.Id, s.Owner?.Label, s.Model, s.ThinkingLevel, s.ReasoningLevel, s.VerboseLevel, s.FastMode,
            s.Worktree?.Id, s.Worktree?.Branch, s.TotalTokens, s.PlacementState, s.PlacementRecoveryAction)));
    }

    /// <summary>Pinned rows first (newest pin first), then the selected column.</summary>
    public static IReadOnlyList<SessionInfo> Sort(
        IEnumerable<SessionInfo> rows, SessionsSortColumn column, bool descending, Func<SessionInfo, string> title)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(title);
        var pinnedFirst = rows
            .OrderByDescending(row => row.Pinned ? 1 : 0)
            .ThenByDescending(row => row.PinnedAt ?? 0);
        var ordered = column == SessionsSortColumn.Name
            ? descending
                ? pinnedFirst.ThenByDescending(title, StringComparer.CurrentCultureIgnoreCase)
                : pinnedFirst.ThenBy(title, StringComparer.CurrentCultureIgnoreCase)
            : descending
                ? pinnedFirst.ThenByDescending(row => row.UpdatedAt ?? DateTime.MinValue)
                : pinnedFirst.ThenBy(row => row.UpdatedAt ?? DateTime.MinValue);
        return ordered.ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Partitions sorted rows into ordered groups, preserving row order. Category
    /// mode also emits empty catalog groups (so they stay move targets) and always
    /// ends with the ungrouped bucket.
    /// </summary>
    public static IReadOnlyList<SessionRowGroup> Group(
        IReadOnlyList<SessionInfo> rows,
        SessionsGroupBy mode,
        IReadOnlyList<string>? knownCategories = null,
        DateTime? nowLocal = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (mode == SessionsGroupBy.None)
            return [new SessionRowGroup(UngroupedId, rows)];

        var now = nowLocal ?? DateTime.Now;
        var buckets = new Dictionary<string, List<SessionInfo>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = GroupId(row, mode, now);
            if (!buckets.TryGetValue(id, out var bucket))
                buckets[id] = bucket = [];
            bucket.Add(row);
        }

        IEnumerable<string> order = mode switch
        {
            SessionsGroupBy.Date => DateBucketOrder.Where(buckets.ContainsKey),
            SessionsGroupBy.Category => CategoryOrder(buckets.Keys, knownCategories ?? []),
            _ => buckets.Keys.Where(id => id.Length > 0)
                .OrderBy(id => id, StringComparer.CurrentCultureIgnoreCase)
                .Append(UngroupedId)
                .Where(buckets.ContainsKey),
        };
        return order.Select(id => new SessionRowGroup(
                id, buckets.TryGetValue(id, out var bucket) ? bucket : (IReadOnlyList<SessionInfo>)Array.Empty<SessionInfo>()))
            .ToArray();
    }

    private static IEnumerable<string> CategoryOrder(IEnumerable<string> present, IReadOnlyList<string> catalog)
    {
        var known = KnownCategories(catalog, present.Where(id => id.Length > 0));
        return known.Append(UngroupedId);
    }

    /// <summary>Catalog order first, then categories found only on rows, alphabetically.</summary>
    public static IReadOnlyList<string> KnownCategories(IReadOnlyList<string> catalog, IEnumerable<string?> rowCategories)
    {
        var seen = new HashSet<string>(catalog.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.Ordinal);
        var discovered = rowCategories
            .Select(category => category?.Trim())
            .OfType<string>()
            .Where(category => category.Length > 0 && !seen.Contains(category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase);
        return catalog.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)
            .Concat(discovered).ToArray();
    }

    public static string GroupId(SessionInfo row, SessionsGroupBy mode, DateTime nowLocal) => mode switch
    {
        SessionsGroupBy.Category => row.Category?.Trim() ?? UngroupedId,
        SessionsGroupBy.Person => OwnerGroupId(row.Owner),
        SessionsGroupBy.Channel => SessionDisplayResolver.Resolve(row).Channel ?? UngroupedId,
        SessionsGroupBy.Kind => DisplayKind(row),
        SessionsGroupBy.Agent => AgentId(row) ?? UngroupedId,
        SessionsGroupBy.Date => DateBucket(row.UpdatedAt, nowLocal),
        _ => UngroupedId,
    };

    public static string OwnerGroupId(SessionOwnerInfo? owner) =>
        owner is { Id: { Length: > 0 } id } ? $"{owner.Type}:{id}" : UngroupedId;

    /// <summary>Calendar buckets in local time; a week is today plus the six previous days.</summary>
    public static string DateBucket(DateTime? updatedUtc, DateTime nowLocal)
    {
        if (updatedUtc is not { } updated || updated <= DateTime.UnixEpoch)
            return UngroupedId;
        var local = updated.Kind == DateTimeKind.Local ? updated : DateTime.SpecifyKind(updated, DateTimeKind.Utc).ToLocalTime();
        var today = nowLocal.Date;
        if (local >= today) return "today";
        if (local >= today.AddDays(-1)) return "yesterday";
        return local >= today.AddDays(-6) ? "week" : "older";
    }

    /// <summary>Gateway kind for global/unknown/group rows, otherwise the resolved classification.</summary>
    public static string DisplayKind(SessionInfo row)
    {
        if (row.Kind is "global" or "unknown" or "group")
            return row.Kind;
        var classification = SessionDisplayResolver.Resolve(row).Classification;
        // Keys without a recognized route shape ("custom") fall back to the Gateway's own kind.
        return string.IsNullOrWhiteSpace(classification) || classification.Equals("custom", StringComparison.OrdinalIgnoreCase)
            ? row.Kind ?? "direct"
            : classification.ToLowerInvariant();
    }

    public static string? AgentId(SessionInfo row) => SessionDisplayResolver.Resolve(row).AgentId;

    public static (IReadOnlyList<SessionInfo> Rows, int PageIndex, int PageCount) Page(
        IReadOnlyList<SessionInfo> rows, int pageIndex, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var size = pageSize > 0 ? pageSize : DefaultPageSize;
        var pageCount = Math.Max(1, (rows.Count + size - 1) / size);
        var index = Math.Clamp(pageIndex, 0, pageCount - 1);
        return (rows.Skip(index * size).Take(size).ToArray(), index, pageCount);
    }

    /// <summary>
    /// Cloud-worker sessions expose Stop unless the placement is local/reclaimed,
    /// a failed placement asks for restart, or an active worker is still running a turn.
    /// </summary>
    public static bool CanStopCloudWorker(SessionInfo row)
    {
        var state = row.PlacementState;
        if (string.IsNullOrWhiteSpace(state) || state is "local" or "reclaimed")
            return false;
        if (state == "failed" && row.PlacementRecoveryAction == "restart")
            return false;
        return state != "active" || row.HasActiveRun != true;
    }

    public static bool IsCloudWorker(SessionInfo row) =>
        !string.IsNullOrWhiteSpace(row.PlacementState) && row.PlacementState is not ("local" or "reclaimed");
}
