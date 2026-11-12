using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

/// <summary>
/// Session context menu for the Sessions page. Starts from the Workspace
/// sidebar's pure <see cref="WorkspaceSessionMenu"/> entries (same labels, order,
/// and main-session gating) and adds the page-only actions: open, move to group,
/// assign owner, stop cloud worker, and checkpoints. Mutating entries also honor
/// the operator's scopes.
/// </summary>
internal static class SessionsHubMenus
{
    public static MenuFlyoutItemBase[] SessionMenu(SessionsHubViewContext context, SessionInfo row, string title)
    {
        var controller = context.Controller;
        var state = context.State;
        var permissions = state.Permissions;
        string T(string key) => SessionsHubPresentation.T(key);
        var menuState = new WorkspaceSessionMenuState(
            row.Pinned, row.Unread, row.Archived, !string.IsNullOrWhiteSpace(row.SessionId),
            controller.MainState(row), state.IsConnected);

        var items = new List<MenuFlyoutItemBase>
        {
            SessionsHubView.Item(T("OpenInChat"), FluentIconCatalog.Chat, () => controller.OpenInChat(row.Key), row.Kind != "global"),
            SessionsHubView.Item(LocalizationHelper.GetString("SessionsPage_OpenDashboard.Text"), FluentIconCatalog.DashboardView,
                () => controller.OpenSessionDashboard(row.Key)),
            MenuSeparator(),
        };
        var extrasInserted = false;
        foreach (var entry in WorkspaceSessionMenu.Build(menuState))
        {
            if (entry.Kind == WorkspaceSessionMenuEntryKind.Separator)
            {
                items.Add(MenuSeparator());
                if (!extrasInserted)
                {
                    items.AddRange(Organization(context, row, title));
                    items.Add(MenuSeparator());
                    extrasInserted = true;
                }
                continue;
            }
            items.Add(Render(context, entry, row, title));
            if (entry.Action == WorkspaceSessionMenuAction.Compact)
                items.Add(SessionsHubView.Item(T("Checkpoints"), FluentIconCatalog.AgentEvents,
                    () => _ = controller.ShowCheckpointsAsync(row, title), state.IsConnected));
        }
        return items.ToArray();
    }

    private static MenuFlyoutItemBase Render(
        SessionsHubViewContext context, WorkspaceSessionMenuEntry entry, SessionInfo row, string title)
    {
        var controller = context.Controller;
        var permissions = context.State.Permissions;
        var label = WorkspaceWindow.Text(entry.LabelKey);
        if (entry.Kind == WorkspaceSessionMenuEntryKind.SubMenu)
        {
            return MenuSubItem(label, (entry.Children ?? []).Select(child => Render(context, child, row, title)).ToArray())
                with { IconElement = FontIcon(FluentIconCatalog.Copy) };
        }
        var isMutation = entry.Action is not (WorkspaceSessionMenuAction.CopyKey or WorkspaceSessionMenuAction.CopySessionId
            or WorkspaceSessionMenuAction.CopyMarkdown or WorkspaceSessionMenuAction.ExportTranscript);
        var enabled = entry.IsEnabled && (entry.Action switch
        {
            WorkspaceSessionMenuAction.CopyKey or WorkspaceSessionMenuAction.CopySessionId
                or WorkspaceSessionMenuAction.CopyMarkdown or WorkspaceSessionMenuAction.ExportTranscript => true,
            WorkspaceSessionMenuAction.Reset or WorkspaceSessionMenuAction.Compact => permissions.IsAdmin,
            WorkspaceSessionMenuAction.Delete => permissions.CanDelete(row),
            _ => permissions.CanWrite,
        }) && !context.State.IsBusy && (!isMutation || context.State.CanMutateRows);
        Action onClick = entry.Action switch
        {
            WorkspaceSessionMenuAction.ToggleArchive when row.Archived => () => _ = controller.UnarchiveAsync(row, title),
            WorkspaceSessionMenuAction.ToggleArchive => () => _ = controller.ArchiveAsync(row, title),
            WorkspaceSessionMenuAction.Delete => () => _ = controller.DeleteAsync(row, title),
            _ => () => _ = controller.RunSharedActionAsync(entry.Action, row, title),
        };
        return WithRosterHelp(
            SessionsHubView.Item(label, Glyph(entry.Icon), onClick, enabled),
            context.State,
            isMutation);
    }

