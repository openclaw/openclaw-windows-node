using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Derives facts for the expanded session from its current Gateway summary.</summary>
internal static class UsageSessionDetailProjection
{
    /// <summary>Only a complete, priced range and completed session can establish an exact cost share.</summary>
    public static double? CostShare(SessionsUsageReport? report, SessionUsageRow? session)
    {
        if (report is null || report.Partial is not null || report.CacheStatus?.IsIncomplete == true
            || report.CacheStatus?.Partial is not null || report.Overview?.Report == UsageReportState.Unavailable
            || session is not { Computing: false, Usage.Totals: { } totals }
            || report.Sessions.Any(row => row.Computing))
            return null;
        var range = report.Totals;
        return range.HasValue("totalCost") && totals.HasValue("totalCost") && range.TotalCost > 0
            && !range.IsLowerBound && !totals.IsLowerBound && range.MissingCostEntries == 0 && totals.MissingCostEntries == 0
            && totals.TotalCost >= 0 && totals.TotalCost <= range.TotalCost
                ? totals.TotalCost / range.TotalCost : null;
    }
}
