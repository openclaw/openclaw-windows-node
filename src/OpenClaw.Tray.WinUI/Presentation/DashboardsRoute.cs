using OpenClaw.Shared.Dashboards;

namespace OpenClawTray.Presentation;

/// <summary>
/// Hub route for the Dashboards page. <c>dashboards</c> opens the gallery;
/// <c>dashboards?session=&lt;key&gt;[&amp;agent=&lt;id&gt;]</c> opens one session's
/// dashboard. Session keys contain colons, so they travel URL-escaped.
/// </summary>
internal static class DashboardsRoute
{
    public const string Tag = "dashboards";
    private const string SessionPrefix = Tag + "?";

    public static bool IsDashboardsTag(string? tag) =>
        string.Equals(tag, Tag, StringComparison.Ordinal) ||
        tag?.StartsWith(SessionPrefix, StringComparison.Ordinal) == true;

    public static string ForSession(string sessionKey, string? agentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var resolvedAgent = string.IsNullOrWhiteSpace(agentId) ? AgentIdFromSessionKey(sessionKey) : agentId.Trim();
        var tag = SessionPrefix + "session=" + Uri.EscapeDataString(sessionKey.Trim());
        return resolvedAgent is null ? tag : tag + "&agent=" + Uri.EscapeDataString(resolvedAgent);
    }

    public static BoardSessionTarget? TryParseSession(string? tag)
    {
        if (tag?.StartsWith(SessionPrefix, StringComparison.Ordinal) != true)
            return null;
        string? session = null;
        string? agent = null;
        foreach (var part in tag[SessionPrefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            switch (part[..separator])
            {
                case "session":
                    session = value;
                    break;
                case "agent":
                    agent = value;
                    break;
            }
        }
        return string.IsNullOrWhiteSpace(session)
            ? null
            : new BoardSessionTarget(session.Trim(), string.IsNullOrWhiteSpace(agent) ? AgentIdFromSessionKey(session) : agent.Trim());
    }

    /// <summary>Agent id of an <c>agent:&lt;id&gt;:...</c> session key.</summary>
    public static string? AgentIdFromSessionKey(string? sessionKey)
    {
        if (sessionKey?.StartsWith("agent:", StringComparison.Ordinal) != true)
            return null;
        var parts = sessionKey.Split(':');
        return parts.Length >= 3 && parts[1].Length > 0 ? parts[1] : null;
    }
}
