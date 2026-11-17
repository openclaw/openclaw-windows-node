using System.Collections.Immutable;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Presentation;

namespace OpenClawTray.Dashboards;

/// <summary>
/// Page-owned inputs for the Reactor dashboards tree: the connection, agent
/// scope, and which dashboard is open. The Reactor root subscribes to
/// <see cref="Changed"/>; the hub page applies navigation and connection
/// changes here instead of remounting the tree.
/// </summary>
internal sealed class DashboardsPageState
{
    private string? _hubAgentId;

    public IBoardGatewayClient? Client { get; private set; }
    public bool Connected { get; private set; }
    public double ViewportWidth { get; private set; }
    public double TopInset { get; private set; }
    public void SetTopInset(double topInset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topInset);
        if (!double.IsFinite(topInset))
            throw new ArgumentOutOfRangeException(nameof(topInset));
        if (TopInset == topInset)
            return;
        TopInset = topInset;
        Changed?.Invoke();
    }

    public void SetViewportWidth(double width)
    {
        if (PageResponsiveLayout.Snap(ViewportWidth) == PageResponsiveLayout.Snap(width))
            return;
        ViewportWidth = width;
        Changed?.Invoke();
    }

    /// <summary>Bumps on every connection or handshake change so owners reload.</summary>
    public int ConnectionRevision { get; private set; }

    public ImmutableArray<string> AgentIds { get; private set; } = ["main"];
    public IReadOnlyDictionary<string, string> AgentNames { get; set; } = ImmutableDictionary<string, string>.Empty;
    public string AgentName(string id) => AgentNames.TryGetValue(id, out var name) ? name : id;
    public string AgentId { get; private set; } = "main";
    public bool NeedsDefaultAgent { get; private set; }
    public BoardSessionTarget? OpenTarget { get; private set; }

    /// <summary>Display title of the open dashboard, when the caller knows it.</summary>
    public string? OpenTitle { get; private set; }

    /// <summary>Gallery row of the open dashboard (author, live, updated), when opened from the gallery.</summary>
    public DashboardSessionRow? OpenRow { get; private set; }

    public string? ReturnCardId { get; private set; }
    public bool ReturnFocusPending { get; private set; }
    public int GalleryShown { get; set; } = 24;
    public string GalleryQuery { get; set; } = "";
    public DashboardGallerySort GallerySort { get; set; } = DashboardGallerySort.Updated;
    public void CompleteGalleryReturn() => ReturnFocusPending = false;

    public Action<string>? OpenChat { get; init; }
    public Action<string>? OpenAgentChat { get; init; }
    public Action? OpenConnection { get; init; }

    /// <summary>Opens the Gateway's web Dashboards gallery in the browser (not a specific board).</summary>
    public Action? OpenWebDashboards { get; init; }

    /// <summary>Copies a session key, the stable identity for finding a board elsewhere.</summary>
    public Action<string>? CopySessionKey { get; init; }
    public bool? CopySucceeded { get; private set; }
    public int CopyNoticeRevision { get; private set; }
    public bool DarkTheme { get; private set; }
    public void SetTheme(bool dark)
    {
        if (DarkTheme == dark)
            return;
        DarkTheme = dark;
        Changed?.Invoke();
    }
    public void ReportCopyResult(bool? succeeded)
    {
        CopySucceeded = succeeded;
        CopyNoticeRevision++;
        Changed?.Invoke();
    }
    public required Action<Action> Post { get; init; }
    public required Func<TimeSpan, Action, IDisposable> Schedule { get; init; }

    public event Action? Changed;

    public void ApplyNavigation(IEnumerable<string> agentIds, string hubAgentId, BoardSessionTarget? target)
    {
        if (_hubAgentId is null && target?.AgentId is null)
            NeedsDefaultAgent = true;
        if (target?.AgentId is not null)
            NeedsDefaultAgent = false;
        var agent = target?.AgentId ?? (_hubAgentId == hubAgentId ? AgentId : hubAgentId);
        _hubAgentId = hubAgentId;
        SetAgents(agentIds, agent);
        Open(target);
    }

    public void SetConnection(IBoardGatewayClient? client, bool connected)
    {
        if (ReferenceEquals(client, Client) && connected == Connected)
            return;
        Client = client;
        Connected = connected;
        ConnectionRevision++;
        Changed?.Invoke();
    }

    public void SetAgents(IEnumerable<string> agentIds, string currentAgentId)
    {
        var ids = agentIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToImmutableArray();
        var current = string.IsNullOrWhiteSpace(currentAgentId) ? "main" : currentAgentId;
        if (!ids.Contains(current))
            ids = ids.Insert(0, current);
        if (ids.SequenceEqual(AgentIds) && current == AgentId)
            return;
        AgentIds = ids;
        AgentId = current;
        Changed?.Invoke();
    }

    public void SelectAgent(string agentId)
    {
        NeedsDefaultAgent = false;
        if (agentId == AgentId || !AgentIds.Contains(agentId))
            return;
        AgentId = agentId;
        ReturnCardId = null;
        ReturnFocusPending = false;
        Changed?.Invoke();
    }

    public void ResolveDefaultAgent(IEnumerable<DashboardSessionRow> rows)
    {
        if (!NeedsDefaultAgent)
            return;
        NeedsDefaultAgent = false;
        var latest = rows.Where(row => row.AgentId is not null && AgentIds.Contains(row.AgentId))
            .OrderByDescending(row => row.UpdatedAtMs).FirstOrDefault();
        if (latest?.AgentId is { } agent)
            SelectAgent(agent);
    }

    public void Open(BoardSessionTarget? target, string? title = null, DashboardSessionRow? row = null, string? returnCardId = null)
    {
        if (target == OpenTarget && title == OpenTitle && ReferenceEquals(row, OpenRow))
            return;
        ReturnFocusPending = target is null && OpenTarget is not null && ReturnCardId is not null;
        if (target is not null)
            ReturnCardId = returnCardId;
        OpenTarget = target;
        OpenTitle = target is null ? null : title;
        OpenRow = target is null ? null : row;
        Changed?.Invoke();
    }
}
