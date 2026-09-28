using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal sealed record WorkspaceAgent(string Id, string Name, string? LatestSessionKey);
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
                var related = Sessions(sessions, id);
                return new WorkspaceAgent(
                    id, ReadString(agent, "name") ?? id, related.FirstOrDefault()?.Key);
            })
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .DistinctBy(agent => agent.Id)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

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
