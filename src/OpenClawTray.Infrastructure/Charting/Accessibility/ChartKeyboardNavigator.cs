using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Manages virtual keyboard focus for interactive charts. The chart root is a single
/// focusable Canvas; the navigator holds <c>{seriesIndex, pointIndex}</c> state and
/// renders a double-ring focus overlay at the current point.
/// </summary>
internal static class ChartKeyboardNavigator
{
    internal record FocusState(int SeriesIndex, int PointIndex, bool HasFocus, int BrushStart = -1, int BrushEnd = -1, bool LegendFocused = false);

    /// <summary>
    /// Wraps <paramref name="chartElement"/> with keyboard navigation support.
    /// The chart's <see cref="IChartAccessibilityData"/> is used to determine
    /// series/point bounds.
    /// </summary>
    internal static Element Wrap(
        Element chartElement,
        IChartAccessibilityData chartData,
        double chartWidth,
        double chartHeight,
        bool disableKeyboard,
        ChartKeyboardOptions options)
    {
        if (disableKeyboard)
            return chartElement;

        return new FuncElement(ctx =>
        {
            var (focusState, setFocusState) = ctx.UseState(new FocusState(0, 0, false));

            var series = chartData.Series;
            int seriesCount = series.Count;
            if (seriesCount == 0)
                return chartElement;

            int maxPoints = 0;
            for (int i = 0; i < seriesCount; i++)
            {
                if (series[i].Points.Count > maxPoints)
                    maxPoints = series[i].Points.Count;
            }

            if (maxPoints == 0)
                return chartElement;

            // Clamp focus to valid bounds
            int si = Math.Clamp(focusState.SeriesIndex, 0, seriesCount - 1);
            int pi = Math.Clamp(focusState.PointIndex, 0, series[si].Points.Count - 1);

            void HandleKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
            {
                var key = e.Key;
                bool ctrl = IsModifierPressed(global::Windows.System.VirtualKey.Control);
                bool shift = IsModifierPressed(global::Windows.System.VirtualKey.Shift);
                bool alt = IsModifierPressed(global::Windows.System.VirtualKey.Menu);
                int newSi = si, newPi = pi;
                bool handled = true;
                bool activate = false;

                // Alt+arrows → pan
                if (alt && !ctrl && !shift)
                {
                    switch (key)
                    {
                        case global::Windows.System.VirtualKey.Left:
                            options.OnPan?.Invoke(-1, 0);
                            break;
                        case global::Windows.System.VirtualKey.Right:
                            options.OnPan?.Invoke(1, 0);
                            break;
                        case global::Windows.System.VirtualKey.Up:
                            options.OnPan?.Invoke(0, -1);
                            break;
                        case global::Windows.System.VirtualKey.Down:
                            options.OnPan?.Invoke(0, 1);
                            break;
                        default:
                            handled = false;
                            break;
                    }
                    if (handled) { e.Handled = true; return; }
                }

                // Shift+arrows → brush selection
                if (shift && !ctrl && !alt)
                {
                    switch (key)
                    {
                        case global::Windows.System.VirtualKey.Left:
                        {
                            int brushStart = focusState.BrushStart >= 0 ? focusState.BrushStart : pi;
                            int brushEnd = Math.Max(0, (focusState.BrushEnd >= 0 ? focusState.BrushEnd : pi) - 1);
                            setFocusState(focusState with { PointIndex = brushEnd, HasFocus = true, BrushStart = brushStart, BrushEnd = brushEnd });
                            options.OnBrushChanged?.Invoke(Math.Min(brushStart, brushEnd), Math.Max(brushStart, brushEnd));
                            e.Handled = true;
                            return;
                        }
                        case global::Windows.System.VirtualKey.Right:
                        {
                            int brushStart = focusState.BrushStart >= 0 ? focusState.BrushStart : pi;
                            int brushEnd = Math.Min(series[si].Points.Count - 1, (focusState.BrushEnd >= 0 ? focusState.BrushEnd : pi) + 1);
                            setFocusState(focusState with { PointIndex = brushEnd, HasFocus = true, BrushStart = brushStart, BrushEnd = brushEnd });
                            options.OnBrushChanged?.Invoke(Math.Min(brushStart, brushEnd), Math.Max(brushStart, brushEnd));
                            e.Handled = true;
                            return;
                        }
                        // Shift+? → help dialog
                        case (global::Windows.System.VirtualKey)191 when IsShiftOemSlash(key):
                            options.OnShowHelp?.Invoke();
                            e.Handled = true;
                            return;
                    }
                }

                switch (key)
                {
                    // ← / → : previous / next point in current series
                    case global::Windows.System.VirtualKey.Left:
                        newPi = Math.Max(0, pi - 1);
                        activate = true;
                        break;
                    case global::Windows.System.VirtualKey.Right:
                        newPi = Math.Min(series[si].Points.Count - 1, pi + 1);
                        activate = true;
                        break;

                    // ↑ / ↓ : switch to adjacent series
                    case global::Windows.System.VirtualKey.Up:
                        newSi = Math.Max(0, si - 1);
                        newPi = Math.Min(pi, series[newSi].Points.Count - 1);
                        activate = true;
                        break;
                    case global::Windows.System.VirtualKey.Down:
                        newSi = Math.Min(seriesCount - 1, si + 1);
                        newPi = Math.Min(pi, series[newSi].Points.Count - 1);
                        activate = true;
                        break;

                    // Home / End
                    case global::Windows.System.VirtualKey.Home:
                        if (ctrl) { newSi = 0; newPi = 0; }
                        else newPi = 0;
                        activate = true;
                        break;
                    case global::Windows.System.VirtualKey.End:
                        if (ctrl)
                        {
                            newSi = seriesCount - 1;
                            newPi = series[newSi].Points.Count - 1;
                        }
                        else
                        {
                            newPi = series[si].Points.Count - 1;
                        }
                        activate = true;
                        break;

                    // Enter / Space : invoke (or toggle series if legend focused)
                    case global::Windows.System.VirtualKey.Enter:
                    case global::Windows.System.VirtualKey.Space:
                        if (focusState.LegendFocused)
                            options.OnSeriesToggle?.Invoke(si);
                        else
                            options.OnPointInvoke?.Invoke(si, pi);
                        break;

                    // Esc : deactivate focus indicator / leave chart
                    case global::Windows.System.VirtualKey.Escape:
                        if (focusState.LegendFocused)
                            setFocusState(focusState with { LegendFocused = false });
                        else
                            setFocusState(new FocusState(si, pi, false));
                        break;

                    // + / = : zoom in
                    case global::Windows.System.VirtualKey.Add:
                    case (global::Windows.System.VirtualKey)187 when ctrl: // Ctrl+=
                        options.OnZoom?.Invoke(1.0);
                        break;

                    // - : zoom out
                    case global::Windows.System.VirtualKey.Subtract:
                    case (global::Windows.System.VirtualKey)189 when ctrl: // Ctrl+-
                        options.OnZoom?.Invoke(-1.0);
                        break;

                    // Ctrl+0 : reset zoom
                    case (global::Windows.System.VirtualKey)48 when ctrl:
                        options.OnZoomReset?.Invoke();
                        break;

                    // L : focus legend
                    case global::Windows.System.VirtualKey.L when !ctrl && !alt:
                        if (options.HasLegend)
                        {
                            setFocusState(focusState with { LegendFocused = true, HasFocus = true });
                            options.OnFocusLegend?.Invoke();
                        }
                        break;

                    // T : toggle alternate view
                    case global::Windows.System.VirtualKey.T when !ctrl && !alt:
                        // Handled by ChartAlternateViewWrapper — let it bubble
                        handled = false;
                        break;

                    // S : speak summary / replay announcement
                    case global::Windows.System.VirtualKey.S when !ctrl && !alt:
                        options.OnRequestSummary?.Invoke();
                        break;

                    // F1 : keyboard help
                    case global::Windows.System.VirtualKey.F1:
                        options.OnShowHelp?.Invoke();
                        break;

                    default:
                        handled = false;
                        break;
                }

                if (handled)
                {
                    bool hasFocus = activate || focusState.HasFocus;
                    if (newSi != si || newPi != pi || hasFocus != focusState.HasFocus)
                    {
                        // Clear brush selection on non-shift navigation
                        setFocusState(new FocusState(newSi, newPi, hasFocus));
                    }
                    e.Handled = true;
                }
            }

            // Build focus indicator overlay when active
            Element? focusOverlay = null;
            if (focusState.HasFocus && !focusState.LegendFocused && si < seriesCount && pi < series[si].Points.Count)
            {
                focusOverlay = BuildFocusIndicator(
                    chartData, si, pi, chartWidth, chartHeight, seriesCount, maxPoints);
            }

            // Wrap chart in a focusable Grid with the keyboard handler
            var wrappedChart = chartElement
                .IsTabStop(true)
                .OnKeyDown(HandleKeyDown);

            if (focusOverlay is null)
                return wrappedChart;

            // Overlay the focus ring using a layered Grid
            return Factories.Grid(
                ["*"], ["*"],
                wrappedChart,
                focusOverlay.Opacity(1.0)
            );
        });
    }

