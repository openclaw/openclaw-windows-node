using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
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
    int ItemCount,
    string InitialTailRequestKey,
    string? DisplayedTailKey) : Element
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
        positioner.Request(
            element.InitialTailIndex,
            element.ItemCount,
            element.InitialTailRequestKey,
            element.DisplayedTailKey);
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
            positioner.Request(
                newElement.InitialTailIndex,
                newElement.ItemCount,
                newElement.InitialTailRequestKey,
                newElement.DisplayedTailKey);
        else if (Positioners.TryGetValue(itemsView, out var existingPositioner))
            existingPositioner.UpdateTail(
                newElement.InitialTailIndex,
                newElement.ItemCount,
                newElement.DisplayedTailKey);
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
    private const double FollowThreshold = 60;

    private readonly WinUIItemsView itemsView;
    private string? _requestKey;
    private string? _displayedTailKey;
    private int _tailIndex;
    private int _itemCount;
    private int _version;
    private bool _valid;
    private bool _awaitingLayout;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private bool _following;
    private readonly TailNavigationQueue _tailNavigationQueue = new();
    private bool _disposed;

    public InitialTailPositioner(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        itemsView.Loaded += OnLoaded;
        itemsView.Unloaded += OnUnloaded;
    }

    public void Request(int tailIndex, int itemCount, string requestKey, string? displayedTailKey)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
            return;

        _requestKey = requestKey;
        _version++;
        DetachLayout();
        _tailIndex = tailIndex;
        _itemCount = itemCount;
        _displayedTailKey = displayedTailKey;
        _following = true;
        _valid = TailNavigationPolicy.TryCapture(tailIndex, displayedTailKey, itemCount, out _);
        if (!_valid)
            return;

        if (itemsView.IsLoaded)
            AwaitLayout();
    }

    public void UpdateTail(int tailIndex, int itemCount, string? displayedTailKey)
    {
        var changed = !string.Equals(_displayedTailKey, displayedTailKey, StringComparison.Ordinal);
        _tailIndex = tailIndex;
        _itemCount = itemCount;
        _displayedTailKey = displayedTailKey;
        _valid = TailNavigationPolicy.TryCapture(tailIndex, displayedTailKey, itemCount, out var request);
        if (!_valid)
        {
            _tailNavigationQueue.Clear();
            return;
        }

        if (changed && _following && itemsView.IsLoaded)
        {
            QueueTailRequest(_version, request);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_valid)
            AwaitLayout();
    }

    private void AwaitLayout()
    {
        if (_disposed || !_valid || !itemsView.IsLoaded || _awaitingLayout)
            return;

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
        if (sender is WinUIScrollView scrollView)
            scrollView.Loaded -= OnScrollViewLoaded;

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
        if (!TailNavigationPolicy.TryCapture(_tailIndex, _displayedTailKey, _itemCount, out var request))
            return;

        itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version
                || itemsView.ScrollView is not { IsLoaded: true })
            {
                if (!_disposed && _valid)
                    AwaitLayout();
                return;
            }

            AttachScrollView();
            if (!StartTailRequest(request) && !_disposed && _valid)
                AwaitLayout();
        });
    }

    private void AttachScrollView()
    {
        var nextScrollView = itemsView.ScrollView;
        if (ReferenceEquals(_scrollView, nextScrollView))
            return;

        DetachScrollView();
        _scrollView = nextScrollView;
        if (_scrollView is not null)
        {
            _scrollView.VerticalAnchorRatio = 1.0;
            _scrollView.ViewChanged += OnViewChanged;
        }
    }

    private void OnViewChanged(WinUIScrollView sender, object args)
    {
        _following = IsNearBottom(sender);
    }

    private void QueueTailRequest(int version, TailNavigationRequest request)
    {
        if (!_tailNavigationQueue.Enqueue(version, request))
            return;

        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_tailNavigationQueue.TryDequeue(_version, out var queuedRequest)
                || _disposed
                || !_valid
                || !itemsView.IsLoaded
                || !_following)
            {
                return;
            }

            StartTailRequest(queuedRequest);
        }))
        {
            _tailNavigationQueue.SchedulingFailed();
            _following = false;
        }
    }

    private bool StartTailRequest(TailNavigationRequest request)
    {
        if (itemsView.ScrollView is not { IsLoaded: true })
            return false;

        if (!TailNavigationPolicy.CanExecute(
                request,
                _tailIndex,
                _displayedTailKey,
                _itemCount))
        {
            return false;
        }

        _following = true;
        itemsView.StartBringItemIntoView(request.Index, new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 1.0,
        });
        return true;
    }

    private static bool IsNearBottom(WinUIScrollView scrollView) =>
        scrollView.ScrollableHeight - scrollView.VerticalOffset <= FollowThreshold;

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _version++;
        _tailNavigationQueue.Clear();
        DetachLayout();
        DetachScrollView();
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

    private void DetachScrollView()
    {
        if (_scrollView is not null)
        {
            _scrollView.VerticalAnchorRatio = double.NaN;
            _scrollView.ViewChanged -= OnViewChanged;
        }

        _scrollView = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _version++;
        _tailNavigationQueue.Clear();
        DetachLayout();
        DetachScrollView();
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
        int itemCount,
        string initialTailRequestKey,
        string? displayedTailKey) =>
        new ItemsViewVerticalScrollControllerElement(
            itemsView,
            scrollBarRef,
            initialTailIndex,
            itemCount,
            initialTailRequestKey,
            displayedTailKey);
}
