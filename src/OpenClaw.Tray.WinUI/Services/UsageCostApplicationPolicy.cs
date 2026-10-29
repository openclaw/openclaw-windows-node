namespace OpenClawTray.Services;

internal static class UsageCostApplicationPolicy
{
    internal enum Decision
    {
        Apply,
        Ignore,
        IgnoreStale,
        StopForUnsupportedRange,
    }

    public static bool ShouldApply(int responseDays, int selectedPeriodDays)
    {
        if (responseDays <= 0)
            return true;

        // The gateway can compute the requested range inclusively (for example,
        // 8 for a 7-day request). Allow +/-1 so valid responses are not dropped.
        return Math.Abs(responseDays - selectedPeriodDays) <= 1;
    }

    public static Decision Decide(int responseDays, int selectedPeriodDays, bool hasLoaded)
    {
        if (ShouldApply(responseDays, selectedPeriodDays))
            return Decision.Apply;

        // A later payload for another period must not replace the selection.
        // The first completed response may be the only range an older gateway
        // returns. Stop loading and leave the selected period in place.
        return hasLoaded ? Decision.Ignore : Decision.StopForUnsupportedRange;
    }

    public static Decision Decide(
        int responseDays,
        int selectedPeriodDays,
        bool hasLoaded,
        DateTime updatedAt,
        DateTime lastAppliedUpdatedAt)
    {
        var rangeDecision = Decide(responseDays, selectedPeriodDays, hasLoaded);
        if (rangeDecision != Decision.Apply)
            return rangeDecision;

        return updatedAt < lastAppliedUpdatedAt
            ? Decision.IgnoreStale
            : Decision.Apply;
    }
}