    /// <summary>
    /// Builds a double-ring focus indicator overlay positioned at the given point.
    /// Inner ring: 1px dark stroke. Outer ring: 1px light stroke.
    /// Guarantees 3:1 contrast against any chart background (WCAG 2.4.13).
    /// </summary>
    private static Element BuildFocusIndicator(
        IChartAccessibilityData chartData,
        int seriesIndex, int pointIndex,
        double chartWidth, double chartHeight,
        int seriesCount, int maxPoints)
    {
        // Derive plot area from axes if available, otherwise fall back to
        // proportional estimation based on chart dimensions.
        var xAxis = chartData.Axes.FirstOrDefault(a => a.AxisType == ChartAxisType.X);
        var yAxis = chartData.Axes.FirstOrDefault(a => a.AxisType == ChartAxisType.Y);

        // Estimate plot margins proportionally to chart size.
        // Y-axis labels are typically ~10% of width; X-axis labels ~12% of height.
        double plotLeft = chartWidth * 0.10;
        double plotTop = chartHeight * 0.06;
        double plotWidth = chartWidth * 0.85;
        double plotHeight = chartHeight * 0.80;

        double x, y;

        // Use actual data values + axis range when available for precise positioning
        var series = chartData.Series;
        var point = (seriesIndex < series.Count && pointIndex < series[seriesIndex].Points.Count)
            ? series[seriesIndex].Points[pointIndex]
            : null;

        if (point is not null && xAxis is not null && (xAxis.Max - xAxis.Min) > 1e-10)
        {
            // For categorical x axes, use index-based positioning
            x = maxPoints > 1
                ? plotLeft + (double)pointIndex / (maxPoints - 1) * plotWidth
                : plotLeft + plotWidth / 2;
        }
        else
        {
            x = maxPoints > 1
                ? plotLeft + (double)pointIndex / (maxPoints - 1) * plotWidth
                : plotLeft + plotWidth / 2;
        }

        if (point is not null && yAxis is not null && (yAxis.Max - yAxis.Min) > 1e-10)
        {
            // Map y value within the axis range (inverted: high values at top)
            double yFraction = (point.YValue - yAxis.Min) / (yAxis.Max - yAxis.Min);
            y = plotTop + (1 - yFraction) * plotHeight;
        }
        else
        {
            y = seriesCount > 1
                ? plotTop + (double)seriesIndex / (seriesCount - 1) * plotHeight
                : plotTop + plotHeight / 2;
        }

        const double outerRadius = 14;
        const double innerRadius = 12;

        // Double-ring for 3:1 contrast on any background
        var fc = D3Dsl.ForcedColors ?? ForcedColorsTheme.Default;
        var darkBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            D3Dsl.IsForcedColors
                ? fc.Foreground
                : global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
        var lightBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            D3Dsl.IsForcedColors
                ? fc.Highlight
                : global::Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // Use D3Circle for the rings (no fill, stroke only)
        var outerRing = D3Dsl.D3Circle(x, y, outerRadius) with
        {
            Stroke = lightBrush,
            StrokeThickness = 1,
        };
        var innerRing = D3Dsl.D3Circle(x, y, innerRadius) with
        {
            Stroke = darkBrush,
            StrokeThickness = 1,
        };

        return D3Dsl.D3Canvas(chartWidth, chartHeight, outerRing, innerRing)
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    private static bool IsModifierPressed(global::Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    /// <summary>
    /// Detects Shift+/ which produces the ? character on US keyboard layouts.
    /// VirtualKey.Number191 is the /? key on US layouts.
    /// </summary>
    private static bool IsShiftOemSlash(global::Windows.System.VirtualKey key)
        => key == (global::Windows.System.VirtualKey)191;
}

/// <summary>
/// Options for keyboard navigation behavior.
/// </summary>
internal record ChartKeyboardOptions
{
    /// <summary>
    /// Called when Enter/Space is pressed on a focused point.
    /// Parameters: seriesIndex, pointIndex.
    /// </summary>
    public Action<int, int>? OnPointInvoke { get; init; }

    /// <summary>Called when brush selection changes. Parameters: start pointIndex, end pointIndex.</summary>
    public Action<int, int>? OnBrushChanged { get; init; }

    /// <summary>Called to toggle a series on/off (legend space key). Parameter: seriesIndex.</summary>
    public Action<int>? OnSeriesToggle { get; init; }

    /// <summary>The alternate view element, if set. Enables T key toggle.</summary>
    public Element? AlternateView { get; init; }

    /// <summary>Called to announce the current summary (S key).</summary>
    public Action? OnRequestSummary { get; init; }

    /// <summary>Whether zoom is enabled (enables +/- and Ctrl+= keys).</summary>
    public bool ZoomEnabled { get; init; }

    /// <summary>Called when zoom changes. Parameter: delta (positive = zoom in, negative = zoom out).</summary>
    public Action<double>? OnZoom { get; init; }

    /// <summary>Whether pan is enabled (enables Alt+arrow keys).</summary>
    public bool PanEnabled { get; init; }

    /// <summary>Called when pan changes. Parameters: deltaX, deltaY.</summary>
    public Action<double, double>? OnPan { get; init; }

    /// <summary>Called to reset zoom to default view (Ctrl+0).</summary>
    public Action? OnZoomReset { get; init; }

    /// <summary>Legend element to focus when L is pressed.</summary>
    public bool HasLegend { get; init; }

    /// <summary>Called when L key is pressed to move focus to the legend.</summary>
    public Action? OnFocusLegend { get; init; }

    /// <summary>Called when Shift+? or F1 is pressed to show keyboard help.</summary>
    public Action? OnShowHelp { get; init; }
}
