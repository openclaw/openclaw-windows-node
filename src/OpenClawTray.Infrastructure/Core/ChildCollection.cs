using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Abstraction over a collection of UIElement children.
/// Allows the reconciler to work with any container type (Panel, ItemsControl, etc.).
/// </summary>
internal interface IChildCollection
{
    int Count { get; }
    UIElement Get(int index);
    void Insert(int index, UIElement element);
    void RemoveAt(int index);
    void Move(int oldIndex, int newIndex);
    void Replace(int index, UIElement element);
}

/// <summary>
/// Wraps Panel.Children (StackPanel, Grid, Canvas, etc.).
/// </summary>
internal sealed class PanelChildCollection : IChildCollection
{
    private readonly UIElementCollection _children;

    public PanelChildCollection(WinUI.Panel panel)
    {
        _children = panel.Children;
    }

    public int Count => _children.Count;
    public UIElement Get(int index) => _children[index];
    public void Insert(int index, UIElement element) => _children.Insert(index, element);
    public void RemoveAt(int index) => _children.RemoveAt(index);

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex) return;
        Debug.Assert(oldIndex >= 0 && oldIndex < _children.Count, $"oldIndex {oldIndex} out of range [0, {_children.Count})");
        Debug.Assert(newIndex >= 0 && newIndex < _children.Count, $"newIndex {newIndex} out of range [0, {_children.Count})");
        var item = _children[oldIndex];
        _children.RemoveAt(oldIndex);
        // newIndex is the final desired position — no adjustment needed.
        // After RemoveAt, inserting at newIndex places the item at that index
        // in the resulting collection.
        _children.Insert(newIndex, item);
    }

    public void Replace(int index, UIElement element)
    {
        // Use explicit RemoveAt+Insert instead of indexer assignment.
        // WinUI's _children[i] = x doesn't always fully disconnect the old
        // element's internal parent state, causing COMException when the old
        // element is later reused from the pool.
        _children.RemoveAt(index);
        _children.Insert(index, element);
    }
}

/// <summary>
/// Wraps ItemsControl.Items (ListView, GridView, FlipView, etc.).
/// Items in these controls are objects (not necessarily UIElement), but we store UIElements.
/// </summary>
internal sealed class ItemsControlChildCollection : IChildCollection
{
    private readonly ItemCollection _items;

    public ItemsControlChildCollection(WinUI.ItemsControl itemsControl)
    {
        _items = itemsControl.Items;
    }

    public int Count => _items.Count;
    public UIElement Get(int index) => _items[index] as UIElement
        ?? throw new InvalidOperationException(
            $"ItemsControl item at index {index} is {_items[index]?.GetType().Name ?? "null"}, expected UIElement.");
    public void Insert(int index, UIElement element) => _items.Insert(index, element);
    public void RemoveAt(int index) => _items.RemoveAt(index);

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex) return;
        Debug.Assert(oldIndex >= 0 && oldIndex < _items.Count, $"oldIndex {oldIndex} out of range [0, {_items.Count})");
        Debug.Assert(newIndex >= 0 && newIndex < _items.Count, $"newIndex {newIndex} out of range [0, {_items.Count})");
        var item = _items[oldIndex];
        _items.RemoveAt(oldIndex);
        _items.Insert(newIndex, item);
    }

    public void Replace(int index, UIElement element)
    {
        // Use RemoveAt+Insert to match PanelChildCollection.Replace.
        // Direct indexer assignment doesn't fully disconnect the old element's
        // internal parent state, causing COMException on later reuse.
        _items.RemoveAt(index);
        _items.Insert(index, element);
    }
}
