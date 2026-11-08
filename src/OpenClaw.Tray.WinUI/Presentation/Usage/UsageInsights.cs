using OpenClaw.Shared;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Derived presentation facts, never inferred billing limits or exhaustive loaded-row analysis.</summary>
internal static class UsageInsights
{
    public static bool NearLimit(ProviderUsageSnapshot provider) =>
        provider.Windows.Any(window => window.UsedPercent >= 75);

    public static (string? Agent, double? CostShare, string? Model) HeroContributors(UsageDashboard dashboard)
    {
        var agent = dashboard.Contributors.GetValueOrDefault(UsageContributorKind.Agent)?
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Label));
        var model = dashboard.Contributors.GetValueOrDefault(UsageContributorKind.Model)?
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Label));
        var summary = dashboard.Summary;
        var share = agent is { CostKnown: true, Unpriced: false, LowerBound: false }
            && summary is { CostKnown: true, LowerBound: false, Partial: false, MissingCostEntries: 0, Cost: > 0 }
            && agent.Cost > 0 && agent.Cost <= summary.Cost
                ? agent.Cost / summary.Cost : (double?)null;
        // Independent aggregates do not establish an agent-to-model relationship.
        return (agent?.Label, share, model?.Label);
    }

    public static bool IsAutomation(string key) =>
        SessionDisplayResolver.Resolve(new SessionInfo { Key = key }).Classification == "cron";

    public static IReadOnlyList<ProviderUsageSnapshot> Attention(UsagePageState state) =>
        state.Availability != UsageAvailability.Connected || state.Providers.Phase != UsageLoadPhase.Loaded
            || state.Providers.Value is not { Refreshing: false } providers
                ? []
                : UsageProviderAttention.Select(providers, DateTimeOffset.Now);

    public static IReadOnlyList<UsageSessionRowView> LoadedSessions(
        UsageSessionsView sessions, UsageContributorKind kind, UsageContributor contributor) =>
        sessions.Rows.Where(row => kind switch
        {
            UsageContributorKind.Agent => row.AgentId == contributor.Label,
            UsageContributorKind.Model => row.Model == contributor.Label && row.Provider == contributor.Provider,
            _ => row.Provider == contributor.Label,
        }).ToList();
}
