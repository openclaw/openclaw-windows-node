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
        var enabled = entry.IsEnabled && entry.Action switch
        {
            WorkspaceSessionMenuAction.CopyKey or WorkspaceSessionMenuAction.CopySessionId
                or WorkspaceSessionMenuAction.CopyMarkdown or WorkspaceSessionMenuAction.ExportTranscript => true,
            WorkspaceSessionMenuAction.Reset or WorkspaceSessionMenuAction.Compact => permissions.IsAdmin,
            WorkspaceSessionMenuAction.Delete => permissions.CanDelete(row),
            _ => permissions.CanWrite,
        } && !context.State.IsBusy;
        Action onClick = entry.Action switch
        {
            WorkspaceSessionMenuAction.ToggleArchive when row.Archived => () => _ = controller.UnarchiveAsync(row, title),
            WorkspaceSessionMenuAction.ToggleArchive => () => _ = controller.ArchiveAsync(row, title),
            WorkspaceSessionMenuAction.Delete => () => _ = controller.DeleteAsync(row, title),
            _ => () => _ = controller.RunSharedActionAsync(entry.Action, row, title),
        };
        return SessionsHubView.Item(label, Glyph(entry.Icon), onClick, enabled);
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

    /// <summary>Move to group, assign owner, and stop cloud worker.</summary>
    private static IEnumerable<MenuFlyoutItemBase> Organization(SessionsHubViewContext context, SessionInfo row, string title)
    {
        var controller = context.Controller;
        var state = context.State;
        var canWrite = state.Permissions.CanWrite && !state.IsBusy;
        string T(string key) => SessionsHubPresentation.T(key);
        yield return MenuSubItem(T("MoveToGroup"), GroupItems(context, row)) with { IconElement = FontIcon(FluentIconCatalog.Folder) };
        yield return MenuSubItem(T("ColorMenu"), ColorItems(context, row)) with { IconElement = FontIcon(ColorGlyph) };
        yield return MenuSubItem(T("IconMenu"), IconItems(context, row)) with { IconElement = FontIcon(FluentIconCatalog.Develop) };
        if (state.Permissions.Supports("sessions.assignOwner"))
            yield return MenuSubItem(T("AssignOwner"), OwnerItems(context, row)) with { IconElement = FontIcon(FluentIconCatalog.People) };
        if (SessionsHubProjection.IsCloudWorker(row))
            yield return SessionsHubView.Item(T("StopWorker"), FluentIconCatalog.Stop,
                () => _ = controller.StopCloudWorkerAsync(row, title),
                canWrite && SessionsHubProjection.CanStopCloudWorker(row) && state.Permissions.Supports("sessions.reclaim"));
    }

    private const string ColorGlyph = "\uE790";

    public static MenuFlyoutItemBase[] ColorItems(SessionsHubViewContext context, SessionInfo row)
    {
        var controller = context.Controller;
        var enabled = context.State.Permissions.CanWrite && !context.State.IsBusy;
        var current = SessionAppearance.NormalizeColor(row.Color);
        var items = new List<MenuFlyoutItemBase>
        {
            Radio(SessionsHubPresentation.T("ColorNone"), "session-color", current is null,
                () => _ = controller.PatchAsync(row, new SessionPatch { Color = SessionPatch.Clear }), enabled),
        };
        foreach (var id in SessionAppearance.ColorIds)
        {
            var value = id;
            items.Add(Radio(SessionsHubPresentation.ColorLabel(value), "session-color", current == value,
                () => _ = controller.PatchAsync(row, new SessionPatch { Color = value }), enabled));
        }
        return items.ToArray();
    }

    public static MenuFlyoutItemBase[] IconItems(SessionsHubViewContext context, SessionInfo row)
    {
        var controller = context.Controller;
        var enabled = context.State.Permissions.CanWrite && !context.State.IsBusy;
        var current = SessionAppearance.NormalizeIcon(row.Icon);
        var items = new List<MenuFlyoutItemBase>
        {
            Radio(SessionsHubPresentation.T("IconDefault"), "session-icon", string.IsNullOrWhiteSpace(row.Icon),
                () => _ = controller.PatchAsync(row, new SessionPatch { Icon = SessionPatch.Clear }), enabled),
        };
        foreach (var id in SessionAppearance.IconIds)
        {
            var value = id;
            items.Add(Radio(SessionsHubPresentation.IconLabel(value), "session-icon", current == value,
                () => _ = controller.PatchAsync(row, new SessionPatch { Icon = value }), enabled));
        }
        return items.ToArray();
    }

    /// <summary>Radio entry that degrades to a disabled, checkmarked item without write access.</summary>
    private static MenuFlyoutItemBase Radio(string text, string group, bool isChecked, Action onClick, bool enabled) =>
        enabled
            ? new RadioMenuFlyoutItemData(text, group, isChecked, onClick)
            : SessionsHubView.Item(text, isChecked ? FluentIconCatalog.Check : null, () => { }, enabled: false);

    public static MenuFlyoutItemBase[] GroupItems(SessionsHubViewContext context, SessionInfo row)
    {
        var controller = context.Controller;
        var enabled = context.State.Permissions.CanWrite && !context.State.IsBusy;
        string T(string key) => SessionsHubPresentation.T(key);
        var items = new List<MenuFlyoutItemBase>();
        foreach (var group in controller.KnownGroups())
        {
            var name = group;
            items.Add(Radio(name, "session-group", row.Category == name, () => _ = controller.MoveToGroupAsync(row, name), enabled));
        }
        if (items.Count > 0)
            items.Add(MenuSeparator());
        items.Add(SessionsHubView.Item(T("NewGroupEllipsis"), FluentIconCatalog.Add,
            () => _ = controller.NewGroupAsync(row), enabled && context.State.Permissions.CanCreateGroup));
        if (row.Category is not null)
            items.Add(SessionsHubView.Item(T("RemoveFromGroup"), null, () => _ = controller.MoveToGroupAsync(row, null), enabled));
        return items.ToArray();
    }

    public static MenuFlyoutItemBase[] OwnerItems(SessionsHubViewContext context, SessionInfo row)
    {
        var controller = context.Controller;
        var state = context.State;
        var enabled = state.Permissions.CanWrite && !state.IsBusy;
        string T(string key) => SessionsHubPresentation.T(key);
        var current = row.Owner;
        bool IsCurrent(string type, string id) => current?.Type == type && current.Id == id;
        var items = new List<MenuFlyoutItemBase>();
        if (state.SelfProfileId is { } self)
            items.Add(new RadioMenuFlyoutItemData(T("AssignToMe"), "session-owner", IsCurrent("human", self),
                () => _ = controller.AssignOwnerAsync(row, "human", self)));
        foreach (var profile in state.Profiles.Where(profile => profile.Id != state.SelfProfileId))
        {
            var id = profile.Id;
            items.Add(new RadioMenuFlyoutItemData(profile.Label, "session-owner", IsCurrent("human", id),
                () => _ = controller.AssignOwnerAsync(row, "human", id)));
        }
        if (state.Agents.Count > 0)
        {
            if (items.Count > 0) items.Add(MenuSeparator());
            foreach (var agent in state.Agents)
            {
                var id = agent.Id;
                items.Add(new RadioMenuFlyoutItemData(F("AgentOwner", agent.Name), "session-owner", IsCurrent("agent", id),
                    () => _ = controller.AssignOwnerAsync(row, "agent", id)));
            }
        }
        if (items.Count == 0)
            items.Add(SessionsHubView.Item(T("NoOwners"), null, () => { }, enabled: false));
        return enabled ? items.ToArray() : items.Select(Disable).ToArray();

        static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);
        static MenuFlyoutItemBase Disable(MenuFlyoutItemBase item) => item switch
        {
            RadioMenuFlyoutItemData radio => SessionsHubView.Item(radio.Text, radio.IsChecked ? FluentIconCatalog.Check : null, () => { }, enabled: false),
            _ => item,
        };
    }

    public static MenuFlyoutItemBase[] GroupHeaderMenu(SessionsHubViewContext context, string group)
    {
        var controller = context.Controller;
        var enabled = context.State.Permissions.CanWrite && !context.State.IsBusy && context.State.GroupsSupported;
        string T(string key) => SessionsHubPresentation.T(key);
        return
        [
            SessionsHubView.Item(T("RenameGroup"), FluentIconCatalog.Rename, () => _ = controller.RenameGroupAsync(group), enabled),
            SessionsHubView.Item(T("DeleteGroup"), FluentIconCatalog.Delete, () => _ = controller.DeleteGroupAsync(group), enabled),
        ];
    }
}
