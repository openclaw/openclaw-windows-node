namespace OpenClawTray.Services;

internal static class UsageCostApplicationPolicy
{
    public static bool ShouldApply(int responseDays, int selectedPeriodDays, bool hasLoaded)
    {
        if (responseDays <= 0)
            return true;

        // The gateway can compute the requested range inclusively (for example,
        // 8 for a 7-day request). Allow +/-1 so valid responses are not dropped.
        if (Math.Abs(responseDays - selectedPeriodDays) <= 1)
            return true;

        // Accept the first response even when an older gateway ignores days, so
        // the page does not spin forever waiting for an exact period match.
        return !hasLoaded;
    }
}
