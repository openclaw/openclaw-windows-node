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

    private bool Has(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
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
