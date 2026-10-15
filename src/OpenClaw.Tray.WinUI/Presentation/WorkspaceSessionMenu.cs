using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;

namespace OpenClawTray.Presentation;

/// <summary>The action a workspace session context-menu entry dispatches.</summary>
internal enum WorkspaceSessionMenuAction
{
    TogglePin,
    Rename,
    ToggleUnread,
    ToggleArchive,
    Fork,
    CopyKey,
    CopySessionId,
    CopyMarkdown,
    Reset,
    Compact,
    ExportTranscript,
    Delete,
}

/// <summary>Fluent glyph for a context-menu entry; the controller maps it to an icon.</summary>
internal enum WorkspaceSessionMenuIcon
{
    None,
    Pin,
    Unpin,
    Rename,
    MarkUnread,
    MarkRead,
    Archive,
    Unarchive,
    Fork,
    Copy,
    Reset,
    Compact,
    Export,
    Delete,
}

internal enum WorkspaceSessionMenuEntryKind
{
    Item,
    SubMenu,
    Separator,
}

/// <summary>One pure menu row: kind, localization key suffix, action, glyph, and gating flags.</summary>
internal sealed record WorkspaceSessionMenuEntry(
    WorkspaceSessionMenuEntryKind Kind,
    string LabelKey = "",
    WorkspaceSessionMenuAction Action = default,
    WorkspaceSessionMenuIcon Icon = WorkspaceSessionMenuIcon.None,
    string? AccessKey = null,
    bool IsEnabled = true,
    bool IsDestructive = false,
    IReadOnlyList<WorkspaceSessionMenuEntry>? Children = null);

/// <summary>Row state at open time, enough to label toggles and gate lifecycle entries.</summary>
internal sealed record WorkspaceSessionMenuState(
    bool IsPinned,
    bool IsUnread,
    bool IsArchived,
    bool HasSessionId,
    SessionMainState MainState,
    bool IsConnected);

/// <summary>
/// Pure model for the workspace sidebar session context menu: builds the entry
/// list from row state and derives the gateway requests for the actions. No
/// WinUI or gateway dependencies, so the layout and payloads are unit testable.
/// Label keys are <c>WorkspaceShell_</c> suffixes resolved by the controller.
/// </summary>
internal static class WorkspaceSessionMenu
{
    /// <summary>Archive and delete share the main-session protection of delete.</summary>
    public static bool CanArchiveOrDelete(SessionMainState mainState) =>
        SessionActionPlanner.IsAllowed(SessionActionKind.Delete, mainState, out _);

    public static SessionPatch RenamePatch(string? text)
    {
        var patch = new SessionPatch();
        if (string.IsNullOrWhiteSpace(text))
            patch.Label = SessionPatch.Clear;
        else
            patch.Label = text.Trim();
        return patch;
    }

    /// <summary>Read acknowledgement: <c>unread:false</c> plus the concurrency guard.</summary>
    public static SessionPatch ReadAcknowledgementPatch(long? markedUnreadAt)
    {
        var patch = new SessionPatch { Unread = false };
        if (markedUnreadAt is { } at)
            patch.ExpectedMarkedUnreadAt = at;
        else
            patch.ExpectedMarkedUnreadAt = SessionPatch.Clear;
        return patch;
    }

    public static SessionCreateRequest ForkRequest(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionCreateRequest
        {
            ParentSessionKey = session.Key,
            AgentId = SessionDisplayResolver.Resolve(session).AgentId,
            Fork = true,
            ForkFrom = session.HasActiveRun == true ? "last-completed" : null,
        };
    }

