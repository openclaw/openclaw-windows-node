using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml.Automation.Peers;

namespace OpenClawTray.Infrastructure.Charting.Accessibility;

/// <summary>
/// Wraps a chart element with an alternate-view toggle. When enabled, pressing T or
/// Alt+Shift+F11 toggles between the chart and the alternate view (typically a data table).
/// The currently-hidden view is set to <c>AccessibilityView.Raw</c> so screen readers
/// only see the active presentation. Focus position is saved/restored across toggles.
/// </summary>
internal static class ChartAlternateViewWrapper
{
    /// <summary>
    /// Wraps <paramref name="chartElement"/> with toggle support for <paramref name="alternateView"/>.
    /// </summary>
    internal static Element Wrap(Element chartElement, Element alternateView, ChartFocusContext? focusContext = null)
    {
        return new FuncElement(ctx =>
        {
            var (isShowingAlternate, setIsShowingAlternate) = ctx.UseState(false);

            void HandleKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
            {
                // T key toggles (when not a modifier key combination other than Alt+Shift+F11)
                bool isToggle = e.Key == global::Windows.System.VirtualKey.T &&
                                !IsModifierPressed(global::Windows.System.VirtualKey.Control) &&
                                !IsModifierPressed(global::Windows.System.VirtualKey.Menu);

                // Alt+Shift+F11 is the universal accessibility toggle
                bool isAltShiftF11 = e.Key == global::Windows.System.VirtualKey.F11 &&
                                     IsModifierPressed(global::Windows.System.VirtualKey.Menu) &&
                                     IsModifierPressed(global::Windows.System.VirtualKey.Shift);

                if (isToggle || isAltShiftF11)
                {
                    if (!isShowingAlternate && focusContext is not null)
                    {
                        // Save focus position before switching to alternate view
                        var pos = focusContext.HasSavedPosition
                            ? (focusContext.SavedSeriesIndex, focusContext.SavedPointIndex)
                            : (0, 0);
                        focusContext.SavePosition(pos.Item1, pos.Item2);
                    }

                    setIsShowingAlternate(!isShowingAlternate);
                    e.Handled = true;
                }
            }

            Element chart = isShowingAlternate
                ? chartElement.AccessibilityView(AccessibilityView.Raw)
                : chartElement;

            Element alternate = isShowingAlternate
                ? alternateView
                : alternateView.AccessibilityView(AccessibilityView.Raw);

            // Live region announcement of current state
            string announcement = isShowingAlternate
                ? "Showing data table"
                : "Showing chart";

            return Factories.VStack(
                chart.Visible(!isShowingAlternate),
                alternate.Visible(isShowingAlternate),
                // Visually hidden text block for live-region announcement.
                // Uses 1×1 size at far offscreen position instead of zero-size,
                // because some screen readers skip zero-dimension elements.
                Factories.TextBlock(announcement)
                    .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite)
                    .AccessibilityView(AccessibilityView.Content)
                    .Width(1).Height(1)
                    .Margin(-10000, 0, 0, 0)
            ).IsTabStop(true).OnKeyDown(HandleKeyDown);
        });
    }

    private static bool IsModifierPressed(global::Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }
}
