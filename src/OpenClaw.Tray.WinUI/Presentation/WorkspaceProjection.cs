using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal sealed record WorkspaceAgent(string Id, string Name, string? LatestSessionKey, string? Emoji = null, string? AvatarUrl = null);
internal sealed record WorkspaceSession(string Key, string Title, string? AgentId,
    bool IsPinned = false, bool IsUnread = false, bool IsArchived = false, bool IsWorking = false);

internal sealed class WorkspaceAgentSelection
{
    private string? _gatewayId;
    private bool _awaitingGatewayRoster = true;
    public string? AgentId { get; private set; }
    public bool HasCurrentRoster { get; private set; }
    public bool HasSessionAccess { get; private set; }
    public bool IsReady { get; private set; }

    public void Select(string? agentId)
    {
        AgentId = agentId;
        IsReady = false;
    }

    public void Refresh(string? gatewayId, JsonElement? data, IReadOnlyList<WorkspaceAgent> agents,
        bool rosterUpdated = false, bool rosterUnsupported = false)
    {
        if (_gatewayId is not null && gatewayId is not null && _gatewayId != gatewayId)
        {
            AgentId = null;
            _awaitingGatewayRoster = true;
        }
        if (gatewayId is not null)
            _gatewayId = gatewayId;
        if (rosterUpdated && (WorkspaceProjection.HasAgentRoster(data) || rosterUnsupported))
            _awaitingGatewayRoster = false;

        // A target switch can precede delivery of the old cached roster's clear events.
        var currentRoster = _awaitingGatewayRoster ? null : data;
        var legacySessionAccess = !_awaitingGatewayRoster && rosterUnsupported;
        AgentId = legacySessionAccess ? null : WorkspaceProjection.SelectedAgentId(currentRoster, agents, AgentId);
        HasCurrentRoster = !_awaitingGatewayRoster && !legacySessionAccess &&
            WorkspaceProjection.HasAgentRoster(currentRoster);
        HasSessionAccess = HasCurrentRoster || legacySessionAccess;
        IsReady = legacySessionAccess ||
            HasCurrentRoster && WorkspaceProjection.IsAgentSelectionReady(currentRoster, agents, AgentId);
    }
}

internal static class WorkspaceProjection
{
    internal static bool HasAgentRoster(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array;

    public static IReadOnlyList<WorkspaceAgent> Agents(JsonElement? data, IReadOnlyList<SessionInfo> sessions)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
            return [];

        return agents.EnumerateArray()
            .Where(agent => agent.ValueKind == JsonValueKind.Object)
            .Select(agent =>
            {
                var id = ReadString(agent, "id") ?? string.Empty;
                var identity = agent.TryGetProperty("identity", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value : default;
                var related = Visible(sessions, id);
                return new WorkspaceAgent(
                    id, ReadString(identity, "name") ?? ReadString(agent, "name") ?? id,
                    related.OrderByDescending(session => session.UpdatedAt).FirstOrDefault()?.Key, ReadString(identity, "emoji"),
                    ReadString(identity, "avatarUrl") ?? ReadString(identity, "avatar"));
            })
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .DistinctBy(agent => agent.Id)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim() : null;

    public static string? SelectedAgentId(JsonElement? data, IReadOnlyList<WorkspaceAgent> agents, string? selectedId)
    {
        if (!HasAgentRoster(data))
            return selectedId;
        if (agents.Any(agent => agent.Id == selectedId))
            return selectedId;
        if (data is not { ValueKind: JsonValueKind.Object } root)
            return null;
        if (root.TryGetProperty("selectionRequired", out var required) && required.ValueKind == JsonValueKind.True)
            return null;
        var defaultId = ReadString(root, "defaultId");
        return agents.FirstOrDefault(agent => agent.Id == defaultId)?.Id
            ?? (agents.Count == 1 ? agents[0].Id : null);
    }

    public static bool RequiresAgentSelection(JsonElement? data, IReadOnlyList<WorkspaceAgent> agents, string? selectedId) =>
        data is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty("selectionRequired", out var required) && required.ValueKind == JsonValueKind.True &&
        SelectedAgentId(data, agents, selectedId) is null;

    public static bool IsAgentSelectionReady(JsonElement? data, IReadOnlyList<WorkspaceAgent> agents, string? selectedId) =>
        HasAgentRoster(data) && !RequiresAgentSelection(data, agents, selectedId);

    public static IReadOnlyList<WorkspaceSession> Sessions(IEnumerable<SessionInfo> source, string? agentId, WorkspaceSessionOrder order)
    {
        // Pinned rows first (most recently pinned on top); everything else keeps creation order (WorkspaceSessionOrder), so new input or activity never reorders the sidebar.
        var sessions = order.Sort(Visible(source, agentId))
            .OrderByDescending(s => s.Pinned)
            .ThenByDescending(s => s.Pinned ? s.PinnedAt ?? 0 : 0)
            .ToArray();
        return Project(sessions);
    }

    // The active sidebar hides archived rows; Settings archives must never
    // influence the "latest session" per agent. Filtering only; ordering lives in Sessions.
    private static IEnumerable<SessionInfo> Visible(IEnumerable<SessionInfo> source, string? agentId) =>
        source
            .Where(session => !session.Archived &&
                !SessionDisplayResolver.IsBackground(session) &&
                (agentId is null || string.Equals(SessionDisplayResolver.Resolve(session).AgentId, agentId, StringComparison.Ordinal)));

    private static IReadOnlyList<WorkspaceSession> Project(SessionInfo[] sessions)
    {
        var titles = SessionTitleFormatter.FormatUnique(sessions);
        return sessions.Select((session, index) => new WorkspaceSession(
            session.Key, titles[index], SessionDisplayResolver.Resolve(session).AgentId,
            session.Pinned, session.Unread, false, SessionRunState.IsWorking(session))).ToArray();
    }
}
