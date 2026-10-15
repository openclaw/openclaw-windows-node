using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

/// <summary>
/// Creation-order ranking for Workspace sidebar rows; activity (updatedAt) never moves a row.
/// Rows with a gateway createdAt sort newest-first by it. Rows without one (sessions or
/// gateways that predate the field) rank below them, newest-first by the order this window
/// first saw them; a batch of unseen rows is seeded by updatedAt so the first paint matches
/// recency, then each rank is frozen. A session created during this run therefore lands on top.
/// Owned by one WorkspaceWindow; not thread-safe (UI thread only).
/// </summary>
internal sealed class WorkspaceSessionOrder
{
    private readonly Dictionary<string, long> _firstSeen = new(StringComparer.Ordinal);
    private long _next;

    public SessionInfo[] Sort(IEnumerable<SessionInfo> sessions)
    {
        var rows = sessions.ToArray();
        foreach (var unseen in rows
                     .Where(session => !_firstSeen.ContainsKey(session.Key))
                     .OrderBy(session => session.UpdatedAt ?? DateTime.MinValue)
                     .ThenBy(session => session.Key, StringComparer.Ordinal))
            _firstSeen[unseen.Key] = _next++;
        return rows
            .OrderByDescending(session => session.CreatedAt.HasValue)
            .ThenByDescending(session => session.CreatedAt ?? 0)
            .ThenByDescending(session => _firstSeen[session.Key])
            .ToArray();
    }
}
