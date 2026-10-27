namespace OpenClawTray.Services;

internal static class UsageCostApplicationPolicy
{
    public static bool CompleteRefresh(
        int responseDays, int selectedDays, DateTime responseUpdatedAt, DateTime lastUpdatedAt,
        int itemCount, AsyncListLoadingState loading)
    {
        if (responseUpdatedAt < lastUpdatedAt ||
            !ShouldApply(responseDays, selectedDays, loading.HasLoaded))
        {
            loading.Fail();
            return false;
        }
        loading.Complete(itemCount);
        return true;
    }

    public static bool IsCurrentRefresh(
        long revision, long currentRevision, int days, int selectedDays,
        bool sameClient, bool connected, long? epoch, long? currentEpoch) =>
        revision == currentRevision && days == selectedDays &&
        sameClient && connected && epoch == currentEpoch;

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
