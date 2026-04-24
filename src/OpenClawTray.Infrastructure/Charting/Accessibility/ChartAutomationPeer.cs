using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Root UIA peer for all Reactor chart types. Exposes the chart as a navigable
/// grid/table so screen readers see data points with series headers and axis labels.
/// </summary>
internal sealed partial class ChartAutomationPeer : FrameworkElementAutomationPeer, IGridProvider, ITableProvider, IScrollProvider
{
    private readonly IChartAccessibilityData _data;

    // Scroll/viewport state — updated when pan/zoom changes
    private double _horizontalScrollPercent = ScrollPatternIdentifiers.NoScroll;
    private double _verticalScrollPercent = ScrollPatternIdentifiers.NoScroll;
    private double _horizontalViewSize = 100;
    private double _verticalViewSize = 100;
    private bool _panEnabled;

    internal ChartAutomationPeer(FrameworkElement owner, IChartAccessibilityData data)
        : base(owner)
    {
        _data = data;
    }

    // ── AutomationPeer overrides ─────────────────────────────────────

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Group;

    protected override string GetClassNameCore() => "Chart";

    protected override string GetNameCore()
    {
        // Priority: explicit AutomationName > Title > auto-derived fallback
        // Ignore "Plot area" — that's an auto-set structural label from D3Dsl,
        // not an intentional chart name.
        var explicitName = base.GetNameCore();
        if (!string.IsNullOrWhiteSpace(explicitName) && explicitName != "Plot area")
            return explicitName;

        if (!string.IsNullOrWhiteSpace(_data.Name))
            return _data.Name;

        // Fallback: derive from chart type + series count
        var chartType = _data.ChartTypeName;
        var seriesCount = _data.Series.Count;
        return seriesCount switch
        {
            0 => $"Empty {chartType.ToLowerInvariant()} chart",
            1 => $"{chartType} chart with 1 series",
            _ => $"{chartType} chart with {seriesCount} series",
        };
    }

