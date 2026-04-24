using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Virtual UIA peer for a single chart data point. Exposes grid-item,
/// table-item, and value patterns so screen readers can navigate and
/// read individual data points without per-point XAML elements.
/// </summary>
internal sealed partial class ChartPointProvider : AutomationPeer,
    IGridItemProvider,
    ITableItemProvider,
    IValueProvider
{
    private readonly ChartAutomationPeer _chartPeer;
    private readonly IChartAccessibilityData _data;
    private readonly int _seriesIndex;
    private readonly int _pointIndex;

    internal ChartPointProvider(
        ChartAutomationPeer chartPeer,
        IChartAccessibilityData data,
        int seriesIndex,
        int pointIndex)
    {
        _chartPeer = chartPeer;
        _data = data;
        _seriesIndex = seriesIndex;
        _pointIndex = pointIndex;
    }

    // ── AutomationPeer overrides ─────────────────────────────────────

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.DataItem;

    protected override string GetClassNameCore() => "ChartPoint";

    protected override string GetNameCore() => Value;

    protected override string GetAutomationIdCore()
        => $"ChartPoint_{_seriesIndex}_{_pointIndex}";

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface switch
        {
            PatternInterface.GridItem => this,
            PatternInterface.TableItem => this,
            PatternInterface.Value => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    protected override AutomationPeer? GetPeerFromPointCore(global::Windows.Foundation.Point point) => null;
    protected override global::Windows.Foundation.Rect GetBoundingRectangleCore() => default;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    // ── IGridItemProvider ────────────────────────────────────────────

    public int Row => _seriesIndex;
    public int Column => _pointIndex;
    public int RowSpan => 1;
    public int ColumnSpan => 1;

    public IRawElementProviderSimple? ContainingGrid
        => ProviderFromPeer(_chartPeer);

    // ── ITableItemProvider ───────────────────────────────────────────

    public IRawElementProviderSimple[] GetRowHeaderItems()
    {
        // Row header = the series name
        var series = GetSeries();
        if (series == null) return [];

        var headerPeer = new ChartSeriesHeaderPeer(_chartPeer, series.Name, _seriesIndex);
        var provider = ProviderFromPeer(headerPeer);
        return provider != null ? [provider] : [];
    }

    public IRawElementProviderSimple[] GetColumnHeaderItems()
    {
        // Column header = the x-axis label for this point
        var point = GetPoint();
        if (point == null) return [];

        var headerPeer = new ChartColumnHeaderPeer(_chartPeer, point.XLabel, _pointIndex);
        var provider = ProviderFromPeer(headerPeer);
        return provider != null ? [provider] : [];
    }

    // ── IValueProvider ───────────────────────────────────────────────

    public bool IsReadOnly => true;

    public string Value
    {
        get
        {
            var point = GetPoint();
            if (point == null) return string.Empty;

            // Use pre-formatted label if available
            if (!string.IsNullOrEmpty(point.FormattedLabel))
                return point.FormattedLabel;

            // Generate default: "{seriesName}, {xLabel}: {yValue}, point {i} of {n}"
            var series = GetSeries();
            if (series == null) return string.Empty;

            return FormatDefaultLabel(series, point);
        }
    }

    public void SetValue(string value)
    {
        // Read-only — chart data points are not editable via UIA
        throw new InvalidOperationException("Chart data points are read-only.");
    }

    // ── Internal helpers ─────────────────────────────────────────────

    internal static string FormatDefaultLabel(
        ChartSeriesDescriptor series,
        ChartPointDescriptor point,
        int pointIndex = -1,
        string? yUnits = null)
    {
        var name = series.Name;
        var xLabel = point.XLabel;
        var yFormatted = FormatYValue(point.YValue, yUnits);

        if (pointIndex >= 0)
        {
            var total = series.Points.Count;
            return $"{name}, {xLabel}: {yFormatted}, point {pointIndex + 1} of {total}";
        }

        return $"{name}, {xLabel}: {yFormatted}";
    }

    private static string FormatYValue(double value, string? units)
    {
        // Format intelligently: integers without decimals, others with reasonable precision
        var formatted = value == Math.Truncate(value)
            ? value.ToString("N0")
            : value.ToString("N2");

        return units != null ? $"{formatted}{units}" : formatted;
    }

    private ChartSeriesDescriptor? GetSeries()
        => _seriesIndex >= 0 && _seriesIndex < _data.Series.Count
            ? _data.Series[_seriesIndex]
            : null;

    private ChartPointDescriptor? GetPoint()
    {
        var series = GetSeries();
        if (series == null) return null;
        return _pointIndex >= 0 && _pointIndex < series.Points.Count
            ? series.Points[_pointIndex]
            : null;
    }

    private string FormatDefaultLabel(ChartSeriesDescriptor series, ChartPointDescriptor point)
    {
        // Find y-axis units from data if available
        var yUnits = _data.Axes
            .Where(a => a.AxisType == ChartAxisType.Y)
            .Select(a => a.Units)
            .FirstOrDefault();

        return FormatDefaultLabel(series, point, _pointIndex, yUnits);
    }
}
