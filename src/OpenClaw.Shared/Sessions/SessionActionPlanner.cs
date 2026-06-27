namespace OpenClaw.Shared.Sessions;

/// <summary>
/// The set of per-session lifecycle actions the UI can offer: open, reset
/// (new chat), compact, delete, export, plus compaction-checkpoint
/// branch/restore.
/// </summary>
public enum SessionActionKind
{
    OpenChat,
    Reset,
    Compact,
    Delete,
    Export,
    Branch,
    Restore,
}

/// <summary>
/// The copy a confirmation dialog should display for a destructive or
/// context-altering session action.
/// </summary>
public sealed record SessionActionPrompt(
    SessionActionKind Kind,
    string SessionName,
    string Title,
    string Body,
    string ConfirmLabel,
    bool IsDestructive);

public enum SessionMainState
{
    Main,
    NotMain,
    Unknown,
}

/// <summary>
/// Pure decision logic for session lifecycle actions, shared by the tray's
/// session menus (SessionsPage flyout + App tray/toast handlers) so every
/// entry point applies the same confirmation copy and main-session
/// protection. Contains no UI or gateway dependencies so it is unit testable.
/// </summary>
public static class SessionActionPlanner
{
    /// <summary>
    /// Destructive actions clear or remove conversation state and must always
    /// be confirmed and styled as dangerous.
    /// </summary>
    public static bool IsDestructive(SessionActionKind kind) =>
        kind is SessionActionKind.Reset
            or SessionActionKind.Delete
            or SessionActionKind.Restore;

    /// <summary>
    /// Actions that prompt before running. Compact is reversible-ish (it
    /// archives rather than deletes) but still alters the live context, so it
    /// is confirmed too. Restore rolls the live session back to a checkpoint
    /// and is therefore confirmed.
    /// </summary>
    public static bool RequiresConfirmation(SessionActionKind kind) =>
        kind is SessionActionKind.Reset
            or SessionActionKind.Compact
            or SessionActionKind.Delete
            or SessionActionKind.Restore;

    /// <summary>
    /// Returns false when an action must not be offered for the given session.
    /// The main session is the gateway's primary conversation: it cannot be
    /// deleted and is not eligible for a destructive checkpoint restore.
    /// Resetting, compacting, and branching it are allowed.
    /// </summary>
    public static bool IsAllowed(SessionActionKind kind, bool isMainSession, out string? blockedReason)
    {
        if (isMainSession && kind == SessionActionKind.Delete)
        {
            blockedReason = "The main session can't be deleted. Reset it instead to start fresh.";
            return false;
        }

        if (isMainSession && kind == SessionActionKind.Restore)
        {
            blockedReason = "The main session can't be rolled back to a checkpoint. Branch from it instead.";
            return false;
        }

        blockedReason = null;
        return true;
    }

    public static bool IsAllowed(SessionActionKind kind, SessionMainState mainState, out string? blockedReason)
    {
        if (!IsAllowed(kind, mainState == SessionMainState.Main, out blockedReason))
            return false;

        if (mainState == SessionMainState.Unknown &&
            kind is SessionActionKind.Delete or SessionActionKind.Restore)
        {
            blockedReason = "Session identity is still loading. Try again after sessions refresh.";
            return false;
        }

        return true;
    }

    public static SessionMainState ResolveMainState(
        string key,
        bool? rowIsMain = null,
        string? mainSessionKey = null,
        IEnumerable<SessionInfo>? sessions = null)
    {
        if (IsMainSessionKeyShape(key)) return SessionMainState.Main;
        if (rowIsMain == true) return SessionMainState.Main;

        if (string.Equals(mainSessionKey, key, StringComparison.Ordinal)) return SessionMainState.Main;
        if (!string.IsNullOrWhiteSpace(mainSessionKey)) return SessionMainState.NotMain;

        var session = sessions?.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        if (session?.IsMain == true) return SessionMainState.Main;
        if (session is not null || rowIsMain.HasValue) return SessionMainState.NotMain;

        return SessionMainState.Unknown;
    }

    public static bool IsMainSessionKeyShape(string key)
    {
        if (string.Equals(key, "main", StringComparison.Ordinal)) return true;
        return key.EndsWith(":main", StringComparison.Ordinal) ||
               key.Contains(":main:main", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the confirmation copy for an action, or <c>null</c> when the
    /// action needs no confirmation. The session's friendly name is used when
    /// available, falling back to the raw key.
    /// </summary>
    public static SessionActionPrompt? BuildPrompt(
        SessionActionKind kind,
        string sessionKey,
        string? displayName,
        bool isMainSession)
    {
        if (!RequiresConfirmation(kind))
            return null;

        var name = Describe(sessionKey, displayName);

        return kind switch
        {
            SessionActionKind.Reset => new SessionActionPrompt(
                kind,
                name,
                "Reset session?",
                $"Start a fresh session for \u201C{name}\u201D? The current conversation context will be cleared.",
                "Reset",
                IsDestructive: true),

            SessionActionKind.Compact => new SessionActionPrompt(
                kind,
                name,
                "Compact session log?",
                $"Keep the most recent messages for \u201C{name}\u201D and archive the rest. " +
                "This creates a compaction checkpoint; export the transcript first if you need the full history.",
                "Compact",
                IsDestructive: false),

            SessionActionKind.Delete => new SessionActionPrompt(
                kind,
                name,
                "Delete session?",
                $"Delete \u201C{name}\u201D and archive its transcript? It will be removed from the session list.",
                "Delete",
                IsDestructive: true),

            SessionActionKind.Restore => new SessionActionPrompt(
                kind,
                name,
                "Restore checkpoint?",
                $"Roll \u201C{name}\u201D back to this compaction checkpoint? Messages added after the checkpoint will be archived.",
                "Restore",
                IsDestructive: true),

            _ => null,
        };
    }

    /// <summary>Friendly label for a session, preferring its display name.</summary>
    public static string Describe(string sessionKey, string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName! : sessionKey;
}
