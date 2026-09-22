using System.Text.Json;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>
/// Passive UIA acknowledgement of the snapshot consumed by a fixture render.
/// Provider/MCP readiness can precede UI delivery; no message content is exposed here.
/// </summary>
internal static class GatewayFixtureRenderObservation
{
    public static string Create(ChatDataSnapshot snapshot, string? selectedThreadId, bool fixtureEnabled)
    {
        if (!fixtureEnabled)
            return string.Empty;
        return JsonSerializer.Serialize(new
        {
            selectedThreadId,
            loadedThreadIds = snapshot.Timelines
                .Where(pair => pair.Value.HistoryLoaded)
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray()
        });
    }
}