    protected override string GetFullDescriptionCore()
    {
        if (!string.IsNullOrWhiteSpace(_data.Description))
            return _data.Description;

        // Auto-generate a summary when no explicit description is set
        var summary = ChartSummarizer.Summarize(_data, _data.ChartTypeName);
        var formatted = ChartSummarizer.FormatSummary(summary);
        return string.IsNullOrWhiteSpace(formatted) ? string.Empty : formatted;
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var children = new List<AutomationPeer>();

        // Tab order: axes (structural) first, then data points
        // This follows the spec's Title/toolbar → Legend → Plot area ordering
        // within the UIA child tree.
        for (int ai = 0; ai < _data.Axes.Count; ai++)
        {
            children.Add(new ChartAxisProvider(this, _data.Axes[ai]));
        }

        for (int si = 0; si < _data.Series.Count; si++)
        {
            var series = _data.Series[si];
            for (int pi = 0; pi < series.Points.Count; pi++)
            {
                children.Add(new ChartPointProvider(this, _data, si, pi));
            }
        }

        return children;
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface switch
        {
            PatternInterface.Grid => this,
            PatternInterface.Table => this,
            PatternInterface.Scroll when _panEnabled => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    // ── IGridProvider ────────────────────────────────────────────────

    public int RowCount => _data.Series.Count;

    public int ColumnCount =>
        _data.Series.Count > 0
            ? _data.Series.Max(s => s.Points.Count)
            : 0;

    public IRawElementProviderSimple? GetItem(int row, int column)
    {
        if (row < 0 || row >= _data.Series.Count)
            return null;
        var series = _data.Series[row];
        if (column < 0 || column >= series.Points.Count)
            return null;

        var pointPeer = new ChartPointProvider(this, _data, row, column);
        return ProviderFromPeer(pointPeer);
    }

    // ── ITableProvider ───────────────────────────────────────────────

    public RowOrColumnMajor RowOrColumnMajor => RowOrColumnMajor.RowMajor;

    public IRawElementProviderSimple[] GetRowHeaders()
    {
        // Row headers = series names (one per series)
        return _data.Series.Select((s, i) =>
        {
            var peer = new ChartSeriesHeaderPeer(this, s.Name, i);
            return ProviderFromPeer(peer);
        }).Where(p => p != null).ToArray()!;
    }

    public IRawElementProviderSimple[] GetColumnHeaders()
    {
        // Column headers = x-axis tick labels from the first series' points
        if (_data.Series.Count == 0)
            return [];

        var maxPoints = _data.Series.Max(s => s.Points.Count);
        return Enumerable.Range(0, maxPoints).Select(i =>
        {
            // Use x-label from the first series that has this point
            var label = _data.Series
                .Where(s => i < s.Points.Count)
                .Select(s => s.Points[i].XLabel)
                .FirstOrDefault() ?? $"Point {i + 1}";

            var peer = new ChartColumnHeaderPeer(this, label, i);
            return ProviderFromPeer(peer);
        }).Where(p => p != null).ToArray()!;
    }

    // ── Internal helpers ─────────────────────────────────────────────

    internal IChartAccessibilityData Data => _data;

    internal void NotifyDataChanged()
    {
        RaiseAutomationEvent(AutomationEvents.StructureChanged);
    }

    // ── Scroll / viewport integration ────────────────────────────────

    /// <summary>
    /// Enables the IScrollProvider pattern on this peer. Called when the chart
    /// is interactive with pan/zoom support.
    /// </summary>
    internal void EnablePan()
    {
        _panEnabled = true;
    }

    /// <summary>
    /// Updates viewport scroll state. Called by the keyboard navigator or
    /// interaction handler when pan/zoom changes.
    /// </summary>
    internal void UpdateViewport(double hPercent, double vPercent, double hViewSize, double vViewSize)
    {
        double oldH = _horizontalScrollPercent;
        double oldV = _verticalScrollPercent;
        _horizontalScrollPercent = hPercent;
        _verticalScrollPercent = vPercent;
        _horizontalViewSize = Math.Clamp(hViewSize, 0, 100);
        _verticalViewSize = Math.Clamp(vViewSize, 0, 100);
        RaisePropertyChangedEvent(ScrollPatternIdentifiers.HorizontalScrollPercentProperty, oldH, hPercent);
        RaisePropertyChangedEvent(ScrollPatternIdentifiers.VerticalScrollPercentProperty, oldV, vPercent);
    }

    // ── IScrollProvider ──────────────────────────────────────────────

    public double HorizontalScrollPercent => _horizontalScrollPercent;
    public double VerticalScrollPercent => _verticalScrollPercent;
    public double HorizontalViewSize => _horizontalViewSize;
    public double VerticalViewSize => _verticalViewSize;
    public bool HorizontallyScrollable => _panEnabled;
    public bool VerticallyScrollable => _panEnabled;

    public void SetScrollPercent(double horizontalPercent, double verticalPercent)
    {
        // Charts handle panning via keyboard/mouse — AT-initiated scroll is not
        // supported. Use keyboard arrow keys with Alt modifier to pan.
        throw new InvalidOperationException(
            "Chart panning is not available via IScrollProvider. Use keyboard navigation (Alt+Arrow keys) to pan.");
    }

    public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
    {
        // Charts handle panning via keyboard/mouse — AT-initiated scroll is not
        // supported. Use keyboard arrow keys with Alt modifier to pan.
        throw new InvalidOperationException(
            "Chart panning is not available via IScrollProvider. Use keyboard navigation (Alt+Arrow keys) to pan.");
    }
}

/// <summary>
/// Lightweight peer used as a row header (series name) in the table pattern.
/// </summary>
internal sealed partial class ChartSeriesHeaderPeer : AutomationPeer
{
    private readonly ChartAutomationPeer _parent;
    private readonly string _name;
    private readonly int _index;

    internal ChartSeriesHeaderPeer(ChartAutomationPeer parent, string name, int index)
    {
        _parent = parent;
        _name = name;
        _index = index;
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.HeaderItem;

    protected override string GetNameCore() => _name;
    protected override string GetClassNameCore() => "ChartSeriesHeader";
    protected override AutomationPeer? GetPeerFromPointCore(global::Windows.Foundation.Point point) => null;
    protected override global::Windows.Foundation.Rect GetBoundingRectangleCore() => default;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override string GetAutomationIdCore() => $"ChartSeriesHeader_{_index}";
}

/// <summary>
/// Lightweight peer used as a column header (x-axis label) in the table pattern.
/// </summary>
internal sealed partial class ChartColumnHeaderPeer : AutomationPeer
{
    private readonly ChartAutomationPeer _parent;
    private readonly string _label;
    private readonly int _index;

    internal ChartColumnHeaderPeer(ChartAutomationPeer parent, string label, int index)
    {
        _parent = parent;
        _label = label;
        _index = index;
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.HeaderItem;

    protected override string GetNameCore() => _label;
    protected override string GetClassNameCore() => "ChartColumnHeader";
    protected override AutomationPeer? GetPeerFromPointCore(global::Windows.Foundation.Point point) => null;
    protected override global::Windows.Foundation.Rect GetBoundingRectangleCore() => default;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override string GetAutomationIdCore() => $"ChartColumnHeader_{_index}";
}
