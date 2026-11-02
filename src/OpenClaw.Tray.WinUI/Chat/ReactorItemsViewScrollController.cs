using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private bool _renderAfterLayout;
    private EventHandler<object>? _rendering;
    private readonly PointerEventHandler _cancelPointerNavigation;
    private bool _awaitingIdle;
    private readonly TailReflowCorrection _reflowCorrection = new();
    private Microsoft.UI.Xaml.Controls.Primitives.IScrollController? _inputScrollController;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private bool _following;
    private bool _initialPending;
    private readonly TailNavigationQueue _tailNavigationQueue = new();
    private bool _disposed;

    public InitialTailPositioner(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        _cancelPointerNavigation = (_, _) => CancelPendingNavigation();
        itemsView.AddHandler(UIElement.PointerPressedEvent, _cancelPointerNavigation, true);
        itemsView.AddHandler(UIElement.PointerWheelChangedEvent, _cancelPointerNavigation, true);
        itemsView.PreviewKeyDown += OnNavigationKeyDown;
        itemsView.SizeChanged += OnSizeChanged;
        itemsView.Loaded += OnLoaded;
        itemsView.Unloaded += OnUnloaded;
    }

    public void Request(int tailIndex, int itemCount, string requestKey, string? displayedTailKey)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
            return;

        _requestKey = requestKey;
        _version++;
        _reflowCorrection.Clear();
        DetachLayout();
        _tailIndex = tailIndex;
        _itemCount = itemCount;
        _displayedTailKey = displayedTailKey;
        _following = true;
        _initialPending = false;
        _valid = TailNavigationPolicy.TryCapture(tailIndex, displayedTailKey, itemCount, out _);
        if (!_valid)
            return;

        _initialPending = true;
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
        if (!_valid)
            return;

        _initialPending = true;
        AwaitLayout();
    }

    private void AwaitLayout(bool renderAfterLayout = false)
    {
        _renderAfterLayout |= renderAfterLayout;
        if (_disposed || !_valid || !itemsView.IsLoaded || _awaitingLayout
            || _awaitingScrollView is not null || _rendering is not null)
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
        var renderAfterLayout = _renderAfterLayout;
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout(renderAfterLayout);
            return;
        }

        AttachScrollView();
        var version = _version;
        if (!TailNavigationPolicy.TryCapture(_tailIndex, _displayedTailKey, _itemCount, out var request))
            return;

        if (!renderAfterLayout)
        {
            DispatchTailRequest(version, request);
            return;
        }

        // Extent changes can precede the native viewport's rendered geometry.
        _rendering = (_, _) =>
        {
            DetachRendering();
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version)
                return;
            if (_scrollView is not { IsLoaded: true })
            {
                AwaitLayout(renderAfterLayout: true);
                return;
            }
            if (_scrollView.State != ScrollingInteractionState.Idle)
            {
                _awaitingIdle = true;
                return;
            }

            DispatchTailRequest(version, request, renderAfterLayout: true);
        };
        CompositionTarget.Rendering += _rendering;
    }

    private void DispatchTailRequest(
        int version, TailNavigationRequest request, bool renderAfterLayout = false)
    {
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version
                || !TailNavigationPolicy.CanDispatch(renderAfterLayout, _following))
                return;

            if (!StartTailRequest(request))
                AwaitLayout(renderAfterLayout);
            else if (!renderAfterLayout)
                _initialPending = false;
        }))
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not be dispatched.");
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
            _scrollView.StateChanged += OnScrollStateChanged;
            _scrollView.ExtentChanged += OnExtentChanged;
            _inputScrollController = itemsView.VerticalScrollController;
            if (_inputScrollController is not null)
                _inputScrollController.IsScrollingWithMouseChanged += OnControllerMouseChanged;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.PreviousSize.Width <= 0 || args.PreviousSize.Height <= 0
            || (args.NewSize.Width == args.PreviousSize.Width
                && args.NewSize.Height == args.PreviousSize.Height))
            return;

        _reflowCorrection.Arm();
    }

    private void OnExtentChanged(WinUIScrollView sender, object args)
    {
        if (_reflowCorrection.TryConsume(_following, IsNearBottom(sender), _initialPending))
            AwaitLayout(renderAfterLayout: true);
    }

    private void OnScrollStateChanged(WinUIScrollView sender, object args)
    {
        if (sender.State == ScrollingInteractionState.Interaction)
            CancelPendingNavigation();
        else if (_awaitingIdle && sender.State == ScrollingInteractionState.Idle)
        {
            _awaitingIdle = false;
            AwaitLayout(renderAfterLayout: true);
        }
    }

    private void OnNavigationKeyDown(object sender, KeyRoutedEventArgs args) =>
        CancelPendingNavigation();

    private void OnControllerMouseChanged(
        Microsoft.UI.Xaml.Controls.Primitives.IScrollController sender, object args)
    {
        if (sender.IsScrollingWithMouse)
            CancelPendingNavigation();
    }

    private void CancelPendingNavigation()
    {
        _version++;
        _initialPending = false;
        // Input without movement (copy, focus, wheel at the tail) keeps follow;
        // input that already left the tail stops it so the reader is not pulled back.
        _following = _scrollView is { } scrollView && IsNearBottom(scrollView);
        _reflowCorrection.Clear();
        _tailNavigationQueue.Clear();
        DetachLayout();
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
        _initialPending = false;
        _reflowCorrection.Clear();
        _tailNavigationQueue.Clear();
        DetachLayout();
        DetachScrollView();
    }

    private void DetachLayout()
    {
        DetachRendering();
        _awaitingIdle = false;
        _renderAfterLayout = false;
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

    private void DetachRendering()
    {
        if (_rendering is not null)
            CompositionTarget.Rendering -= _rendering;
        _rendering = null;
    }

    private void DetachScrollView()
    {
        if (_inputScrollController is not null)
            _inputScrollController.IsScrollingWithMouseChanged -= OnControllerMouseChanged;
        _inputScrollController = null;
        if (_scrollView is not null)
        {
            _scrollView.VerticalAnchorRatio = double.NaN;
            _scrollView.ViewChanged -= OnViewChanged;
            _scrollView.StateChanged -= OnScrollStateChanged;
            _scrollView.ExtentChanged -= OnExtentChanged;
        }

        _scrollView = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _version++;
        _reflowCorrection.Clear();
        _tailNavigationQueue.Clear();
        DetachLayout();
        DetachScrollView();
        itemsView.Loaded -= OnLoaded;
        itemsView.Unloaded -= OnUnloaded;
        itemsView.SizeChanged -= OnSizeChanged;
        itemsView.PreviewKeyDown -= OnNavigationKeyDown;
        itemsView.RemoveHandler(UIElement.PointerPressedEvent, _cancelPointerNavigation);
        itemsView.RemoveHandler(UIElement.PointerWheelChangedEvent, _cancelPointerNavigation);
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