    private static string? Glyph(WorkspaceSessionMenuIcon icon) => icon switch
    {
        WorkspaceSessionMenuIcon.Pin => FluentIconCatalog.Pin,
        WorkspaceSessionMenuIcon.Unpin => FluentIconCatalog.Unpin,
        WorkspaceSessionMenuIcon.Rename => FluentIconCatalog.Rename,
        WorkspaceSessionMenuIcon.MarkUnread => FluentIconCatalog.MarkUnread,
        WorkspaceSessionMenuIcon.MarkRead => FluentIconCatalog.MarkRead,
        WorkspaceSessionMenuIcon.Archive => FluentIconCatalog.Archive,
        WorkspaceSessionMenuIcon.Unarchive => FluentIconCatalog.Unarchive,
        WorkspaceSessionMenuIcon.Fork => FluentIconCatalog.Fork,
        WorkspaceSessionMenuIcon.Copy => FluentIconCatalog.Copy,
        WorkspaceSessionMenuIcon.Reset => FluentIconCatalog.Reset,
        WorkspaceSessionMenuIcon.Compact => FluentIconCatalog.Compact,
        WorkspaceSessionMenuIcon.Export => FluentIconCatalog.Export,
        WorkspaceSessionMenuIcon.Delete => FluentIconCatalog.Delete,
        _ => null,
    };

    /// <summary>Move a session between the categories already present in the roster.</summary>
    private static IEnumerable<MenuFlyoutItemBase> Organization(SessionsHubViewContext context, SessionInfo row, string title)
    {
        string T(string key) => SessionsHubPresentation.T(key);
        yield return MenuSubItem(T("MoveToGroup"), GroupItems(context, row)) with { IconElement = FontIcon(FluentIconCatalog.Folder) };
    }

    /// <summary>Radio entry that degrades to a disabled, checkmarked item without write access.</summary>
    private static MenuFlyoutItemBase Radio(
        string text, string group, bool isChecked, Action onClick, bool enabled, string? disabledReason = null) =>
        enabled
            ? new RadioMenuFlyoutItemData(text, group, isChecked, onClick)
            : WithRosterHelp(
                SessionsHubView.Item(text, isChecked ? FluentIconCatalog.Check : null, () => { }, enabled: false),
                disabledReason);

    public static MenuFlyoutItemBase[] GroupItems(SessionsHubViewContext context, SessionInfo row)
    {
        var controller = context.Controller;
        var state = context.State;
        var enabled = state.Permissions.CanWrite && state.CanMutateRows;
        var unavailableReason = !state.CanMutateRows ? SessionsHubPresentation.T("RosterNotCurrent") : null;
        string T(string key) => SessionsHubPresentation.T(key);
        var items = new List<MenuFlyoutItemBase>();
        foreach (var group in controller.KnownGroups())
        {
            var name = group;
            items.Add(Radio(name, "session-group", row.Category == name, () => _ = controller.MoveToGroupAsync(row, name), enabled,
                unavailableReason));
        }
        if (items.Count > 0)
            items.Add(MenuSeparator());
        if (row.Category is not null)
            items.Add(WithRosterHelp(SessionsHubView.Item(T("RemoveFromGroup"), null,
                () => _ = controller.MoveToGroupAsync(row, null), enabled), unavailableReason));
        return items.ToArray();
    }

    private static MenuFlyoutItemBase WithRosterHelp(
        MenuFlyoutItemBase item, SessionsHubState state, bool mutation) =>
        mutation && !state.CanMutateRows
            ? WithRosterHelp(item, SessionsHubPresentation.T("RosterNotCurrent"))
            : item;

    private static MenuFlyoutItemBase WithRosterHelp(MenuFlyoutItemBase item, string? reason)
    {
        return reason is not null && item is MenuFlyoutItemData data
            ? data with { Description = reason }
            : item;
    }

}
