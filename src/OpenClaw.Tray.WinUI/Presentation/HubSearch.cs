using System.Text;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal enum HubSearchGroup { Chats, Messages, Settings, Automations, Skills, Agents, Models, Dashboard }
internal enum HubSearchTargetKind { Page, Session, Dashboard }

internal sealed record HubSearchTarget(HubSearchTargetKind Kind, string Value, string? Focus = null);

internal sealed record HubSearchItem(
    string Id, HubSearchGroup Group, string Title, string? Subtitle, string Icon,
    HubSearchTarget Target, string Keywords = "", string? DashboardPath = null,
    bool ServerMatch = false, double Score = 0, DateTime? UpdatedAt = null)
{
    public bool OpensDashboard => Target.Kind == HubSearchTargetKind.Dashboard;
}

internal sealed record HubSearchMatch(HubSearchItem Item, int Rank);

internal static class HubSearch
{
    internal const int GroupLimit = 5;

    internal static IReadOnlyList<HubSearchMatch> Find(
        IEnumerable<HubSearchItem> items, string? text, int perGroupLimit = GroupLimit)
    {
        var query = Normalize(text).Trim();
        return items
            .Select(item => new HubSearchMatch(item, Rank(item, query)))
            .Where(match => query.Length == 0 || match.Rank > 0)
            .GroupBy(match => match.Item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(match => match.Rank)
                .ThenBy(match => match.Item.Group == HubSearchGroup.Messages).First())
            .GroupBy(match => match.Item.Group)
            .OrderByDescending(group => group.Max(match => match.Rank))
            .ThenBy(group => group.Key)
            .SelectMany(group => group.OrderByDescending(match => match.Rank)
                .ThenByDescending(match => match.Item.Score)
                .ThenByDescending(match => match.Item.UpdatedAt)
                .ThenBy(match => match.Item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(match => match.Item.Id, StringComparer.Ordinal)
                .Take(perGroupLimit))
            .ToArray();
    }

    private static int Rank(HubSearchItem item, string query)
    {
        if (query.Length == 0) return 0;
        var title = Normalize(item.Title);
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            Normalize(item.Keywords).Equals(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        var fields = $"{title}\n{Normalize(item.Subtitle)}\n{Normalize(item.Keywords)}";
        if (fields.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        // Full-text hits may match separated words or text outside the display snippet.
        return item.ServerMatch || query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => fields.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    internal static IEnumerable<HubSearchItem> Sessions(
        IEnumerable<SessionInfo> source, bool serverMatch = false, bool dashboardSupported = false)
    {
        var sessions = source.Where(session => !session.Archived &&
            !SessionDisplayResolver.IsBackground(session)).ToArray();
        var titles = SessionTitleFormatter.FormatUnique(sessions);
        return sessions.Select((session, index) => new HubSearchItem(
            $"session:{session.Key}", HubSearchGroup.Chats, titles[index],
            SessionDisplayResolver.Resolve(session).AgentId, "\uE8BD",
            new(HubSearchTargetKind.Session, session.Key),
            $"{session.Label} {session.Subject} {session.AgentId} {session.Key}",
            DashboardPath: dashboardSupported ? HubSearchCatalog.SessionPath(session.Key, session.IsMain) : null,
            ServerMatch: serverMatch, UpdatedAt: session.UpdatedAt)).ToArray();
    }

    internal static string Normalize(string? text) => (text ?? "").Normalize(NormalizationForm.FormC);
}
