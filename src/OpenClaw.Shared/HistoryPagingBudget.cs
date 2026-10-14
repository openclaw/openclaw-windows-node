using System;

namespace OpenClaw.Shared;

/// <summary>
/// U1 companion reliability: bounded paging budget for history reconstruction.
/// Pure policy so the numbers can be unit-tested without the UI: how many items to
/// request per page, how many items may be folded per UI tick, and whether another
/// page is required. Keeping this out of the UI thread logic is what makes the
/// rebuild bounded and cancellable.
/// </summary>
internal static class HistoryPagingBudget
{
    public const int DefaultPageSize = 200;
    public const int DefaultItemsPerTick = 250;

    /// <summary>Rows to request for the next page (0 when nothing remains).</summary>
    public static int NextPageSize(int remaining, int pageSize = DefaultPageSize)
        => remaining <= 0 || pageSize <= 0 ? 0 : Math.Min(remaining, pageSize);

    /// <summary>Rows that may be folded during this UI tick (0 when nothing pending).</summary>
    public static int ItemsThisTick(int pending, int budget = DefaultItemsPerTick)
        => pending <= 0 || budget <= 0 ? 0 : Math.Min(pending, budget);

    /// <summary>True when a full page came back, so paging should continue.</summary>
    public static bool ShouldContinuePaging(int fetched, int pageSize)
        => fetched > 0 && pageSize > 0 && fetched >= pageSize;
}
