using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal sealed record WorkspaceAgent(string Id, string Name, string? LatestSessionKey, string? Emoji = null, string? AvatarUrl = null);
internal sealed record WorkspaceSession(string Key, string Title, string? AgentId);

internal static class WorkspaceProjection
{
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
                var related = Sessions(sessions, id);
                return new WorkspaceAgent(
                    id, ReadString(identity, "name") ?? ReadString(agent, "name") ?? id,
                    related.FirstOrDefault()?.Key, ReadString(identity, "emoji"),
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

    public static IReadOnlyList<WorkspaceSession> Sessions(IEnumerable<SessionInfo> source, string? agentId)
    {
        var sessions = SessionVisibilityFilter.VisibleSessions(source, showCompleted: false)
            .Where(session => session.IsBackground != true &&
                (agentId is null || string.Equals(session.AgentId, agentId, StringComparison.Ordinal)))
            .OrderByDescending(session => session.UpdatedAt)
            .ToArray();
        var titles = SessionTitleFormatter.FormatUnique(sessions);
        return sessions.Select((session, index) => new WorkspaceSession(session.Key, titles[index], session.AgentId)).ToArray();
    }
}
