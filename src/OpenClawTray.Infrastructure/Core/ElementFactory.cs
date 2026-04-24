using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Bridges WinUI's ItemsRepeater/IElementFactory to Reactor's Reconciler.
/// GetElement calls the view builder then mounts; RecycleElement unmounts.
/// </summary>
public sealed partial class ElementFactory<T> : IElementFactory
{
    private IReadOnlyList<T> _items;
    private Func<T, int, Element> _viewBuilder;
    private readonly Reconciler _reconciler;
    private readonly Action _requestRerender;
    private readonly ElementPool? _pool;

    // Track the full element (including ModifiedElement wrappers) per realized index
    // so RefreshRealizedItems can reconcile correctly.
    private readonly Dictionary<int, Element> _mountedElements = new();

    public ElementFactory(
        IReadOnlyList<T> items,
        Func<T, int, Element> viewBuilder,
        Reconciler reconciler,
        Action requestRerender,
        ElementPool? pool = null)
    {
        _items = items;
        _viewBuilder = viewBuilder;
        _reconciler = reconciler;
        _requestRerender = requestRerender;
        _pool = pool;
    }

    /// <summary>
    /// Update items and viewBuilder in place without replacing the factory.
    /// This avoids ItemsRepeater re-realizing all items (which causes
    /// "Cannot run layout in the middle of a collection change" crashes).
    /// Existing realized items stay mounted; they'll render new content
    /// on the next GetElement call (scroll or explicit refresh).
    /// </summary>
    internal void UpdateInPlace(IReadOnlyList<T> items, Func<T, int, Element> viewBuilder)
    {
        _items = items;
        _viewBuilder = viewBuilder;
    }

    /// <summary>
    /// After updating the factory in place, reconcile all currently realized
    /// items with the new viewBuilder output. This updates existing WinUI
    /// controls via property changes (no add/remove on the ItemsRepeater's
    /// Children collection).
    /// </summary>
    /// <summary>
    /// When set, RefreshRealizedItems is skipped if the predicate returns true.
    /// Used by DataGrid to suppress reconciliation during active scrolling.
    /// </summary>
    internal Func<bool>? ShouldSkipRefresh;

    internal void RefreshRealizedItems(Microsoft.UI.Xaml.Controls.ItemsRepeater repeater)
    {
        // If scrolling restarted after the render was dispatched, skip reconciliation.
        // The next settle timer will pick it up when scrolling truly stops.
        if (ShouldSkipRefresh?.Invoke() == true)
            return;

        // Only iterate tracked indices (realized items), not all items.
        // Use a snapshot to avoid modifying the dictionary during iteration.
        var indices = _mountedElements.Keys.ToArray();
        foreach (var i in indices)
        {
            var child = repeater.TryGetElement(i);
            if (child is null) { _mountedElements.Remove(i); continue; }

            if (!_mountedElements.TryGetValue(i, out var oldElement)) continue;
            if (i < 0 || i >= _items.Count) continue;

            var newElement = _viewBuilder(_items[i], i);
            _mountedElements[i] = newElement;

            _reconciler.Reconcile(oldElement, newElement, child, _requestRerender);
        }
    }

    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        var index = args.Data is int i ? i : 0;
        if (index < 0 || index >= _items.Count)
            return new TextBlock { Text = "" };

        var item = _items[index];
        var element = _viewBuilder(item, index);
        _mountedElements[index] = element;
        var control = _reconciler.Mount(element, _requestRerender);
        return control ?? new TextBlock { Text = "" };
    }

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is null) return;

        // Clean up Reactor state (component contexts, effects).
        _reconciler.UnmountChild(args.Element);

        // Pool interactive leaf controls for reuse. Layout containers (Panel, Border)
        // are NOT pooled here because ItemsRepeater may still reference the root element
        // during its layout pass and modifying children causes COMExceptions. Interactive
        // controls are safe to detach and pool because they are leaves with no children.
        if (_pool is not null)
            PoolInteractiveLeaves(args.Element);
    }

    /// <summary>
    /// Walk the recycled subtree and pool interactive leaf controls (Button, TextBox,
    /// ToggleSwitch). These are the most expensive controls to create and benefit most
    /// from pooling. Detaches each from its parent panel before returning to the pool.
    /// </summary>
    private void PoolInteractiveLeaves(UIElement root)
    {
        if (root is Microsoft.UI.Xaml.Controls.Panel panel)
        {
            // Walk children in reverse so removal doesn't shift indices
            for (int i = panel.Children.Count - 1; i >= 0; i--)
                PoolInteractiveLeaves(panel.Children[i]);
        }
        else if (root is Microsoft.UI.Xaml.Controls.Border border && border.Child is not null)
        {
            PoolInteractiveLeaves(border.Child);
        }
        else if (root is FrameworkElement fe && IsPoolableInteractive(fe))
        {
            _pool!.Return(fe);
        }
    }

    private static bool IsPoolableInteractive(FrameworkElement fe) =>
        fe is Microsoft.UI.Xaml.Controls.Button
        or TextBox
        or Microsoft.UI.Xaml.Controls.ToggleSwitch;
}
