using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinUIAnnotatedScrollBar = Microsoft.UI.Xaml.Controls.AnnotatedScrollBar;
using WinUIItemsView = Microsoft.UI.Xaml.Controls.ItemsView;
using WinUIScrollView = Microsoft.UI.Xaml.Controls.ScrollView;

namespace OpenClawTray.Chat;

file sealed record ItemsViewVerticalScrollControllerElement(
    Element Child,
    ElementRef<WinUIAnnotatedScrollBar> ScrollBarRef,
    int InitialTailIndex,
    int ItemCount,
    int JumpRevision,
    string InitialTailRequestKey,
    string? DisplayedTailKey,
    object ContentRevision,
    Action<bool>? TailVisibilityChanged) : Element
{
    static ItemsViewVerticalScrollControllerElement() =>
        ControlRegistry.RegisterDecorator<ItemsViewVerticalScrollControllerElement>(
            static () => new ItemsViewVerticalScrollControllerHandler());
}

file sealed class ItemsViewVerticalScrollControllerHandler
    : IDecoratorElementHandler<ItemsViewVerticalScrollControllerElement>
{
    // Keep ownership on the native control, not on a particular managed projection.
    private static readonly DependencyProperty PositionerProperty = DependencyProperty.RegisterAttached(
        "ChatScrollAdapter", typeof(object), typeof(ItemsViewVerticalScrollControllerHandler), new PropertyMetadata(null));

    public UIElement Mount(MountContext context, ItemsViewVerticalScrollControllerElement element)
    {
        var control = context.MountChild(element.Child);
        if (control is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");

        BindPositioner(itemsView, element, context.BindFor(itemsView, element));
        return itemsView;
    }

    private static ConversationScrollAdapter BindPositioner(
        WinUIItemsView itemsView,
        ItemsViewVerticalScrollControllerElement element,
        ReactorBinding<ItemsViewVerticalScrollControllerElement> binding)
    {
        (itemsView.GetValue(PositionerProperty) as ConversationScrollAdapter)?.Dispose();
        binding.Reference(
            get: static value => value.ScrollBarRef,
            set: static (value, scrollBar) =>
                ((WinUIItemsView)value).VerticalScrollController = scrollBar?.ScrollController);
        var positioner = new ConversationScrollAdapter(itemsView);
        positioner.TailVisibilityChanged = element.TailVisibilityChanged;
        positioner.UpdateContentRevision(element.ContentRevision);
        itemsView.SetValue(PositionerProperty, positioner);
        positioner.Request(
            element.InitialTailIndex,
            element.ItemCount,
            element.InitialTailRequestKey,
            element.DisplayedTailKey,
            explicitJump: false);
        return positioner;
    }

    public UIElement Update(
        UpdateContext context,
        ItemsViewVerticalScrollControllerElement oldElement,
        ItemsViewVerticalScrollControllerElement newElement,
        UIElement control)
    {
        var previousPositioner = control.GetValue(PositionerProperty) as ConversationScrollAdapter;
        var updated = context.ReconcileChild(oldElement.Child, newElement.Child, control);
        if (updated is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");
        var positioner = itemsView.GetValue(PositionerProperty) as ConversationScrollAdapter;
        if (previousPositioner is not null && !ReferenceEquals(previousPositioner, positioner))
        {
            if (ReferenceEquals(control.GetValue(PositionerProperty), previousPositioner))
                control.ClearValue(PositionerProperty);
            previousPositioner.Dispose();
        }
        if (positioner is null)
            BindPositioner(itemsView, newElement, context.BindFor(itemsView, newElement));
        else
        {
            positioner.TailVisibilityChanged = newElement.TailVisibilityChanged;
            positioner.UpdateContentRevision(newElement.ContentRevision);
            if (!string.Equals(oldElement.InitialTailRequestKey, newElement.InitialTailRequestKey, StringComparison.Ordinal))
                positioner.Request(
                    newElement.InitialTailIndex,
                    newElement.ItemCount,
                    newElement.InitialTailRequestKey,
                    newElement.DisplayedTailKey,
                    explicitJump: oldElement.JumpRevision != newElement.JumpRevision);
            else
                positioner.UpdateTail(
                    newElement.InitialTailIndex,
                    newElement.ItemCount,
                    newElement.DisplayedTailKey);
        }
        return itemsView;
    }

    public V1UnmountDisposition Unmount(UnmountContext context, ItemsViewVerticalScrollControllerElement? element, UIElement control)
    {
        if (control.GetValue(PositionerProperty) is ConversationScrollAdapter positioner)
        {
            control.ClearValue(PositionerProperty);
            positioner.Dispose();
        }
        return V1UnmountDisposition.ContinueDefaultTraversal;
    }
}

// Thin native adapter for the conversation ScrollView. It translates WinUI events into
// ChatScrollPolicy inputs and applies the returned command; every whether-to-scroll
// decision lives in the policy. This is the only code allowed to scroll the conversation.
// The native mechanics it keeps (layout/render waits, staged row realization, landing
// confirmation and generation fencing) exist because synchronous ItemsView brings and
// layout-time scrolling hang or cycle WinUI layout.
file sealed class ConversationScrollAdapter : IDisposable
{
    private const double OffsetCorrectionAnchorRatio = 0.5;
    // Exactly 1 selects WinUI's extent-edge mode, which can undo an offset as virtual
    // rows remeasure. The nearest interior point anchors content at its bottom instead.
    private static readonly double BottomAnchorRatio = Math.BitDecrement(1.0);
    private const double TopAnchorRatio = 0;

    private readonly WinUIItemsView itemsView;
    private readonly ChatScrollPolicy _policy = new();
    private string? _requestKey;
    private string? _displayedTailKey;
    private object? _contentRevision;
    private int _tailIndex;
    private int _itemCount;
    private int _version;
    private bool _valid;
    private bool _awaitingLayout;
    private bool _landingPending;
    private EventHandler<object>? _rendering;
    private EventHandler<object>? _confirming;
    private EventHandler<object>? _realizing;
    private int _realizationVersion;
    private readonly InitialTailConfirmation _confirmation = new();
    private readonly PointerEventHandler _readerWheel;
    private readonly PointerEventHandler _readerPointerPressed;
    private readonly TypedEventHandler<UIElement, GettingFocusEventArgs> _readerGettingFocus;
    private int? _readerBringCorrelationId;
    private TypedEventHandler<WinUIScrollView, ScrollingAnchorRequestedEventArgs>? _offsetCorrectionAnchorRequested;
    private bool _awaitingIdleLanding;
    private Microsoft.UI.Xaml.Controls.Primitives.IScrollController? _inputScrollController;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private readonly TailNavigationQueue _tailNavigationQueue = new();
    private bool _disposed;
    internal Action<bool>? TailVisibilityChanged { get; set; }
    private bool? _reportedAtTail;

    public ConversationScrollAdapter(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        _readerWheel = (_, _) => OnUserInput();
        _readerPointerPressed = (_, args) => OnReaderInput(args.OriginalSource, mayToggleDisclosure: true);
        _readerGettingFocus = OnReaderFocus;
        itemsView.AddHandler(UIElement.PointerPressedEvent, _readerPointerPressed, true);
        itemsView.AddHandler(UIElement.PointerWheelChangedEvent, _readerWheel, true);
        itemsView.PreviewKeyDown += OnNavigationKeyDown;
        itemsView.AddHandler(UIElement.GettingFocusEvent, _readerGettingFocus, true);
        itemsView.SizeChanged += OnSizeChanged;
        itemsView.Loaded += OnLoaded;
        itemsView.Unloaded += OnUnloaded;
    }

    public void Request(
        int tailIndex,
        int itemCount,
        string requestKey,
        string? displayedTailKey,
        bool explicitJump)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
            return;

        _requestKey = requestKey;
        SetTail(tailIndex, itemCount, displayedTailKey);
        Apply(_policy.Request(
            explicitJump ? ChatScrollRequest.JumpToLatest : ChatScrollRequest.Open, _valid));
    }

    public void UpdateTail(int tailIndex, int itemCount, string? displayedTailKey)
    {
        if (_disposed)
            return;

        var changed = !string.Equals(_displayedTailKey, displayedTailKey, StringComparison.Ordinal);
        SetTail(tailIndex, itemCount, displayedTailKey);
        Apply(_policy.RowsChanged(_valid, changed));
    }

    // A new Entries revision grants the policy a fresh, bounded correction budget.
    public void UpdateContentRevision(object revision)
    {
        if (_disposed || ReferenceEquals(_contentRevision, revision))
            return;

        _contentRevision = revision;
        Apply(_policy.ContentRevisionChanged());
    }

    private void SetTail(int tailIndex, int itemCount, string? displayedTailKey)
    {
        _tailIndex = tailIndex;
        _itemCount = itemCount;
        _displayedTailKey = displayedTailKey;
        _valid = TailNavigationPolicy.TryCapture(tailIndex, displayedTailKey, itemCount, out _);
    }

    // The only path from a policy decision to native scrolling.
    private void Apply(ChatScrollCommand command)
    {
        if (_disposed)
            return;

        switch (command.Action)
        {
            case ChatScrollAction.Cancel:
                CancelNativeWork();
                break;
            case ChatScrollAction.LandOnTail:
                CancelNativeWork();
                if (itemsView.IsLoaded)
                    AwaitLayout();
                break;
            // A pin is the same staged tail-row bring as a new row. An offset scroll to the
            // extent end enters WinUI's extent-edge mode, where remeasured virtual rows snap
            // the offset back; an element bring keeps the tail row as the native target.
            // A pin never restarts a bring already settling.
            case ChatScrollAction.ScrollToTail:
            case ChatScrollAction.PinToBottom:
                if (itemsView.IsLoaded
                    && (command.Action == ChatScrollAction.ScrollToTail || _realizing is null)
                    && TailNavigationPolicy.TryCapture(_tailIndex, _displayedTailKey, _itemCount, out var request))
                    QueueTailRequest(_version, request);
                break;
        }

        if (!_policy.ReaderNavigationPending)
            _readerBringCorrelationId = null;
        ApplyAnchor();
        ReportTailVisibility();
    }

    private void ApplyAnchor()
    {
        // A landing offset correction temporarily owns the native anchor.
        if (_scrollView is null || _offsetCorrectionAnchorRequested is not null)
            return;

        var ratio = _policy.Anchor == ChatScrollAnchor.Top ? TopAnchorRatio : BottomAnchorRatio;
        if (_scrollView.VerticalAnchorRatio != ratio)
            _scrollView.VerticalAnchorRatio = ratio;
    }

    private void CancelNativeWork()
    {
        _version++;
        _tailNavigationQueue.Clear();
        DetachLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => Apply(_policy.Loaded(_valid));

    private void AwaitLayout()
    {
        _landingPending = true;
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
        var version = _version;
        // A Jump request may change no layout property. Drain pending layout, but do
        // not wait forever for a LayoutUpdated event that the stable view will not raise.
        if (!itemsView.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_disposed && version == _version && _awaitingLayout && _landingPending
                    && _policy.IsLanding && itemsView.IsLoaded
                    && itemsView.ScrollView is { IsLoaded: true, ViewportHeight: > 0 })
                    CompleteLayoutWait();
            }))
            OpenClawTray.Services.Logger.Warn("Chat tail layout wait could not be dispatched.");
    }

    private void OnScrollViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is WinUIScrollView scrollView)
            scrollView.Loaded -= OnScrollViewLoaded;

        _awaitingScrollView = null;
        if (_landingPending)
            AwaitLayout();
    }

    private void OnLayoutUpdated(object? sender, object args) => CompleteLayoutWait();

    private void CompleteLayoutWait()
    {
        if (!_landingPending)
            return;
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout();
            return;
        }

        AttachScrollView();
        var initialRequest = new InitialTailNavigationRequest(_version);
        if (!_policy.LandAfterRender)
        {
            DispatchLanding(initialRequest);
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
                AwaitLayout();
                return;
            }
            if (_scrollView.State != ScrollingInteractionState.Idle)
            {
                _awaitingIdleLanding = true;
                return;
            }

            DispatchLanding(initialRequest);
        };
        CompositionTarget.Rendering += _rendering;
    }

    private void DispatchLanding(InitialTailNavigationRequest initialRequest)
    {
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || initialRequest.Version != _version
                || !_policy.Permits(ChatScrollAction.LandOnTail)
                || !initialRequest.TryResolve(
                    _version, _tailIndex, _displayedTailKey, _itemCount, out var request))
                return;

            if (_scrollView is { IsLoaded: true } scrollView
                && ReferenceEquals(scrollView, itemsView.ScrollView)
                && TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount))
                ConfirmInitialLanding(initialRequest.Version, request);
            else
                AwaitLayout();
        }))
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not be dispatched.");
    }

    private void ConfirmInitialLanding(int version, TailNavigationRequest request)
    {
        DetachConfirmation();
        var generation = _confirmation.Reset(GetRequestItem(request));
        if (!StartTailRequest(request, ChatScrollAction.LandOnTail))
        {
            _confirmation.Invalidate();
            AwaitLayout();
            return;
        }

        ObserveInitialLanding(version, generation);
    }

    private bool IsInitialConfirmationCurrent(int version, int generation) =>
        !_disposed && _valid && itemsView.IsLoaded && _policy.IsLanding
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
                scrollView.State == ScrollingInteractionState.Idle, Geometry(scrollView).AtBottom))
            {
                case InitialTailLanding.Landed:
                    CompleteInitialLanding();
                    break;
                case InitialTailLanding.Retry:
                    DetachConfirmation();
                    QueueInitialTailRetry(version, generation);
                    break;
                case InitialTailLanding.CorrectOffset:
                    DetachConfirmation();
                    QueueInitialTailRetry(version, generation, correctOffset: true);
                    break;
                case InitialTailLanding.GiveUp:
                    var diagnostics = _confirmation.Diagnostics;
                    var attemptDiagnostics = _confirmation.AttemptDiagnostics;
                    var bringOptions = _confirmation.BringOptionsObservation;
                    CompleteInitialLanding();
                    LogInitialTailFailure(diagnostics, attemptDiagnostics, scrollView, bringOptions);
                    break;
            }
        };
        CompositionTarget.Rendering += _confirming;
    }

    private void LogInitialTailFailure(InitialTailDiagnosticSnapshot diagnostics,
        InitialTailAttemptDiagnostics attempt, WinUIScrollView scrollView,
        InitialTailBringOptionsObservation bringOptions)
    {
        // This runs only after the existing GiveUp cleanup. Native reads and the
        // local logger are best effort: neither can interrupt navigation cleanup.
        var native = "nativeReadSucceeded=False";
        try
        {
            native = FormattableString.Invariant(
                $"nativeReadSucceeded=True offset={scrollView.VerticalOffset} scrollableHeight={scrollView.ScrollableHeight} viewportHeight={scrollView.ViewportHeight} state={(int)scrollView.State}");
        }
        catch (Exception) { }

        try
        {
            OpenClawTray.Services.Logger.Warn(FormattableString.Invariant(
                $"Chat initial tail navigation did not land at the tail. frames={diagnostics.ObservedFrames} retries={diagnostics.Retries} settledFrames={diagnostics.SettledFrames} hasExpectedTarget={diagnostics.HasExpectedTarget} hasCorrelation={diagnostics.HasCorrelation} bringCompleted={diagnostics.BringCompleted} attemptActive={diagnostics.AttemptActive} itemCount={_itemCount} tailIndex={_tailIndex} {native} ") +
                FormattableString.Invariant($"bringReadSucceeded={attempt.ProposedOffset.HasValue && attempt.BringOffset.HasValue && attempt.BringScrollableHeight.HasValue} proposedOffset={attempt.ProposedOffset} bringOffset={attempt.BringOffset} bringScrollableHeight={attempt.BringScrollableHeight} targetIndexAvailable={attempt.TargetIndex.HasValue} targetIndex={attempt.TargetIndex} ") +
                FormattableString.Invariant($"completionReadSucceeded={attempt.CompletedOffset.HasValue && attempt.CompletedScrollableHeight.HasValue} completedOffset={attempt.CompletedOffset} completedScrollableHeight={attempt.CompletedScrollableHeight} completionSourceReadSucceeded={attempt.CompletionSourceItemMatches.HasValue} completionSourceItemMatches={attempt.CompletionSourceItemMatches} ") +
                FormattableString.Invariant($"maxOffsetAvailable={attempt.MaxObservedOffset.HasValue} maxObservedOffset={attempt.MaxObservedOffset} ") +
                FormattableString.Invariant($"bringCancelReadSucceeded={bringOptions.CancelAtCallback.HasValue} bringCancelAtCallback={bringOptions.CancelAtCallback} bringAnimationDesiredReadSucceeded={bringOptions.AnimationDesiredAtCallback.HasValue} bringAnimationDesiredAtCallback={bringOptions.AnimationDesiredAtCallback}"));
        }
        catch (Exception) { }
    }

    private void QueueInitialTailRetry(int version, int generation, bool correctOffset = false)
    {
        // Starting a native bring from Rendering can deadlock the WinUI layout pass.
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

            var targetItem = GetRequestItem(request);
            correctOffset &= _confirmation.CanCorrectOffset(targetItem);
            _confirmation.BeginAttempt(targetItem);
            if (correctOffset)
            {
                if (_scrollView is not { IsLoaded: true } scrollView
                    || !ReferenceEquals(scrollView, itemsView.ScrollView))
                {
                    _confirmation.Invalidate();
                    AwaitLayout();
                    return;
                }

                // An indexed bring can complete without moving restored, virtualized history.
                // Correct to the live extent within the existing retry budget, not the stale bring target.
                // Keep the tail as the native anchor while recycled rows change the extent.
                // Disabling anchoring can cycle layout; an edge anchor can undo the correction.
                if (_offsetCorrectionAnchorRequested is not null)
                    scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested;
                _offsetCorrectionAnchorRequested = (sender, args) =>
                {
                    if (IsInitialConfirmationCurrent(version, generation)
                        && ReferenceEquals(sender, _scrollView)
                        && TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount)
                        && sender.Content is ItemsRepeater repeater
                        && repeater.TryGetElement(request.Index) is FrameworkElement { IsLoaded: true, Visibility: Visibility.Visible } target
                        && ReferenceEquals(target.DataContext, targetItem))
                    {
                        args.AnchorElement = target;
                    }
                };
                scrollView.AnchorRequested += _offsetCorrectionAnchorRequested;
                scrollView.VerticalAnchorRatio = OffsetCorrectionAnchorRatio;
                var correlationId = scrollView.ScrollTo(
                    scrollView.HorizontalOffset, scrollView.ScrollableHeight,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                _confirmation.OffsetCorrectionStarted(correlationId);
            }
            else if (!StartTailRequest(request, ChatScrollAction.LandOnTail))
            {
                CompleteInitialLanding();
                OpenClawTray.Services.Logger.Warn("Chat initial tail navigation retry could not start.");
                return;
            }

            ObserveInitialLanding(version, generation);
        }))
        {
            if (IsInitialConfirmationCurrent(version, generation))
                CompleteInitialLanding();
            OpenClawTray.Services.Logger.Warn("Chat initial tail navigation retry could not be dispatched.");
        }
    }

    private void CompleteInitialLanding()
    {
        ReleaseOffsetCorrectionAnchor();
        CancelRealization();
        DetachConfirmation();
        _confirmation.Invalidate();
        Apply(_policy.LandingCompleted(CurrentGeometry()));
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
            ApplyAnchor();
            _scrollView.ViewChanged += OnViewChanged;
            _scrollView.StateChanged += OnScrollStateChanged;
            _scrollView.ExtentChanged += OnExtentChanged;
            _scrollView.BringingIntoView += OnBringingIntoView;
            _scrollView.ScrollCompleted += OnScrollCompleted;
            _inputScrollController = itemsView.VerticalScrollController;
            if (_inputScrollController is not null)
                _inputScrollController.IsScrollingWithMouseChanged += OnControllerMouseChanged;
            ReportTailVisibility();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.PreviousSize.Width <= 0 || args.PreviousSize.Height <= 0
            || (args.NewSize.Width == args.PreviousSize.Width
                && args.NewSize.Height == args.PreviousSize.Height))
            return;

        _confirmation.GeometryChanged();
        Apply(_policy.ViewportResized());
    }

    private void OnExtentChanged(WinUIScrollView sender, object args)
    {
        _confirmation.GeometryChanged();
        Apply(_policy.ContentChanged(Geometry(sender)));
    }

    private void OnBringingIntoView(WinUIScrollView sender, ScrollingBringingIntoViewEventArgs args)
    {
        if (ReferenceEquals(sender, _scrollView) && !args.Cancel
            && args.RequestEventArgs?.TargetElement is Control { FocusState: FocusState.Keyboard })
        {
            // A real reader destination leaves the tail before the native offset can move.
            // Edge anchoring can otherwise keep remeasuring virtual rows and starve this request.
            var command = _policy.ReaderNavigationStarting(sender.ScrollableHeight - args.TargetVerticalOffset);
            if (_policy.ReaderNavigationPending)
                _readerBringCorrelationId = args.CorrelationId;
            Apply(command);
        }

        if (!_policy.IsLanding || !ReferenceEquals(sender, _scrollView)
            || args.RequestEventArgs?.TargetElement is not FrameworkElement target)
            return;

        var correlationId = args.CorrelationId;
        var targetItem = target.DataContext;
        (double ProposedOffset, double Offset, double ScrollableHeight)? geometry = null;
        int? targetIndex = null;
        // Public getters only. Do not realize a row or force layout to diagnose it.
        try { geometry = (args.TargetVerticalOffset, sender.VerticalOffset, sender.ScrollableHeight); }
        catch (Exception) { }
        try
        {
            if (sender.Content is Microsoft.UI.Xaml.Controls.ItemsRepeater repeater
                && ReferenceEquals(VisualTreeHelper.GetParent(target), repeater))
                targetIndex = repeater.GetElementIndex(target);
        }
        catch (Exception) { }
        _confirmation.BringingIntoView(correlationId, targetItem, geometry, targetIndex);
        // Passive callback-time values, not the final outcome after all listeners.
        bool? cancelAtCallback = null;
        bool? animationDesiredAtCallback = null;
        try { cancelAtCallback = args.Cancel; }
        catch (Exception) { }
        try { animationDesiredAtCallback = args.RequestEventArgs?.AnimationDesired; }
        catch (Exception) { }
        _confirmation.ObserveBringOptions(correlationId, targetItem, cancelAtCallback, animationDesiredAtCallback);
    }

    private void OnScrollCompleted(WinUIScrollView sender, ScrollingScrollCompletedEventArgs args)
    {
        if (ReferenceEquals(sender, _scrollView) && _readerBringCorrelationId == args.CorrelationId)
        {
            _readerBringCorrelationId = null;
            Apply(_policy.ReaderNavigationCompleted(Geometry(sender)));
        }

        if (_policy.IsLanding && ReferenceEquals(sender, _scrollView))
        {
            var correlationId = args.CorrelationId;
            (double Offset, double ScrollableHeight)? geometry = null;
            object? currentSourceItem = null;
            var sourceReadSucceeded = false;
            try { geometry = (sender.VerticalOffset, sender.ScrollableHeight); }
            catch (Exception) { }
            try
            {
                if (itemsView.ItemsSource is System.Collections.IList items)
                {
                    currentSourceItem = _tailIndex >= 0 && _tailIndex < items.Count ? items[_tailIndex] : null;
                    sourceReadSucceeded = true;
                }
            }
            catch (Exception) { }
            _confirmation.ScrollCompleted(correlationId, geometry, currentSourceItem, sourceReadSucceeded);
        }
    }

    private void OnScrollStateChanged(WinUIScrollView sender, object args)
    {
        if (sender.State == ScrollingInteractionState.Interaction)
            OnUserInput();
        else if (sender.State == ScrollingInteractionState.Idle && ReferenceEquals(sender, _scrollView))
        {
            if (_awaitingIdleLanding)
            {
                _awaitingIdleLanding = false;
                AwaitLayout();
            }
            Apply(_policy.Settled(Geometry(sender)));
        }
    }

    private void OnNavigationKeyDown(object sender, KeyRoutedEventArgs args) =>
        OnReaderInput(
            args.OriginalSource,
            mayToggleDisclosure: args.Key is global::Windows.System.VirtualKey.Enter or global::Windows.System.VirtualKey.Space);

    private void OnReaderInput(object? source, bool mayToggleDisclosure)
    {
        if (mayToggleDisclosure && _scrollView is not null && IsDisclosureHeader(source))
            Apply(_policy.DisclosureToggled(CurrentGeometry()));
        else
            OnUserInput();
    }

    private bool IsDisclosureHeader(object? source)
    {
        for (var element = source as UIElement;
             element is not null && !ReferenceEquals(element, itemsView);
             element = VisualTreeHelper.GetParent(element) as UIElement)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Name: "ExpanderHeader" })
                return true;
        }

        return false;
    }

    private void OnReaderFocus(UIElement sender, GettingFocusEventArgs args)
    {
        // Programmatic realization focus must not cancel pending tail navigation.
        if (args.FocusState == FocusState.Keyboard)
            OnUserInput();
    }

    private void OnControllerMouseChanged(
        Microsoft.UI.Xaml.Controls.Primitives.IScrollController sender, object args)
    {
        if (sender.IsScrollingWithMouse)
            OnUserInput();
        else if (_scrollView is { } scrollView)
            Apply(_policy.Settled(Geometry(scrollView)));
    }

    private void OnUserInput() => Apply(_policy.UserInput(CurrentGeometry()));

    private void OnViewChanged(WinUIScrollView sender, object args)
    {
        var geometry = Geometry(sender);
        _confirmation.GeometryChanged();
        if (_policy.IsLanding && ReferenceEquals(sender, _scrollView))
        {
            try { _confirmation.ObserveOffset(sender.VerticalOffset); }
            catch (Exception) { }
        }
        // Any resulting pin is deferred past layout and rendering, never issued here.
        Apply(_policy.ViewChanged(geometry));
    }

    // Jump to latest follows the policy mode, not the raw offset, so an overshoot or a
    // pending pin cannot flash it while following.
    private void ReportTailVisibility()
    {
        var atTail = !_policy.ShowsJumpToLatest;
        if (_reportedAtTail != atTail)
        {
            _reportedAtTail = atTail;
            TailVisibilityChanged?.Invoke(atTail);
        }
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
                || !_policy.Permits(ChatScrollAction.ScrollToTail))
            {
                return;
            }

            StartTailRequest(queuedRequest, ChatScrollAction.ScrollToTail);
        }))
        {
            _tailNavigationQueue.SchedulingFailed();
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not be queued.");
        }
    }

    private bool StartTailRequest(TailNavigationRequest request, ChatScrollAction purpose)
    {
        if (itemsView.ScrollView is not { IsLoaded: true } scrollView)
            return false;

        if (!TailNavigationPolicy.CanExecute(
                request,
                _tailIndex,
                _displayedTailKey,
                _itemCount))
        {
            return false;
        }

        ReleaseOffsetCorrectionAnchor();
        if (scrollView.Content is not ItemsRepeater repeater)
            return false;

        CancelRealization();
        // Avoid ItemsView's synchronous layout and recycled-container ancestor loop.
        // The repeater retains virtualization while the row is arranged naturally.
        var target = repeater.GetOrCreateElement(request.Index) as FrameworkElement;
        if (target is null)
            return false;
        var version = _version;
        var realizationVersion = _realizationVersion;
        var frames = 0;
        var settledFrames = 0;
        (double Extent, double Viewport, double Top, double Height)? previous = null;
        _realizing = (_, _) =>
        {
            if (_disposed || version != _version || realizationVersion != _realizationVersion || !itemsView.IsLoaded
                || !ReferenceEquals(scrollView, itemsView.ScrollView)
                || !_policy.Permits(purpose)
                || !TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount))
            {
                DetachRealization();
                return;
            }
            if (++frames > InitialTailConfirmation.MaxObservedFrames)
            {
                DetachRealization();
                OpenClawTray.Services.Logger.Warn("Chat tail realization did not settle.");
                return;
            }
            if (!target.IsLoaded || target.ActualHeight <= 0
                || repeater.GetElementIndex(target) != request.Index
                || !ReferenceEquals(target.DataContext, GetRequestItem(request)))
            {
                settledFrames = 0;
                return;
            }
            var bounds = target.TransformToVisual(scrollView).TransformBounds(
                new global::Windows.Foundation.Rect(0, 0, target.ActualWidth, target.ActualHeight));
            var geometry = (scrollView.ExtentHeight, scrollView.ViewportHeight, bounds.Top, bounds.Height);
            settledFrames = previous == geometry && scrollView.State == ScrollingInteractionState.Idle
                ? settledFrames + 1 : 0;
            previous = geometry;
            if (settledFrames < InitialTailConfirmation.SettledFramesBeforeDecision)
                return;

            DetachRealization();
            if (!itemsView.DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || version != _version || realizationVersion != _realizationVersion || !itemsView.IsLoaded
                    || !_policy.Permits(purpose)
                    || !ReferenceEquals(scrollView, itemsView.ScrollView)
                    || !TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount)
                    || repeater.GetElementIndex(target) != request.Index
                    || !ReferenceEquals(target.DataContext, GetRequestItem(request))
                    || (purpose == ChatScrollAction.ScrollToTail
                        && (_inputScrollController?.IsScrollingWithMouse == true
                            || !_policy.BeginPin(Geometry(scrollView)))))
                    return;
                target.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 1.0,
                });
            }))
                OpenClawTray.Services.Logger.Warn("Chat tail realization could not be dispatched.");
        };
        CompositionTarget.Rendering += _realizing;

        return true;
    }

    private void DetachRealization()
    {
        if (_realizing is not null)
            CompositionTarget.Rendering -= _realizing;
        _realizing = null;
    }

    private void CancelRealization()
    {
        _realizationVersion++;
        DetachRealization();
    }

    private object? GetRequestItem(TailNavigationRequest request) =>
        itemsView.ItemsSource is System.Collections.IList items
        && request.Index >= 0 && request.Index < items.Count
            ? items[request.Index]
            : null;

    private static ChatScrollGeometry Geometry(WinUIScrollView scrollView) =>
        new(scrollView.VerticalOffset, scrollView.ScrollableHeight);

    private ChatScrollGeometry? CurrentGeometry() =>
        _scrollView is { } scrollView ? Geometry(scrollView) : null;

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _version++;
        _tailNavigationQueue.Clear();
        DetachLayout();
        DetachScrollView();
    }

    private void ReleaseOffsetCorrectionAnchor()
    {
        if (_offsetCorrectionAnchorRequested is null)
            return;

        if (_scrollView is not null)
            _scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested;
        _offsetCorrectionAnchorRequested = null;
        ApplyAnchor();
    }

    private void DetachLayout()
    {
        ReleaseOffsetCorrectionAnchor();
        CancelRealization();
        DetachRendering();
        DetachConfirmation();
        _confirmation.Invalidate();
        _awaitingIdleLanding = false;
        _landingPending = false;
        StopLayoutWait();
    }

    private void StopLayoutWait()
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
            if (_offsetCorrectionAnchorRequested is not null)
                _scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested;
            _scrollView.VerticalAnchorRatio = double.NaN;
            _scrollView.ViewChanged -= OnViewChanged;
            _scrollView.StateChanged -= OnScrollStateChanged;
            _scrollView.ExtentChanged -= OnExtentChanged;
            _scrollView.BringingIntoView -= OnBringingIntoView;
            _scrollView.ScrollCompleted -= OnScrollCompleted;
        }

        _scrollView = null;
        _readerBringCorrelationId = null;
        _offsetCorrectionAnchorRequested = null;
        _reportedAtTail = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TailVisibilityChanged = null;
        _version++;
        _tailNavigationQueue.Clear();
        DetachLayout();
        DetachScrollView();
        itemsView.Loaded -= OnLoaded;
        itemsView.Unloaded -= OnUnloaded;
        itemsView.SizeChanged -= OnSizeChanged;
        itemsView.PreviewKeyDown -= OnNavigationKeyDown;
        itemsView.RemoveHandler(UIElement.GettingFocusEvent, _readerGettingFocus);
        itemsView.RemoveHandler(UIElement.PointerPressedEvent, _readerPointerPressed);
        itemsView.RemoveHandler(UIElement.PointerWheelChangedEvent, _readerWheel);
    }
}

internal static class ItemsViewScrollControllerExtensions
{
    public static Element BindVerticalScrollController<T>(
        this ItemsViewElement<T> itemsView,
        ElementRef<WinUIAnnotatedScrollBar> scrollBarRef,
        int initialTailIndex,
        int itemCount,
        int jumpRevision,
        string initialTailRequestKey,
        string? displayedTailKey,
        object contentRevision,
        Action<bool>? tailVisibilityChanged = null) =>
        new ItemsViewVerticalScrollControllerElement(
            itemsView,
            scrollBarRef,
            initialTailIndex,
            itemCount,
            jumpRevision,
            initialTailRequestKey,
            displayedTailKey,
            contentRevision,
            tailVisibilityChanged);
}
