namespace OpenClaw.SetupEngine;

/// <summary>
/// Decides whether setup footer actions share one right-aligned row or stack one per row.
/// </summary>
/// <remarks>
/// Footer actions share a 180-DIP minimum width. A failure footer with three actions, or a
/// window capped by a small or highly scaled work area, can leave the page column narrower
/// than one row needs, so the footer stacks instead of overflowing its column.
/// <code>
/// var stacked = !SetupFooterLayout.FitsOnOneRow([180, 180, 180], spacing: 8, available: 507);
/// </code>
/// </remarks>
public static class SetupFooterLayout
{
    // Absorbs sub-pixel rounding from layout so an exactly fitting row is not stacked.
    private const double Tolerance = 0.5;

    public static double RowWidth(IReadOnlyList<double> widths, double spacing) =>
        widths.Count == 0 ? 0 : widths.Sum() + spacing * (widths.Count - 1);

    public static double StackHeight(IReadOnlyList<double> heights, double spacing) =>
        heights.Count == 0 ? 0 : heights.Sum() + spacing * (heights.Count - 1);

    /// <summary>An unconstrained width always fits; otherwise the row must fit the available width.</summary>
    public static bool FitsOnOneRow(IReadOnlyList<double> widths, double spacing, double available) =>
        double.IsPositiveInfinity(available) || RowWidth(widths, spacing) <= available + Tolerance;
}
