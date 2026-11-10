// <summary>
// Defines Workspace destinations, route dispatch, and the window's Back and Forward history.
// Settings tags are normalized so aliases share one history identity and unavailable sections can be pruned.
// Usage:
//   if (WorkspaceNavigation.TryResolveWorkspace(tag, out var destination))
//       history.Navigate(destination);
// </summary>

namespace OpenClawTray.Presentation;

/// <summary>Identifies a top-level Workspace page.</summary>
internal enum WorkspacePageId
{
    Home, Notifications, Settings
}

internal enum CompanionPageId
{
    Connection, Sessions, Instances, Cron, Agents,
    Config, Usage, LocalAi, DefaultModel, Voice, Permissions, Sandbox, Debug, Settings, About
}

/// <summary>
/// One entry in the Workspace window's single history. <see cref="SessionKey"/> identifies a chat;
/// <see cref="CompanionTag"/> identifies the Settings section shown in the same window.
/// <see cref="Embedded"/> shows that section beside the Workspace sidebar, without Settings' own
/// section list, instead of replacing the whole window.
/// </summary>
internal sealed record WorkspaceDestination(
   WorkspacePageId Page, string? SessionKey = null, string? CompanionTag = null, bool Embedded = false)
{
   /// <summary>
   /// Creates a native page destination. Dashboards always uses the standalone pinned-page
   /// presentation, including session links and command navigation, never the Settings section list.
   /// </summary>
   public static WorkspaceDestination Settings(string companionTag, bool embedded = false) =>
       new(WorkspacePageId.Settings, null, companionTag, embedded || DashboardsRoute.IsDashboardsTag(companionTag));

   /// <summary>The replacement for a pruned section keeps the presentation the user was in.</summary>
   public static WorkspaceDestination PrunedFallback(WorkspaceDestination current, string fallbackTag) =>
       Settings(fallbackTag, current.Page == WorkspacePageId.Settings && current.Embedded);

   /// <summary>
   /// The control to refocus when leaving Settings: the one that opened it, unless a sidebar row was
   /// invoked to leave (a deliberate navigation that owns focus). History exits always restore it.
   /// </summary>
   public static T? SettingsExitFocus<T>(T? saved, SettingsExitCause cause) where T : class =>
       cause == SettingsExitCause.SidebarActivation ? null : saved;
}

public enum SettingsExitCause { History, SidebarActivation }

/// <summary>
/// Escape leaves full Settings for the app, matching the macOS Settings sidebar. Anything that already
/// owns Escape keeps it: a control that handled the key, an open flyout, menu, picker or dialog, and
/// text or selection editors. Auto-repeat and pinned pages beside the sidebar never leave.
/// </summary>
internal static class SettingsEscapePolicy
{
    public static bool ExitsSettings(
        WorkspaceDestination current, bool handled, bool isRepeat, bool overlayOpen, bool focusInEditor) =>
        current is { Page: WorkspacePageId.Settings, Embedded: false }
        && !handled && !isRepeat && !overlayOpen && !focusInEditor;
}

/// <summary>
/// Window boundary and route identity for the approved Workspace. Agent file
/// workspaces remain companion routes; they are not the application's landing page.
/// </summary>
internal static class WorkspaceNavigation
{
    public static IReadOnlyDictionary<string, WorkspacePageId> Routes { get; } =
        new Dictionary<string, WorkspacePageId>(StringComparer.Ordinal)
        {
            ["home"] = WorkspacePageId.Home,
            ["notifications"] = WorkspacePageId.Notifications,
        };

    private static readonly HashSet<string> DeprecatedRoutes = new(StringComparer.Ordinal)
    {
        "agents", "agent-detail", "writer-detail", "dashboards", "dashboard-detail", "canvas",
        "systems", "system-detail", "automations", "automation-detail", "plugins", "skills",
        "sessions", "usage", "activity", "tasks", "meetings", "apps", "portals", "more",
        "agentevents"
    };

