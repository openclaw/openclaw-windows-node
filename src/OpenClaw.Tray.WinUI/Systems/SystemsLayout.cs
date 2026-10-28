using System;

namespace OpenClawTray.Systems;

/// <summary>
/// Size-driven layout for the Systems page. Breakpoints are discrete, so resizing re-renders only
/// when the layout actually changes. Sizes are the page root's, including padding.
/// </summary>
internal readonly record struct SystemsLayout(
    bool Stacked,
    double InventoryWidth,
    int StatColumns,
    int SummaryColumns,
    int CoresPerRow,
    bool ShowOverview,
    double ApprovalsMaxHeight)
{
    public const double PagePadding = 24;
    public const double PaneGap = 16;
    // Stacking costs vertical space the details need, so it is reserved for genuinely narrow pages.
    private const double StackBelow = 600;
    private const double CompactInventoryBelow = 1040;
    // Scroll gutter plus section padding around the statistics grid.
    private const double DetailsChrome = 12 + 36;
    private const double TileChrome = 28;
    private const double CoreCell = 12;

    // Below these heights the machines need the room: the subtitle and the summary strip (whose
    // counts and filters the list repeats) are hidden and the pairing list is capped lower.
    // The Hub allows a 400px minimum height.
    private const double ShortBelow = 560;
    private const double MediumBelow = 800;

    public static readonly SystemsLayout Default = For(1280, 900);

    public static SystemsLayout For(double width, double height)
    {
        var content = Math.Max(0, width - 2 * PagePadding);
        var stacked = content < StackBelow;
        // Stacked panes fill the width, so the list width is unused there; keeping it constant
        // means resizing re-renders only at breakpoints.
        var inventory = stacked ? 0 : content < CompactInventoryBelow ? 260 : 320;
        var details = (stacked ? content : content - inventory - PaneGap) - DetailsChrome;
        var stats = details >= 680 ? 3 : details >= 440 ? 2 : 1;
        var tile = (details - 12 * (stats - 1)) / stats;
        var cores = (int)Math.Clamp(Math.Floor((tile - TileChrome + 3) / CoreCell), 4, 16);
        var summary = content >= 1000 ? 5 : content >= 600 ? 3 : 2;
        var shortPage = height < ShortBelow;
        var approvals = shortPage ? 72 : height < MediumBelow ? 160 : 260;
        return new SystemsLayout(stacked, inventory, stats, summary, cores, !shortPage, approvals);
    }
}
