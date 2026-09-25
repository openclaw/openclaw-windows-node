namespace OpenClawTray.Presentation;

internal enum WorkspacePageId
{
    Home, Agents, AgentDetail, WriterDetail, Dashboards, DashboardDetail, Canvas,
    Systems, SystemDetail, Automations, AutomationDetail, Plugins, Skills,
    Sessions, Usage, Activity, Tasks, Meetings, Apps, Portals, Notifications, More
}

internal enum CompanionPageId
{
    Connection, Sessions, Skills, Channels, Instances, Cron, AgentEvents, Agents,
    Bindings, Config, Usage, LocalAi, Voice, Permissions, Sandbox, Debug, Settings, About
}

internal sealed record WorkspaceDestination(WorkspacePageId Page, string? ItemId = null);

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
            ["agents"] = WorkspacePageId.Agents,
            ["agent-detail"] = WorkspacePageId.AgentDetail,
            ["writer-detail"] = WorkspacePageId.WriterDetail,
            ["dashboards"] = WorkspacePageId.Dashboards,
            ["dashboard-detail"] = WorkspacePageId.DashboardDetail,
            ["canvas"] = WorkspacePageId.Canvas,
            ["systems"] = WorkspacePageId.Systems,
            ["system-detail"] = WorkspacePageId.SystemDetail,
            ["automations"] = WorkspacePageId.Automations,
            ["automation-detail"] = WorkspacePageId.AutomationDetail,
            ["plugins"] = WorkspacePageId.Plugins,
            ["skills"] = WorkspacePageId.Skills,
            ["sessions"] = WorkspacePageId.Sessions,
            ["usage"] = WorkspacePageId.Usage,
            ["activity"] = WorkspacePageId.Activity,
            ["tasks"] = WorkspacePageId.Tasks,
            ["meetings"] = WorkspacePageId.Meetings,
            ["apps"] = WorkspacePageId.Apps,
            ["portals"] = WorkspacePageId.Portals,
            ["notifications"] = WorkspacePageId.Notifications,
            ["more"] = WorkspacePageId.More,
        };

    public static IReadOnlyList<WorkspacePageId> PinnedPages { get; } =
    [
        WorkspacePageId.Home, WorkspacePageId.Agents, WorkspacePageId.Dashboards,
        WorkspacePageId.Systems, WorkspacePageId.Automations, WorkspacePageId.Plugins
    ];

    public static bool TryResolveWorkspace(string? tag, out WorkspaceDestination destination)
    {
        destination = new(WorkspacePageId.Home);
        if (tag is null or "hub" or "home" or "workspace" or "chat")
            return true;

        return tag.StartsWith("workspace:", StringComparison.Ordinal)
            && Routes.TryGetValue(tag["workspace:".Length..], out var page)
            && Assign(page, out destination);
    }

    private static bool Assign(WorkspacePageId page, out WorkspaceDestination destination)
    {
        destination = new(page);
        return true;
    }

    public static string CompanionTag(CompanionPageId page, string agentId = "main") => page switch
    {
        CompanionPageId.LocalAi => "local-ai",
        CompanionPageId.AgentEvents => "agentevents",
        CompanionPageId.Agents => $"agent:{agentId}",
        _ => page.ToString().ToLowerInvariant()
    };

    public static WorkspacePageId Section(WorkspacePageId page) => page switch
    {
        WorkspacePageId.AgentDetail or WorkspacePageId.WriterDetail => WorkspacePageId.Agents,
        WorkspacePageId.DashboardDetail or WorkspacePageId.Canvas => WorkspacePageId.Dashboards,
        WorkspacePageId.SystemDetail => WorkspacePageId.Systems,
        WorkspacePageId.AutomationDetail => WorkspacePageId.Automations,
        WorkspacePageId.Skills => WorkspacePageId.Plugins,
        _ when PinnedPages.Contains(page) => page,
        _ => WorkspacePageId.More
    };
}

internal sealed class WorkspaceNavigationHistory
{
    private readonly Stack<WorkspaceDestination> _back = new();
    private readonly Stack<WorkspaceDestination> _forward = new();
    public WorkspaceDestination Current { get; private set; } = new(WorkspacePageId.Home);
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public bool Navigate(WorkspaceDestination destination)
    {
        if (destination == Current)
            return false;
        _back.Push(Current);
        _forward.Clear();
        Current = destination;
        return true;
    }

    public bool GoBack()
    {
        if (!_back.TryPop(out var previous))
            return false;
        _forward.Push(Current);
        Current = previous;
        return true;
    }

    public bool GoForward()
    {
        if (!_forward.TryPop(out var next))
            return false;
        _back.Push(Current);
        Current = next;
        return true;
    }
}
