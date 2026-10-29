using System.Collections.Immutable;
using OpenClaw.Shared.Dashboards;

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

    /// <summary>Bumps on every connection or handshake change so owners reload.</summary>
    public int ConnectionRevision { get; private set; }

    public ImmutableArray<string> AgentIds { get; private set; } = ["main"];
    public string AgentId { get; private set; } = "main";
    public BoardSessionTarget? OpenTarget { get; private set; }

    /// <summary>Display title of the open dashboard, when the caller knows it.</summary>
    public string? OpenTitle { get; private set; }

    /// <summary>Gallery row of the open dashboard (author, live, updated), when opened from the gallery.</summary>
    public DashboardSessionRow? OpenRow { get; private set; }

    public Action<string>? OpenChat { get; init; }
    public Action? OpenConnection { get; init; }
    public required Action<Action> Post { get; init; }
    public required Func<TimeSpan, Action, IDisposable> Schedule { get; init; }

    public event Action? Changed;

    public void ApplyNavigation(IEnumerable<string> agentIds, string hubAgentId, BoardSessionTarget? target)
    {
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
        if (agentId == AgentId || !AgentIds.Contains(agentId))
            return;
        AgentId = agentId;
        Changed?.Invoke();
    }

    public void Open(BoardSessionTarget? target, string? title = null, DashboardSessionRow? row = null)
    {
        if (target == OpenTarget && title == OpenTitle && ReferenceEquals(row, OpenRow))
            return;
        OpenTarget = target;
        OpenTitle = target is null ? null : title;
        OpenRow = target is null ? null : row;
        Changed?.Invoke();
    }
}