    /// <summary>Dispatches a route to the Workspace host or companion navigation host.</summary>
    public static void Dispatch(
        string? tag,
        Action<WorkspaceDestination> openWorkspace,
        Action<string> openCompanion,
        Action<string> openDashboard)
    {
        if (tag is "channels" or "activity")
        {
            openDashboard("channels");
            return;
        }

        if (TryResolveWorkspace(tag, out var destination))
        {
            openWorkspace(destination);
            return;
        }

        if (tag!.StartsWith("workspace:", StringComparison.Ordinal))
            throw new ArgumentException("Unknown Workspace route. Use workspace:home or workspace:notifications.", nameof(tag));

        if (tag != "command-center" &&
            HubPageRegistry.ResolvePage(HubPageRegistry.NormalizeTag(tag, "main")) is null)
            throw new ArgumentException("Unknown companion Settings route.", nameof(tag));

        openCompanion(tag);
    }

    /// <summary>
    /// Reports destination admission using the same route policy. Native hosts may complete
    /// synchronously; Dashboard hosts wait for Windows to accept the browser launch.
    /// </summary>
    public static Task<bool> DispatchAsync(
        string? tag,
        Func<WorkspaceDestination, Task<bool>> openWorkspace,
        Func<string, Task<bool>> openCompanion,
        Func<string, Task<bool>> openDashboard)
    {
        Task<bool>? admission = null;
        Dispatch(tag, destination => admission = openWorkspace(destination),
            section => admission = openCompanion(section), path => admission = openDashboard(path));
        return admission ?? throw new InvalidOperationException("The navigation route was not dispatched.");
    }

    /// <summary>Resolves a tag to a Workspace destination without dispatching it.</summary>
    public static bool TryResolveWorkspace(string? tag, out WorkspaceDestination destination)
    {
        destination = new(WorkspacePageId.Home);
        if (tag is null or "hub" or "home" or "workspace" or "chat")
            return true;

        if (!tag.StartsWith("workspace:", StringComparison.Ordinal))
            return false;
        var route = tag["workspace:".Length..];
        if (Routes.TryGetValue(route, out var page))
        {
            destination = new(page);
            return true;
        }
        // Old prototype links return to chat, never adding removed surfaces to history.
        // Unprefixed companion routes retain their existing settings-page meaning.
        return DeprecatedRoutes.Contains(route);
    }

    /// <summary>Returns the canonical companion tag for a companion page and optional agent scope.</summary>
    public static string CompanionTag(CompanionPageId page, string agentId = "main") => page switch
    {
        CompanionPageId.LocalAi => "local-ai",
        CompanionPageId.DefaultModel => "model",
        CompanionPageId.Agents => $"agent:{agentId}",
        _ => page.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Canonical identity of a Settings history entry. Aliases collapse to one section and agent
    /// scope stays in the tag, except About, which keeps its own identity so Back restores it.
    /// </summary>
    public static string NormalizeSettingsTag(string tag, string agentId) =>
        HubPageRegistry.NormalizeTag(tag, agentId);

}

/// <summary>Maintains one Workspace window history and its retained chat destination.</summary>
internal sealed class WorkspaceNavigationHistory
{
    private readonly Stack<WorkspaceDestination> _back = new();
    private readonly Stack<WorkspaceDestination> _forward = new();
    public WorkspaceDestination Current { get; private set; } = new(WorkspacePageId.Home);
    public WorkspaceDestination ChatDestination { get; private set; } = new(WorkspacePageId.Home);
    private WorkspaceDestination _appDestination = new(WorkspacePageId.Home);

    /// <summary>
    /// Where Settings' Back to app returns: the most recently shown app destination (chat, Notifications
    /// or a pinned page beside the sidebar), with chat resolved to the retained conversation.
    /// </summary>
    public WorkspaceDestination AppDestination =>
        _appDestination.Page == WorkspacePageId.Home ? ChatDestination : _appDestination;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Selects a chat session, preserving the current non-chat page when appropriate.</summary>
    public bool SelectChatSession(string sessionKey, string? previousSessionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var destination = new WorkspaceDestination(WorkspacePageId.Home, sessionKey);
        if (Current.Page == WorkspacePageId.Home)
        {
            // Initial cached/default selection lives in the composer until its first handoff.
            var bindPrevious = Current.SessionKey is null && !string.IsNullOrWhiteSpace(previousSessionKey);
            if (bindPrevious)
                SetCurrent(new(WorkspacePageId.Home, previousSessionKey));
            return Navigate(destination) || bindPrevious;
        }
        if (ChatDestination == destination)
            return false;

        // A completed /new may arrive after navigation away from chat.
        // Retain its conversation without replacing the currently visible page.
        ChatDestination = destination;
        return true;
    }

