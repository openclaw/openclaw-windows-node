using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Element record for the VirtualList component — a count-based virtualized list
/// backed by WinUI's ItemsRepeater. Unlike LazyVStack which takes a concrete
/// IReadOnlyList&lt;T&gt;, VirtualList works with an item count and a render callback,
/// making it suitable for data-source-driven scenarios where items are fetched on demand.
/// </summary>
public record VirtualListElement : Element
{
    /// <summary>Total number of items (drives the virtualizer's extent).</summary>
    public required int ItemCount { get; init; }

    /// <summary>
    /// Render callback: given an index, return the Element for that row.
    /// Called only for visible items (virtualized).
    /// </summary>
    public required Func<int, Element> RenderItem { get; init; }

    /// <summary>
    /// Optional key function for stable identity across re-renders.
    /// When provided, the reconciler uses this to match items across reorderings.
    /// </summary>
    public Func<int, string>? GetItemKey { get; init; }

    /// <summary>
    /// Fixed item height in pixels. When set, enables the fixed-height fast path
    /// with O(1) offset calculation — no per-item measurement needed.
    /// When null, variable-height mode is used with EstimatedItemHeight.
    /// </summary>
    public double? ItemHeight { get; init; }

    /// <summary>
    /// Estimated item height for variable-height mode (default 40px).
    /// Used for initial scroll extent calculation before items are measured.
    /// Ignored when ItemHeight is set.
    /// </summary>
    public double EstimatedItemHeight { get; init; } = 40;

    /// <summary>Spacing between items in pixels (default 0).</summary>
    public double Spacing { get; init; }

    /// <summary>
    /// Callback ref for scroll operations. When set, receives a VirtualListRef
    /// that exposes ScrollToIndex and scroll position save/restore.
    /// </summary>
    public Action<VirtualListRef>? Ref { get; init; }

    /// <summary>
    /// Callback fired when the visible range changes (viewport tracking).
    /// Receives the first and last visible item indices.
    /// </summary>
    public Action<int, int>? OnVisibleRangeChanged { get; init; }
}

/// <summary>
/// Imperative handle for VirtualList scroll operations.
/// Obtained via the VirtualListElement.Ref callback.
/// </summary>
public sealed class VirtualListRef
{
    private readonly ScrollViewer? _scrollViewer;
    private readonly ItemsRepeater? _repeater;
    private readonly double? _itemHeight;

    internal VirtualListRef(ScrollViewer? scrollViewer, ItemsRepeater? repeater, double? itemHeight)
    {
        _scrollViewer = scrollViewer;
        _repeater = repeater;
        _itemHeight = itemHeight;
    }

    /// <summary>The underlying ItemsRepeater, for advanced scenarios.</summary>
    public ItemsRepeater? Repeater => _repeater;

    /// <summary>
    /// Programmatically scroll to bring the item at the given index into view.
    /// </summary>
    public void ScrollToIndex(int index)
    {
        if (_scrollViewer is null || _repeater is null) return;

        if (_itemHeight.HasValue)
        {
            // Fixed-height fast path: O(1) offset calculation
            var offset = index * (_itemHeight.Value + GetSpacing());
            _scrollViewer.ChangeView(null, offset, null, disableAnimation: false);
        }
        else
        {
            // Variable-height: use ItemsRepeater to get element and bring into view
            var element = _repeater.TryGetElement(index);
            if (element is not null)
            {
                element.StartBringIntoView();
            }
            else
            {
                // Element not realized yet — estimate and scroll, then it'll realize
                var estimated = index * 40; // EstimatedItemHeight default
                _scrollViewer.ChangeView(null, estimated, null, disableAnimation: false);
            }
        }
    }

    /// <summary>Gets the current vertical scroll offset.</summary>
    public double ScrollOffset => _scrollViewer?.VerticalOffset ?? 0;

    /// <summary>Restores a previously saved scroll offset.</summary>
    public void RestoreScrollOffset(double offset)
    {
        _scrollViewer?.ChangeView(null, offset, null, disableAnimation: true);
    }

    private double GetSpacing()
    {
        if (_repeater?.Layout is StackLayout stack) return stack.Spacing;
        return 0;
    }
}