    public static IReadOnlyList<WorkspaceSessionMenuEntry> Build(WorkspaceSessionMenuState state)
    {
        var gw = state.IsConnected;
        var canLifecycle = state.IsConnected && CanArchiveOrDelete(state.MainState);
        if (state.IsArchived)
            return BuildArchived(state, gw, canLifecycle);

        return
        [
            new(WorkspaceSessionMenuEntryKind.Item,
                state.IsPinned ? "SessionMenu_Unpin" : "SessionMenu_Pin",
                WorkspaceSessionMenuAction.TogglePin,
                state.IsPinned ? WorkspaceSessionMenuIcon.Unpin : WorkspaceSessionMenuIcon.Pin,
                "P", gw),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Rename",
                WorkspaceSessionMenuAction.Rename, WorkspaceSessionMenuIcon.Rename, "R", gw),
            new(WorkspaceSessionMenuEntryKind.Item,
                state.IsUnread ? "SessionMenu_MarkRead" : "SessionMenu_MarkUnread",
                WorkspaceSessionMenuAction.ToggleUnread,
                state.IsUnread ? WorkspaceSessionMenuIcon.MarkRead : WorkspaceSessionMenuIcon.MarkUnread,
                "U", gw),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Archive",
                WorkspaceSessionMenuAction.ToggleArchive, WorkspaceSessionMenuIcon.Archive, "A",
                IsEnabled: canLifecycle),
            new(WorkspaceSessionMenuEntryKind.Separator),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Fork",
                WorkspaceSessionMenuAction.Fork, WorkspaceSessionMenuIcon.Fork, "F", gw),
            CopySubMenu(state, gw),
            new(WorkspaceSessionMenuEntryKind.Separator),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Reset",
                WorkspaceSessionMenuAction.Reset, WorkspaceSessionMenuIcon.Reset, "R", gw),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Compact",
                WorkspaceSessionMenuAction.Compact, WorkspaceSessionMenuIcon.Compact, "C", gw),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Export",
                WorkspaceSessionMenuAction.ExportTranscript, WorkspaceSessionMenuIcon.Export, "E", gw),
            new(WorkspaceSessionMenuEntryKind.Separator),
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Delete",
                WorkspaceSessionMenuAction.Delete, WorkspaceSessionMenuIcon.Delete, "D",
                IsEnabled: canLifecycle, IsDestructive: true),
        ];
    }

    private static IReadOnlyList<WorkspaceSessionMenuEntry> BuildArchived(
        WorkspaceSessionMenuState state, bool gw, bool canLifecycle) =>
    [
        new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Unarchive",
            WorkspaceSessionMenuAction.ToggleArchive, WorkspaceSessionMenuIcon.Unarchive, "A", gw),
        new(WorkspaceSessionMenuEntryKind.Separator),
        new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Fork",
            WorkspaceSessionMenuAction.Fork, WorkspaceSessionMenuIcon.Fork, "F", gw),
        CopySubMenu(state, gw),
        new(WorkspaceSessionMenuEntryKind.Separator),
        new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_Delete",
            WorkspaceSessionMenuAction.Delete, WorkspaceSessionMenuIcon.Delete, "D",
            IsEnabled: canLifecycle, IsDestructive: true),
    ];

    private static WorkspaceSessionMenuEntry CopySubMenu(WorkspaceSessionMenuState state, bool gw)
    {
        var children = new List<WorkspaceSessionMenuEntry>
        {
            new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_CopyKey",
                WorkspaceSessionMenuAction.CopyKey, WorkspaceSessionMenuIcon.None),
        };
        if (state.HasSessionId)
            children.Add(new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_CopyId",
                WorkspaceSessionMenuAction.CopySessionId, WorkspaceSessionMenuIcon.None));
        children.Add(new(WorkspaceSessionMenuEntryKind.Item, "SessionMenu_CopyMarkdown",
            WorkspaceSessionMenuAction.CopyMarkdown, WorkspaceSessionMenuIcon.None, IsEnabled: gw));
        return new(WorkspaceSessionMenuEntryKind.SubMenu, "SessionMenu_Copy",
            WorkspaceSessionMenuAction.CopyKey, WorkspaceSessionMenuIcon.Copy, "C",
            Children: children);
    }
}
