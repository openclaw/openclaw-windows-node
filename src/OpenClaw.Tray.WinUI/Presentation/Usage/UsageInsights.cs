using OpenClaw.Shared;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Derived presentation facts, never inferred billing limits or exhaustive loaded-row analysis.</summary>
internal static class UsageInsights
{
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
