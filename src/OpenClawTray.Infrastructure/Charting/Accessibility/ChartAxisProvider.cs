using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Virtual UIA peer for a chart axis. Exposes <see cref="IRangeValueProvider"/>
/// so assistive technology can read the axis range and step values.
/// </summary>
internal sealed partial class ChartAxisProvider : AutomationPeer, IRangeValueProvider
{
    private readonly ChartAutomationPeer _chartPeer;
    private readonly ChartAxisDescriptor _axis;

    internal ChartAxisProvider(ChartAutomationPeer chartPeer, ChartAxisDescriptor axis)
    {
        _chartPeer = chartPeer;
        _axis = axis;
    }

    // ── AutomationPeer overrides ─────────────────────────────────────

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Custom;

    protected override string GetClassNameCore() => "ChartAxis";

    protected override string GetNameCore()
    {
        var prefix = _axis.AxisType == ChartAxisType.X ? "X axis" : "Y axis";
        if (!string.IsNullOrWhiteSpace(_axis.Label))
            return $"{prefix}: {_axis.Label}";
        return prefix;
    }

    protected override string GetAutomationIdCore()
        => $"ChartAxis_{_axis.AxisType}";

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface switch
        {
            PatternInterface.RangeValue => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    protected override AutomationPeer? GetPeerFromPointCore(global::Windows.Foundation.Point point) => null;
    protected override global::Windows.Foundation.Rect GetBoundingRectangleCore() => default;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    // ── IRangeValueProvider ──────────────────────────────────────────

    public double Value => (_axis.Min + _axis.Max) / 2;
    public double Minimum => _axis.Min;
    public double Maximum => _axis.Max;
    public bool IsReadOnly => true;

    public double SmallChange
    {
        get
        {
            var range = _axis.Max - _axis.Min;
            return range > 0 ? range / 20 : 1;
        }
    }

    public double LargeChange
    {
        get
        {
            var range = _axis.Max - _axis.Min;
            return range > 0 ? range / 4 : 5;
        }
    }

    public void SetValue(double value)
    {
        // Axes are read-only in the current implementation
        throw new InvalidOperationException("Chart axis values are read-only.");
    }
}
