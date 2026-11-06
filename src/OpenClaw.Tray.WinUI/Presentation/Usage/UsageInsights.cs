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
                : providers.Providers.Where(provider => string.IsNullOrEmpty(provider.Error)
                        && provider.Windows.Any(window => window.UsedPercent >= 75
                            && (window.ResetAt is null || window.ResetAt > DateTimeOffset.Now)))
                    .OrderByDescending(provider => provider.Windows.Max(window => window.UsedPercent)).ToList();

    public static IReadOnlyList<UsageSessionRowView> LoadedSessions(
        UsageSessionsView sessions, UsageContributorKind kind, UsageContributor contributor) =>
        sessions.Rows.Where(row => kind switch
        {
            UsageContributorKind.Agent => row.AgentId == contributor.Label,
            UsageContributorKind.Model => row.Model == contributor.Label && row.Provider == contributor.Provider,
            _ => row.Provider == contributor.Label,
        }).ToList();
}
