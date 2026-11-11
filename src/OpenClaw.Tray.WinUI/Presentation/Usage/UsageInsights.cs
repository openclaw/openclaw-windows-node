using OpenClaw.Shared;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Derived presentation facts, never inferred billing limits or exhaustive loaded-row analysis.</summary>
internal static class UsageInsights
{
    public static (string? Agent, double? CostShare, string? Model) HeroContributors(UsageDashboard dashboard)
    {
        var agent = dashboard.Contributors.GetValueOrDefault(UsageContributorKind.Agent)?
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Label));
        var model = dashboard.Contributors.GetValueOrDefault(UsageContributorKind.Model)?
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Label));
        var summary = dashboard.Summary;
        var share = agent is { CostKnown: true, Unpriced: false, LowerBound: false, CostIncomplete: false }
            && summary is { CostKnown: true, LowerBound: false, Partial: false, MissingCostEntries: 0, Cost: > 0 }
            && agent.Cost > 0 && agent.Cost <= summary.Cost
                ? agent.Cost / summary.Cost : (double?)null;
        // Independent aggregates do not establish an agent-to-model relationship.
        return (agent?.Label, share, model?.Label);
    }

    public static bool IsAutomation(string key) =>
        SessionDisplayResolver.Resolve(new SessionInfo { Key = key }).Classification == "cron";

    public static bool ContributorSharesKnown(UsageSummary summary, IReadOnlyList<UsageContributor> contributors, UsageMetric metric) =>
        !summary.Partial && !summary.LowerBound && (metric != UsageMetric.Cost || summary.MissingCostEntries == 0)
        && contributors.All(item => !item.LowerBound &&
            (metric == UsageMetric.Cost ? item.CostKnown && !item.CostIncomplete && !item.Unpriced : item.TokensKnown));

    public static UsageSessionRowView? CostliestLoadedSession(UsageDashboard dashboard) =>
        dashboard.Summary.Partial || dashboard.Sessions.Rows.Any(row =>
            !row.CostKnown || row.CostIncomplete || row.LowerBound || row.Computing)
            ? null
            : dashboard.Sessions.Rows.Where(row => row.Cost > 0).OrderByDescending(row => row.Cost).FirstOrDefault();

    /// <summary>Keeps the browsing limit, plus at most one explicitly selected loaded session.</summary>
    public static IReadOnlyList<UsageSessionRowView> VisibleSessions(UsageSessionsView sessions, int limit, string? selectedKey)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (sessions.ServerPaged) return sessions.Rows;
        var visible = sessions.Rows.Take(limit).ToList();
        if (selectedKey is not null && visible.All(row => row.Key != selectedKey)
            && sessions.Rows.FirstOrDefault(row => row.Key == selectedKey) is { } selected)
            visible.Insert(0, selected);
        return visible;
    }

    public static IReadOnlyList<ProviderUsageSnapshot> Attention(UsagePageState state, DateTimeOffset? now = null) =>
        state.Availability != UsageAvailability.Connected || state.Providers.Phase != UsageLoadPhase.Loaded
            || state.Providers.Value is not { Refreshing: false } providers
                ? []
                : UsageProviderAttention.Select(providers, now ?? DateTimeOffset.Now);

    public static IReadOnlyList<UsageSessionRowView> LoadedSessions(
        UsageSessionsView sessions, UsageContributorKind kind, UsageContributor contributor) =>
        sessions.Rows.Where(row => kind switch
        {
            UsageContributorKind.Agent => row.AgentId == contributor.Label,
            UsageContributorKind.Model => row.Model == contributor.Label && row.Provider == contributor.Provider,
            _ => row.Provider == contributor.Label,
        }).ToList();
}
