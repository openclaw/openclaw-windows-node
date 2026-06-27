namespace OpenClaw.Shared.Sessions;

/// <summary>
/// Selection policy for compaction checkpoints. Branching can use a displayed
/// checkpoint directly, but restore is destructive, so it should only target a
/// checkpoint when the newest entry is unambiguous.
/// </summary>
public static class SessionCheckpointSelection
{
    /// <summary>
    /// Returns the checkpoint that is provably the most recent, or
    /// <c>null</c> when that can't be established. Restore archives every
    /// message after the checkpoint, so callers should refuse to restore when
    /// this returns <c>null</c> rather than guessing.
    /// </summary>
    public static SessionCompactionCheckpoint? ResolveUnambiguousLatest(
        IReadOnlyList<SessionCompactionCheckpoint> checkpoints)
    {
        var valid = checkpoints.Where(c => !string.IsNullOrEmpty(c.Id)).ToList();
        if (valid.Count == 0) return null;

        if (valid.Any(c => c.CreatedAt is null)) return null;

        var ordered = valid.OrderByDescending(c => c.CreatedAt!.Value).ToList();
        if (ordered.Count == 1) return ordered[0];

        return ordered[0].CreatedAt!.Value > ordered[1].CreatedAt!.Value ? ordered[0] : null;
    }
}
