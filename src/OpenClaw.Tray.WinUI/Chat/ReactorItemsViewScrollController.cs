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
    private EventHandler<object>? _confirming;
    private readonly InitialTailConfirmation _confirmation = new();
    private readonly PointerEventHandler _cancelPointerNavigation;
    private bool _awaitingIdle;
    private readonly TailReflowCorrection _reflowCorrection = new();
    private Microsoft.UI.Xaml.Controls.Primitives.IScrollController? _inputScrollController;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private int? _inFlightBringCorrelation;
    private int? _deferredBringCorrelation;
    private bool _following;
    private bool _initialPending;
    private bool _awaitingInitialRows;
    private readonly TailNavigationQueue _tailNavigationQueue = new();
    private PendingTailBring? _pendingBring;
    private bool _disposed;

    private sealed record PendingTailBring(
        int Version, bool RequiresFollowing, Action? Started, bool NeedsRealization);

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
        // Serialize positioning against a previous conversation's native scroll.
        // This is ordering/cancellation bookkeeping, not a cure for the ItemsView
        // wrapper's recycled-target loop. Tail navigation bypasses that wrapper.
        _deferredBringCorrelation = _inFlightBringCorrelation;
        if (_deferredBringCorrelation is null)
            RestoreFarEdgeAnchoring();
        _tailIndex = tailIndex;
        _itemCount = itemCount;
        _displayedTailKey = displayedTailKey;
        _following = true;
        _initialPending = false;
        _valid = TailNavigationPolicy.TryCapture(tailIndex, displayedTailKey, itemCount, out _);
        _awaitingInitialRows = !_valid;
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
            if (_initialPending)
            {
                _version++;
                _initialPending = false;
                _awaitingInitialRows = true;
                DetachLayout();
            }
            return;
        }

        // The first rows of a request that started empty take the initial path, so
        // the bring waits for layout and its landing is confirmed. Input clears this.
        if (_awaitingInitialRows)
        {
            _awaitingInitialRows = false;
            _tailNavigationQueue.Clear();
            _initialPending = true;
            if (itemsView.IsLoaded)
                AwaitLayout();
            return;
        }

        if (changed && itemsView.IsLoaded
            && TailNavigationPolicy.CanFollowTail(_following, _initialPending))
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
        if (_pendingBring is { } pending)
        {
            itemsView.LayoutUpdated -= OnLayoutUpdated;
            _awaitingLayout = false;
            _renderAfterLayout = false;
            DispatchPreparedBring(pending);
            return;
        }

        var renderAfterLayout = _renderAfterLayout;
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout(renderAfterLayout);
            return;
        }

        AttachScrollView();
        if (_deferredBringCorrelation is not null)
            return;

        var initialRequest = new InitialTailNavigationRequest(_version);

        if (!renderAfterLayout)
        {
            DispatchTailRequest(initialRequest);
            return;
        }

        // Extent changes can precede the native viewport's rendered geometry.
        _rendering = (_, _) =>
        {
            DetachRendering();
            if (_disposed || !_valid || !itemsView.IsLoaded || initialRequest.Version != _version)
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

            DispatchTailRequest(initialRequest, renderAfterLayout: true);
        };
        CompositionTarget.Rendering += _rendering;
    }

    private void DispatchTailRequest(
        InitialTailNavigationRequest initialRequest, bool renderAfterLayout = false)
    {
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || initialRequest.Version != _version
                || !TailNavigationPolicy.CanDispatch(renderAfterLayout, _following))
                return;

            if (!initialRequest.TryResolve(
                    _version, _tailIndex, _displayedTailKey, _itemCount, out var request))
                return;

            if (!renderAfterLayout)
                ConfirmInitialLanding(initialRequest.Version, request);
            else
                StartTailRequest(request);
        }))
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not be dispatched.");
    }

    private void ConfirmInitialLanding(int version, TailNavigationRequest request)
    {
        DetachConfirmation();
        var generation = _confirmation.Reset(GetRequestItem(request));
        if (!StartTailRequest(request, () => ObserveInitialLanding(version, generation)))
        {
            CompleteInitialLanding();
        }
    }

    private bool IsInitialConfirmationCurrent(int version, int generation) =>
        !_disposed && _valid && itemsView.IsLoaded && _initialPending
        && version == _version && _confirmation.IsCurrent(generation);

    private void ObserveInitialLanding(int version, int generation)
    {
        if (!IsInitialConfirmationCurrent(version, generation))
            return;

        _confirming = (_, _) =>
        {
            if (!IsInitialConfirmationCurrent(version, generation))
            {
                DetachConfirmation();
                return;
            }

            if (_scrollView is not { IsLoaded: true } scrollView
                || !ReferenceEquals(scrollView, itemsView.ScrollView))
            {
                DetachConfirmation();
                _confirmation.Invalidate();
                AwaitLayout();
                return;
            }

            switch (_confirmation.Observe(
                scrollView.State == ScrollingInteractionState.Idle, IsAtCurrentTail(scrollView)))
            {
                case InitialTailLanding.Landed:
                    CompleteInitialLanding();
                    break;
                case InitialTailLanding.Retry:
                    DetachConfirmation();
                    QueueInitialTailRetry(version, generation);
                    break;
                case InitialTailLanding.GiveUp:
                    CompleteInitialLanding();
                    OpenClawTray.Services.Logger.Warn("Chat initial tail navigation did not land at the tail.");
                    break;
            }
        };
        CompositionTarget.Rendering += _confirming;
    }

    private void QueueInitialTailRetry(int version, int generation)
    {
        // Realization and bring requests belong outside the rendering/layout callback.
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsInitialConfirmationCurrent(version, generation))
                return;

            if (!TailNavigationPolicy.TryCapture(
                    _tailIndex, _displayedTailKey, _itemCount, out var request))
            {
                CompleteInitialLanding();
                OpenClawTray.Services.Logger.Warn("Chat initial tail navigation retry could not start.");
                return;
            }

            _confirmation.BeginAttempt(GetRequestItem(request));
            if (!StartTailRequest(request, () => ObserveInitialLanding(version, generation)))
            {
                CompleteInitialLanding();
                OpenClawTray.Services.Logger.Warn("Chat initial tail navigation retry could not start.");
                return;
            }
        }))
        {
            if (IsInitialConfirmationCurrent(version, generation))
                CompleteInitialLanding();
            OpenClawTray.Services.Logger.Warn("Chat initial tail navigation retry could not be dispatched.");
        }
    }

    private void CompleteInitialLanding()
    {
        DetachConfirmation();
        _confirmation.Invalidate();
        _initialPending = false;
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
            _scrollView.BringingIntoView += OnBringingIntoView;
            _scrollView.ScrollCompleted += OnScrollCompleted;
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

        _confirmation.GeometryChanged();
        _reflowCorrection.Arm();
    }

    private void OnExtentChanged(WinUIScrollView sender, object args)
    {
        _confirmation.GeometryChanged();
        if (_reflowCorrection.TryConsume(_following, IsNearBottom(sender), _initialPending))
            AwaitLayout(renderAfterLayout: true);
    }

    private void ResumeAfterDeferredBring(int version)
    {
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || version != _version || _deferredBringCorrelation is not null)
                return;

            RestoreFarEdgeAnchoring();
            itemsView.InvalidateArrange();
            AwaitLayout();
        }))
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not resume after the previous scroll.");
    }

    // Keep the chat's far-edge anchor when a previous native scroll completes.
    // Direct container brings do not use ItemsView's temporary centered anchor.
    private void RestoreFarEdgeAnchoring()
    {
        if (_scrollView is not null)
            _scrollView.VerticalAnchorRatio = 1.0;
    }

    private void OnBringingIntoView(WinUIScrollView sender, ScrollingBringingIntoViewEventArgs args)
    {
        if (!ReferenceEquals(sender, _scrollView))
            return;

        _inFlightBringCorrelation = args.CorrelationId;
        if (!_initialPending || args.RequestEventArgs?.TargetElement is not FrameworkElement target)
            return;

        _confirmation.BringingIntoView(args.CorrelationId, target.DataContext);
    }

    private void OnScrollCompleted(WinUIScrollView sender, ScrollingScrollCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _scrollView))
            return;

        if (_inFlightBringCorrelation == args.CorrelationId)
            _inFlightBringCorrelation = null;
        if (_deferredBringCorrelation == args.CorrelationId)
        {
            _deferredBringCorrelation = null;
            ResumeAfterDeferredBring(_version);
        }
        if (_initialPending)
            _confirmation.ScrollCompleted(args.CorrelationId);
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
        _awaitingInitialRows = false;
        _deferredBringCorrelation = null;
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
        _confirmation.GeometryChanged();
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
                || !TailNavigationPolicy.CanFollowTail(_following, _initialPending))
            {
                return;
            }

            StartTailRequest(queuedRequest);
        }))
        {
            _tailNavigationQueue.SchedulingFailed();
            _following = false;
            OpenClawTray.Services.Logger.Warn("Chat tail follow could not be dispatched.");
        }
    }

    private bool StartTailRequest(
        TailNavigationRequest request, Action? started = null, bool allowPreparationRetry = true)
    {
        // A superseded queued follow request is cancellation, not a native failure.
        if (!TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount))
            return false;

        var repeater = itemsView.ScrollView is { IsLoaded: true, Content: ItemsRepeater owner }
            && IsCurrentTail(request, owner) ? owner : null;
        if (repeater is null && !allowPreparationRetry)
        {
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not realize the current tail.");
            return false;
        }

        // Nominate the owning repeater's anchor outside layout. Never retain this
        // element: native measure can recycle it before the bring is dispatched.
        if (repeater is not null)
            repeater.GetOrCreateElement(request.Index);

        // A temporarily missing/replaced source gets one preparation retry after
        // native layout (or the current ScrollView's Loaded event), not a loop.
        _pendingBring = new(_version, !_initialPending, started, NeedsRealization: repeater is null);
        // Supersede an older reflow render gate without invalidating confirmation.
        DetachRendering();
        _renderAfterLayout = false;
        if (_awaitingScrollView is { } previous && !ReferenceEquals(previous, itemsView.ScrollView))
        {
            previous.Loaded -= OnScrollViewLoaded;
            _awaitingScrollView = null;
        }
        AwaitLayout();
        if (!_awaitingLayout && _awaitingScrollView is null)
        {
            _pendingBring = null;
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not await native layout.");
            return false;
        }

        if (repeater is not null)
            repeater.InvalidateMeasure();
        else
            itemsView.InvalidateMeasure();
        return true;
    }

    private bool IsCurrentTail(TailNavigationRequest request, ItemsRepeater repeater) =>
        TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount)
        && repeater.ItemsSourceView is { } source && source.Count == _itemCount;

    private void DispatchPreparedBring(PendingTailBring pending)
    {
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_pendingBring, pending))
                return;
            _pendingBring = null;
            if (_disposed || !_valid || !itemsView.IsLoaded || pending.Version != _version
                || (pending.RequiresFollowing && !_following))
                return;

            if (pending.NeedsRealization)
            {
                if (!TailNavigationPolicy.TryCapture(_tailIndex, _displayedTailKey, _itemCount, out var retry)
                    || !StartTailRequest(retry, pending.Started, allowPreparationRetry: false))
                {
                    OpenClawTray.Services.Logger.Warn("Chat tail navigation preparation retry could not start.");
                    ObserveFailedInitialAttempt(pending);
                }
                return;
            }

            // Resolve the live tail again, then reacquire once after native layout.
            // A missing/recycled target is a bounded failure, not another layout loop.
            if (itemsView.ScrollView is not { IsLoaded: true, Content: ItemsRepeater repeater }
                || !TailNavigationPolicy.TryCapture(_tailIndex, _displayedTailKey, _itemCount, out var request)
                || !IsCurrentTail(request, repeater)
                || repeater.TryGetElement(request.Index) is not ItemContainer target
                || repeater.GetElementIndex(target) != request.Index
                || !ReferenceEquals(target.DataContext, GetRequestItem(request)))
            {
                OpenClawTray.Services.Logger.Warn("Chat tail navigation could not reacquire the current tail after layout.");
                ObserveFailedInitialAttempt(pending);
                return;
            }

            AttachScrollView();
            _following = true;
            // Confirmation must correlate the live item, not the pre-layout row.
            if (_initialPending)
                _confirmation.BeginAttempt(GetRequestItem(request));
            target.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 1.0,
            });
            pending.Started?.Invoke();
        }))
        {
            _pendingBring = null;
            if (!pending.RequiresFollowing)
                CompleteInitialLanding();
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not be dispatched after layout.");
        }
    }

    // An initial attempt that could not bring is observed like a missing native
    // bring, so InitialTailConfirmation's retry budget decides when to give up.
    private void ObserveFailedInitialAttempt(PendingTailBring pending)
    {
        if (pending.RequiresFollowing)
            return;
        if (pending.Started is { } observe)
            observe();
        else
            CompleteInitialLanding();
    }

    private object? GetRequestItem(TailNavigationRequest request) =>
        itemsView.ScrollView?.Content is ItemsRepeater repeater
        && repeater.ItemsSourceView is { } source
        && request.Index >= 0 && request.Index < source.Count
            ? source.GetAt(request.Index)
            : null;

    private static bool IsNearBottom(WinUIScrollView scrollView) =>
        scrollView.ScrollableHeight - scrollView.VerticalOffset <= FollowThreshold;

    // A missing or stale native source leaves a short view that reads as the
    // bottom. Only the current rows can confirm an initial landing.
    private bool IsAtCurrentTail(WinUIScrollView scrollView) =>
        IsNearBottom(scrollView)
        && scrollView.Content is ItemsRepeater { ItemsSourceView: { } source }
        && source.Count == _itemCount;

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
        _pendingBring = null;
        DetachRendering();
        DetachConfirmation();
        _confirmation.Invalidate();
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

    private void DetachConfirmation()
    {
        if (_confirming is not null)
            CompositionTarget.Rendering -= _confirming;
        _confirming = null;
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
            _scrollView.BringingIntoView -= OnBringingIntoView;
            _scrollView.ScrollCompleted -= OnScrollCompleted;
        }

        _scrollView = null;
        _inFlightBringCorrelation = null;
        _deferredBringCorrelation = null;
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
