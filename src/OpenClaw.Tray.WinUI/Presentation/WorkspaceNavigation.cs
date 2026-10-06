namespace OpenClawTray.Presentation;

internal enum WorkspacePageId
{
    Home, Notifications
}

internal enum CompanionPageId
{
    Connection, Sessions, Skills, Channels, Instances, Cron, AgentEvents, Agents,
    Bindings, Config, Usage, LocalAi, Voice, Permissions, Sandbox, Debug, Settings, About
}

internal sealed record WorkspaceDestination(WorkspacePageId Page, string? SessionKey = null);

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

}

internal sealed class WorkspaceNavigationHistory
{
    private readonly Stack<WorkspaceDestination> _back = new();
    private readonly Stack<WorkspaceDestination> _forward = new();
    public WorkspaceDestination Current { get; private set; } = new(WorkspacePageId.Home);
    public WorkspaceDestination ChatDestination { get; private set; } = new(WorkspacePageId.Home);
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

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

    private static void RemoveFrom(Stack<WorkspaceDestination> history, string key)
    {
        var retained = history.Reverse().Where(destination => destination.SessionKey != key).ToArray();
        history.Clear();
        foreach (var destination in retained)
            history.Push(destination);
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
