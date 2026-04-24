namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Abstraction that chart elements implement to expose their data to
/// <see cref="ChartAutomationPeer"/> without coupling the peer to any
/// concrete chart type.
/// </summary>
internal interface IChartAccessibilityData
{
    string? Name { get; }
    string? Description { get; }
    IReadOnlyList<ChartSeriesDescriptor> Series { get; }
    IReadOnlyList<ChartAxisDescriptor> Axes { get; }
    ChartViewport? Viewport { get; }

    /// <summary>
    /// Human-readable chart type name for auto-generated accessible names
    /// (e.g., "Line", "Bar", "Pie", "Tree", "Force graph").
    /// </summary>
    string ChartTypeName => "Chart";
}

// ═══════════════════════════════════════════════════════════════════
//  Descriptor records — immutable snapshots of chart structure
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Describes a single data point in a chart series for accessibility purposes.
/// </summary>
/// <param name="XLabel">Human-readable x-axis label (e.g., "March 14").</param>
/// <param name="YValue">Numeric y value.</param>
/// <param name="FormattedLabel">
/// Pre-formatted label for the point (e.g., "$42,300 on March 14").
/// When null, the peer generates a default label.
/// </param>
public record ChartPointDescriptor(
    string XLabel,
    double YValue,
    string? FormattedLabel = null);

/// <summary>
/// Describes a single series in a chart.
/// </summary>
/// <param name="Name">Human-readable series name (e.g., "Revenue").</param>
/// <param name="Points">Ordered data points in this series.</param>
public record ChartSeriesDescriptor(
    string Name,
    IReadOnlyList<ChartPointDescriptor> Points);

/// <summary>
/// Describes a chart axis (x or y).
/// </summary>
/// <param name="AxisType">Which axis this descriptor represents.</param>
/// <param name="Label">Human-readable axis label (e.g., "Month").</param>
/// <param name="Min">Minimum visible value.</param>
/// <param name="Max">Maximum visible value.</param>
/// <param name="Units">Unit annotation (e.g., "USD", "°C").</param>
public record ChartAxisDescriptor(
    ChartAxisType AxisType,
    string? Label,
    double Min,
    double Max,
    string? Units = null);

/// <summary>
/// Describes the current visible viewport (for pan/zoom scenarios).
/// </summary>
/// <param name="XMin">Left edge of the visible range.</param>
/// <param name="XMax">Right edge of the visible range.</param>
/// <param name="YMin">Bottom edge of the visible range.</param>
/// <param name="YMax">Top edge of the visible range.</param>
public record ChartViewport(
    double XMin,
    double XMax,
    double YMin,
    double YMax);

/// <summary>Identifies which axis a descriptor represents.</summary>
public enum ChartAxisType
{
    X,
    Y,
}

/// <summary>
/// Attached to wrapper elements (e.g., <see cref="Core.FuncElement"/> from
/// keyboard-navigator wrapping) so the accessibility scanner can inspect the
/// inner chart canvas without needing to evaluate the render function.
/// </summary>
internal record ChartScannerHint(Core.CanvasElement InnerCanvas);
