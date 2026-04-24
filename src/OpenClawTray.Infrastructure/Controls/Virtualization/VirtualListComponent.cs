using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Component that renders a virtualized list using WinUI's ItemsRepeater.
/// Adapts the count-based VirtualListElement API to the ItemsRepeater infrastructure,
/// supporting both fixed-height (O(1) offset) and variable-height modes.
/// </summary>
public class VirtualListComponent : Component<VirtualListElement>
{
    public override Element Render()
    {
        var el = Props;

        // Build index list for ItemsRepeater's item source
        var indices = UseMemo(() =>
            Enumerable.Range(0, el.ItemCount).ToList() as IReadOnlyList<int>,
            el.ItemCount);

        // Key selector: use provided GetItemKey or default to index string
        var keySelector = el.GetItemKey ?? (i => i.ToString());

        // View builder: wrap the RenderItem callback, applying fixed height when set
        var fixedHeight = el.ItemHeight;
        Func<int, int, Element> viewBuilder = (index, _) =>
        {
            if (index < 0 || index >= el.ItemCount)
                return Empty();

            var item = el.RenderItem(index);

            // Fixed-height fast path: force each item to the exact height
            // so ItemsRepeater skips per-item measurement (O(1) offset)
            if (fixedHeight.HasValue)
                item = item.Height(fixedHeight.Value);

            return item;
        };

        // Configure the LazyVStack with appropriate settings
        var estimatedSize = el.ItemHeight ?? el.EstimatedItemHeight;
        var lazyStack = LazyVStack(indices, i => keySelector(i), viewBuilder) with
        {
            Spacing = el.Spacing,
            EstimatedItemSize = estimatedSize,
        };

        // Wire up Ref and OnVisibleRangeChanged via ScrollViewer setters.
        // These setters run at mount AND on every update, so we use a flag ref
        // to ensure event handlers are only attached once.
        var wiredRef = UseRef(false);
        var elRef = el.Ref;
        var elOnRange = el.OnVisibleRangeChanged;
        var elHeight = el.ItemHeight;
        var elEstHeight = el.EstimatedItemHeight;
        var elSpacing = el.Spacing;

        if (elRef is not null || elOnRange is not null)
        {
            lazyStack = lazyStack with
            {
                ScrollViewerSetters = [sv =>
                {
                    // Always update the Ref (new VirtualListRef pointing at same controls)
                    if (elRef is not null)
                    {
                        var repeater = sv.Content as ItemsRepeater;
                        elRef(new VirtualListRef(sv, repeater, elHeight));
                    }

                    // Only attach event handler once
                    if (!wiredRef.Current && elOnRange is not null)
                    {
                        wiredRef.Current = true;
                        sv.ViewChanged += (_, _) =>
                        {
                            var (first, last) = GetVisibleRange(sv, elHeight, elEstHeight, elSpacing);
                            elOnRange(first, last);
                        };
                    }
                }],
            };
        }

        return lazyStack;
    }

    /// <summary>
    /// Calculates the first and last visible item indices from the ScrollViewer viewport.
    /// </summary>
    private static (int First, int Last) GetVisibleRange(
        ScrollViewer sv, double? itemHeight, double estimatedHeight, double spacing)
    {
        var offset = sv.VerticalOffset;
        var viewportHeight = sv.ViewportHeight;
        var totalItemSize = (itemHeight ?? estimatedHeight) + spacing;
        if (totalItemSize <= 0) return (0, 0);

        var first = Math.Max(0, (int)(offset / totalItemSize));
        var last = (int)((offset + viewportHeight) / totalItemSize);
        return (first, last);
    }
}
