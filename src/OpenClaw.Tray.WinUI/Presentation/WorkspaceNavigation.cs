namespace OpenClawTray.Presentation;

internal enum WorkspacePageId
{
    Home, Notifications, Settings
}

internal enum CompanionPageId
{
    Connection, Sessions, Skills, Channels, Instances, Cron, AgentEvents, Agents,
    Bindings, Config, Usage, LocalAi, Voice, Permissions, Sandbox, Debug, Settings, About
}

/// <summary>
/// One entry in the Workspace window's single history. <see cref="SessionKey"/> identifies a chat;
/// <see cref="CompanionTag"/> identifies the Settings section shown in the same window.
/// </summary>
internal sealed record WorkspaceDestination(
    WorkspacePageId Page, string? SessionKey = null, string? CompanionTag = null)
{
    public static WorkspaceDestination Settings(string companionTag) =>
        new(WorkspacePageId.Settings, null, companionTag);
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
        "sessions", "usage", "activity", "tasks", "meetings", "apps", "portals", "more"
    };

    public static void Dispatch(
        string? tag,
        Action<WorkspaceDestination> openWorkspace,
        Action<string> openCompanion)
    {
        if (TryResolveWorkspace(tag, out var destination))
        {
            openWorkspace(destination);
            return;
        }

        if (tag!.StartsWith("workspace:", StringComparison.Ordinal))
            throw new ArgumentException("Unknown Workspace route. Use workspace:home or workspace:notifications.", nameof(tag));

        openCompanion(tag);
    }

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

    public static string CompanionTag(CompanionPageId page, string agentId = "main") => page switch
    {
        CompanionPageId.LocalAi => "local-ai",
        CompanionPageId.AgentEvents => "agentevents",
        CompanionPageId.Agents => $"agent:{agentId}",
        _ => page.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Canonical identity of a Settings history entry. Aliases collapse to one section and agent
    /// scope stays in the tag, except About, which keeps its own identity so Back restores it.
    /// </summary>
    public static string NormalizeSettingsTag(string tag, string agentId) =>
        tag is "about" or "info" ? "about" : HubPageRegistry.NormalizeTag(tag, agentId);

}

internal sealed class WorkspaceNavigationHistory
{
    private readonly Stack<WorkspaceDestination> _back = new();
    private readonly Stack<WorkspaceDestination> _forward = new();
    public WorkspaceDestination Current { get; private set; } = new(WorkspacePageId.Home);
    public WorkspaceDestination ChatDestination { get; private set; } = new(WorkspacePageId.Home);
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

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

    public bool Navigate(WorkspaceDestination destination)
    {
        if (destination == Current)
            return false;
        _back.Push(Current);
        _forward.Clear();
        SetCurrent(destination);
        return true;
    }

    public bool GoBack()
    {
        if (!_back.TryPop(out var previous))
            return false;
        _forward.Push(Current);
        SetCurrent(previous);
        return true;
    }

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
    }
}