    /// <summary>Removes a closed chat session from the current destination and both history stacks.</summary>
    public bool RemoveSession(string key, string? replacementKey)
    {
        var replacement = new WorkspaceDestination(WorkspacePageId.Home, replacementKey);
        var wasCurrent = Current.SessionKey == key;
        RemoveFrom(_back, key);
        RemoveFrom(_forward, key);
        if (ChatDestination.SessionKey == key)
            ChatDestination = replacement;
        if (wasCurrent)
            SetCurrent(replacement);
        return wasCurrent;
    }

    private static void RemoveFrom(Stack<WorkspaceDestination> history, string key) =>
        RemoveFrom(history, destination => destination.SessionKey == key);

    private static void RemoveFrom(
        Stack<WorkspaceDestination> history, Func<WorkspaceDestination, bool> shouldRemove)
    {
        var retained = history.Reverse().Where(destination => !shouldRemove(destination)).ToArray();
        history.Clear();
        foreach (var destination in retained)
            history.Push(destination);
    }

    /// <summary>
    /// Drops Back/Forward Settings entries whose section is no longer available (for example
    /// Gateway sections after disconnect). The current destination is left to the caller.
    /// </summary>
    public void RemoveSettingsEntries(Func<string, bool> shouldRemove)
    {
        bool Match(WorkspaceDestination destination) =>
            destination is { Page: WorkspacePageId.Settings, CompanionTag: { } tag } && shouldRemove(tag);
        RemoveFrom(_back, Match);
        RemoveFrom(_forward, Match);
        if (Match(_appDestination))
            _appDestination = new(WorkspacePageId.Home);
    }

    /// <summary>
    /// Removes unavailable Settings sections from Back/Forward and from <see cref="Current"/>.
    /// A pruned Current becomes <paramref name="fallback"/> without a history entry, and adjacent
    /// duplicates that pruning creates are collapsed so Back never lands on the page already shown.
    /// Returns true when Current changed and the caller must render it.
    /// </summary>
    public bool PruneSettings(Func<string, bool> shouldRemove, WorkspaceDestination fallback)
    {
        RemoveSettingsEntries(shouldRemove);
        var currentChanged = Current is { Page: WorkspacePageId.Settings, CompanionTag: { } tag } &&
            shouldRemove(tag);
        if (currentChanged)
            SetCurrent(fallback);
        CollapseAdjacentDuplicates();
        return currentChanged;
    }

    private void CollapseAdjacentDuplicates()
    {
        var back = _back.Reverse().ToList();
        var forward = _forward.ToList();
        var previous = (WorkspaceDestination?)null;
        var retainedBack = new List<WorkspaceDestination>();
        foreach (var destination in back)
        {
            if (destination != previous)
                retainedBack.Add(destination);
            previous = destination;
        }
        if (previous == Current && retainedBack.Count > 0)
            retainedBack.RemoveAt(retainedBack.Count - 1);
        previous = Current;
        var retainedForward = new List<WorkspaceDestination>();
        foreach (var destination in forward)
        {
            if (destination != previous)
                retainedForward.Add(destination);
            previous = destination;
        }
        _back.Clear();
        foreach (var destination in retainedBack)
            _back.Push(destination);
        _forward.Clear();
        foreach (var destination in Enumerable.Reverse(retainedForward))
            _forward.Push(destination);
    }

    /// <summary>Navigates to a destination and records the previous destination for Back.</summary>
    public bool Navigate(WorkspaceDestination destination)
    {
        if (destination == Current)
            return false;
        _back.Push(Current);
        _forward.Clear();
        SetCurrent(destination);
        return true;
    }

    /// <summary>Moves Back one destination when history is available.</summary>
    public bool GoBack()
    {
        if (!_back.TryPop(out var previous))
            return false;
        _forward.Push(Current);
        SetCurrent(previous);
        return true;
    }

    /// <summary>Moves Forward one destination when history is available.</summary>
    public bool GoForward()
    {
        if (!_forward.TryPop(out var next))
            return false;
        _back.Push(Current);
        SetCurrent(next);
        return true;
    }

    private void SetCurrent(WorkspaceDestination destination)
    {
        Current = destination;
        if (destination.Page == WorkspacePageId.Home)
            ChatDestination = destination;
        if (destination is not { Page: WorkspacePageId.Settings, Embedded: false })
            _appDestination = destination;
    }
}
