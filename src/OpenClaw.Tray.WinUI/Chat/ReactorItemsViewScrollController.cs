using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using WinUIAnnotatedScrollBar = Microsoft.UI.Xaml.Controls.AnnotatedScrollBar;
using WinUIItemsView = Microsoft.UI.Xaml.Controls.ItemsView;
using WinUIScrollView = Microsoft.UI.Xaml.Controls.ScrollView;

namespace OpenClawTray.Chat;

file sealed record ItemsViewVerticalScrollControllerElement(
    Element Child,
    ElementRef<WinUIAnnotatedScrollBar> ScrollBarRef,
    int InitialTailIndex,
    string InitialTailRequestKey) : Element
{
    static ItemsViewVerticalScrollControllerElement() =>
        ControlRegistry.RegisterDecorator<ItemsViewVerticalScrollControllerElement>(
            static () => new ItemsViewVerticalScrollControllerHandler());
}

file sealed class ItemsViewVerticalScrollControllerHandler
    : IDecoratorElementHandler<ItemsViewVerticalScrollControllerElement>
{
    private static readonly ConditionalWeakTable<WinUIItemsView, InitialTailPositioner> Positioners = new();

    public UIElement Mount(MountContext context, ItemsViewVerticalScrollControllerElement element)
    {
        var control = context.MountChild(element.Child);
        if (control is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");

        context.BindFor(itemsView, element).Reference(
            get: static value => value.ScrollBarRef,
            set: static (value, scrollBar) =>
                ((WinUIItemsView)value).VerticalScrollController = scrollBar?.ScrollController);
        var positioner = new InitialTailPositioner(itemsView);
        Positioners.Add(itemsView, positioner);
        positioner.Request(element.InitialTailIndex, element.InitialTailRequestKey);
        return itemsView;
    }

    public UIElement Update(
        UpdateContext context,
        ItemsViewVerticalScrollControllerElement oldElement,
        ItemsViewVerticalScrollControllerElement newElement,
        UIElement control)
    {
        var updated = context.ReconcileChild(oldElement.Child, newElement.Child, control);
        if (updated is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");
        if (!string.Equals(oldElement.InitialTailRequestKey, newElement.InitialTailRequestKey, StringComparison.Ordinal)
            && Positioners.TryGetValue(itemsView, out var positioner))
            positioner.Request(newElement.InitialTailIndex, newElement.InitialTailRequestKey);
        else if (Positioners.TryGetValue(itemsView, out var existingPositioner))
            existingPositioner.UpdateTailIndex(newElement.InitialTailIndex);
        return itemsView;
    }

    public V1UnmountDisposition Unmount(UnmountContext context, ItemsViewVerticalScrollControllerElement? element, UIElement control)
    {
        if (control is WinUIItemsView itemsView && Positioners.TryGetValue(itemsView, out var positioner))
        {
            Positioners.Remove(itemsView);
            positioner.Dispose();
        }
        return V1UnmountDisposition.ContinueDefaultTraversal;
    }
}

file sealed class InitialTailPositioner : IDisposable
{
    private readonly WinUIItemsView itemsView;
    private string? _requestKey;
    private int _tailIndex;
    private int _version;
    private bool _valid;
    private bool _awaitingLayout;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private bool _following;
    private bool _disposed;

    public InitialTailPositioner(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        itemsView.Loaded += OnLoaded;
        itemsView.Unloaded += OnUnloaded;
    }

    public void Request(int tailIndex, string requestKey)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
            return;
        _requestKey = requestKey;
        _version++;
        DetachLayout();
        _valid = tailIndex >= 0;
        if (!_valid) return;
        _tailIndex = tailIndex;
        SetFollowing(true);
        if (itemsView.IsLoaded) AwaitLayout();
    }

    public void UpdateTailIndex(int tailIndex)
    {
        _tailIndex = tailIndex;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_valid) AwaitLayout();
    }

    private void AwaitLayout()
    {
        if (_disposed || !_valid || !itemsView.IsLoaded || _awaitingLayout) return;
        if (itemsView.ScrollView is { IsLoaded: false } scrollView)
        {
            _awaitingScrollView = scrollView;
            scrollView.Loaded += OnScrollViewLoaded;
            return;
        }
        _awaitingLayout = true;
        itemsView.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnScrollViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is WinUIScrollView scrollView) scrollView.Loaded -= OnScrollViewLoaded;
        _awaitingScrollView = null;
        AwaitLayout();
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout();
            return;
        }
        var version = _version;
        var index = _tailIndex;
        itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version
                || itemsView.ScrollView is not { IsLoaded: true })
            {
                if (!_disposed && _valid) AwaitLayout();
                return;
            }
            itemsView.StartBringItemIntoView(index, new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 1.0,
            });
            AttachScrollView();
            ApplyFollowAnchor();
        });
    }

    private void AttachScrollView()
    {
        if (ReferenceEquals(_scrollView, itemsView.ScrollView))
            return;

        if (_scrollView is not null)
            _scrollView.ViewChanged -= OnViewChanged;
        _scrollView = itemsView.ScrollView;
        if (_scrollView is not null)
            _scrollView.ViewChanged += OnViewChanged;
    }

    private void OnViewChanged(WinUIScrollView sender, object args)
    {
        if (_tailIndex < 0
            || !itemsView.TryGetItemIndex(0.5, 1.0, out var bottomIndex))
        {
            return;
        }

        SetFollowing(bottomIndex >= _tailIndex);
    }

    private void SetFollowing(bool following)
    {
        if (_following == following)
            return;

        _following = following;
        ApplyFollowAnchor();
    }

    private void ApplyFollowAnchor()
    {
        if (itemsView.ScrollView is { IsLoaded: true } scrollView)
            scrollView.VerticalAnchorRatio = _following ? 1.0 : double.NaN;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _version++;
        DetachLayout();
    }

    private void DetachLayout()
    {
        if (_awaitingScrollView is { } scrollView)
        {
            scrollView.Loaded -= OnScrollViewLoaded;
            _awaitingScrollView = null;
        }
        if (_awaitingLayout)
        {
            itemsView.LayoutUpdated -= OnLayoutUpdated;
            _awaitingLayout = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _version++;
        DetachLayout();
        if (_scrollView is not null)
            _scrollView.ViewChanged -= OnViewChanged;
        _scrollView = null;
        itemsView.Loaded -= OnLoaded;
        itemsView.Unloaded -= OnUnloaded;
    }
}

internal static class ItemsViewScrollControllerExtensions
{
    public static Element BindVerticalScrollController<T>(
        this ItemsViewElement<T> itemsView,
        ElementRef<WinUIAnnotatedScrollBar> scrollBarRef,
        int initialTailIndex,
        string initialTailRequestKey) =>
        new ItemsViewVerticalScrollControllerElement(itemsView, scrollBarRef, initialTailIndex, initialTailRequestKey);
}
