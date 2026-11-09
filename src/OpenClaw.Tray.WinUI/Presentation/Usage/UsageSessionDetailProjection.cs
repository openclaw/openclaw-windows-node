using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Selects the current Gateway summary for the expanded session.</summary>
internal static class UsageSessionDetailProjection
{
    /// <summary>
    /// The single-session read covers the selected range and scope, so it is preferred. The list row is the
    /// fallback while that read loads or when it fails.
    /// </summary>
    public static SessionUsageRow? SummaryRow(SessionUsageRow? listRow, SessionUsageRow? detail) => detail ?? listRow;

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
