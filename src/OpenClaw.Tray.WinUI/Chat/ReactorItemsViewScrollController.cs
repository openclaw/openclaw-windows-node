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
    int SendRevision,
    string InitialTailRequestKey,
    string? DisplayedTailKey,
    object ContentRevision,
    Action<bool>? TailVisibilityChanged,
    IReadOnlyList<ChatHistoryAnchor> HistoryAnchors) : Element
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
        "ChatTailPositioner", typeof(object), typeof(ItemsViewVerticalScrollControllerHandler), new PropertyMetadata(null));

    public UIElement Mount(MountContext context, ItemsViewVerticalScrollControllerElement element)
    {
        var control = context.MountChild(element.Child);
        if (control is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");

        BindPositioner(itemsView, element, context.BindFor(itemsView, element));
        return itemsView;
    }

    private static InitialTailPositioner BindPositioner(
        WinUIItemsView itemsView,
        ItemsViewVerticalScrollControllerElement element,
        ReactorBinding<ItemsViewVerticalScrollControllerElement> binding)
    {
        (itemsView.GetValue(PositionerProperty) as InitialTailPositioner)?.Dispose();
        var positioner = new InitialTailPositioner(itemsView);
        itemsView.SetValue(PositionerProperty, positioner);
        binding.Reference(
            get: static value => value.ScrollBarRef,
            set: static (value, scrollBar) =>
                (value.GetValue(PositionerProperty) as InitialTailPositioner)?.HistoryNavigation.SetScrollBar(scrollBar));
        positioner.TailVisibilityChanged = element.TailVisibilityChanged;
        positioner.HistoryNavigation.Update(element.HistoryAnchors);
        positioner.UpdateContentRevision(element.ContentRevision);
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
        var previousPositioner = control.GetValue(PositionerProperty) as InitialTailPositioner;
        var updated = context.ReconcileChild(oldElement.Child, newElement.Child, control);
        if (updated is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");
        var positioner = itemsView.GetValue(PositionerProperty) as InitialTailPositioner;
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
            if (!oldElement.HistoryAnchors.SequenceEqual(newElement.HistoryAnchors))
                positioner.HistoryNavigation.Update(newElement.HistoryAnchors);
            positioner.UpdateContentRevision(newElement.ContentRevision);
            if (!string.Equals(oldElement.InitialTailRequestKey, newElement.InitialTailRequestKey, StringComparison.Ordinal))
                positioner.Request(
                    newElement.InitialTailIndex,
                    newElement.ItemCount,
                    newElement.InitialTailRequestKey,
                    newElement.DisplayedTailKey,
                    explicitJump: oldElement.JumpRevision != newElement.JumpRevision);
            else if (oldElement.SendRevision != newElement.SendRevision)
                positioner.FollowSend(
                    newElement.InitialTailIndex,
                    newElement.ItemCount,
                    $"{newElement.InitialTailRequestKey}|send:{newElement.SendRevision}",
                    newElement.DisplayedTailKey);
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
        if (control.GetValue(PositionerProperty) is InitialTailPositioner positioner)
        {
            control.ClearValue(PositionerProperty);
            positioner.Dispose();
        }
        return V1UnmountDisposition.ContinueDefaultTraversal;
    }
}

file sealed class InitialTailPositioner : IDisposable
{
    private const double FollowThreshold = 60;
    private const double OffsetCorrectionAnchorRatio = 0.5;
    // Exactly 1 selects WinUI's extent-edge mode, which can undo an offset as virtual
    // rows remeasure. The nearest interior point anchors content at its bottom instead.
    private static readonly double TailAnchorRatio = Math.BitDecrement(1.0);

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
    private EventHandler<object>? _realizing;
    private int _realizationVersion;
    private readonly InitialTailConfirmation _confirmation = new();
    private readonly InitialTailVerification _verification = new();
    private EventHandler<object>? _verifying;
    private int? _verificationRealization;
    private int? _tailBringCorrelationId;
    private readonly PointerEventHandler _cancelPointerNavigation;
    private readonly PointerEventHandler _readerPointerPressed;
    private readonly TypedEventHandler<UIElement, GettingFocusEventArgs> _readerGettingFocus;
    private bool _disclosureAnchored;
    private bool _readerAnchored;
    private int? _readerBringCorrelationId;
    private int? _tailCorrectionCorrelationId;
    private bool _tailCorrectionPending;
    // Set by a send at the tail until its rows land there. Appending can re-estimate
    // virtual rows above and move the offset before the extent grows; those layout moves
    // must not end follow or reveal the jump control. Reader input still ends it.
    private bool _sendLanding;
    private string? _sendFromTailKey;
    private FrameworkElement? _tailBringTarget;
    private TypedEventHandler<WinUIScrollView, ScrollingAnchorRequestedEventArgs>? _offsetCorrectionAnchorRequested;
    private TypedEventHandler<WinUIScrollView, ScrollingAnchorRequestedEventArgs>? _disclosureAnchorRequested;
    private bool _awaitingIdle;
    private readonly TailReflowCorrection _reflowCorrection = new();
    private Microsoft.UI.Xaml.Controls.Primitives.IScrollController? _inputScrollController;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private double _extentHeight;
    private bool _following;
    private bool _initialPending;
    private bool _explicitJumpPending;
    private bool _awaitingInitialRows;
    private readonly TailNavigationQueue _tailNavigationQueue = new();
    private bool _disposed;
    internal Action<bool>? TailVisibilityChanged { get; set; }
    private bool? _reportedAtTail;
    internal ChatHistoryNavigation HistoryNavigation { get; }
    private TailNavigationRequest? _historyRequest;

    public InitialTailPositioner(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        HistoryNavigation = new ChatHistoryNavigation(itemsView, NavigateHistory);
        _cancelPointerNavigation = (_, _) => OnReaderInput(null, mayToggleDisclosure: false);
        _readerPointerPressed = (_, args) => OnReaderInput(args.OriginalSource, mayToggleDisclosure: true);
        _readerGettingFocus = OnReaderFocus;
        itemsView.AddHandler(UIElement.PointerPressedEvent, _readerPointerPressed, true);
        itemsView.AddHandler(UIElement.PointerWheelChangedEvent, _cancelPointerNavigation, true);
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

        HistoryNavigation.ResetSelection();
        RestoreTailAnchor();
        StopLandingVerification();
        _tailBringCorrelationId = null;
        _historyRequest = null;
        _requestKey = requestKey;
        _version++;
        _reflowCorrection.Clear();
        _tailCorrectionPending = false;
        _tailCorrectionCorrelationId = null;
        _sendLanding = false;
        DetachLayout();
        _tailIndex = tailIndex;
        _itemCount = itemCount;
        _displayedTailKey = displayedTailKey;
        _following = true;
        _initialPending = false;
        _explicitJumpPending = explicitJump;
        _valid = TailNavigationPolicy.TryCapture(tailIndex, displayedTailKey, itemCount, out _);
        _awaitingInitialRows = !_valid;
        if (!_valid)
        {
            _explicitJumpPending = false;
            return;
        }

        _initialPending = true;
        if (itemsView.IsLoaded)
            AwaitLayout(renderAfterLayout: explicitJump);
    }

    // A prompt sent at the tail joins it like any appended row, so follow lands it within a
    // few frames. The initial landing waits for settled frames and is for a send that travels.
    public void FollowSend(int tailIndex, int itemCount, string requestKey, string? displayedTailKey)
    {
        if (!_disposed && _following && _historyRequest is null
            && _scrollView is { IsLoaded: true } scrollView && ReferenceEquals(scrollView, itemsView.ScrollView)
            && IsNearBottom(scrollView))
        {
            if (_initialPending)
            {
                // An opened chat confirms its landing over several settled frames. A prompt sent
                // meanwhile from the idle tail joins it, so restarting navigation would jump away.
                if (_explicitJumpPending || scrollView.State != ScrollingInteractionState.Idle
                    || !IsAtBottom(scrollView))
                {
                    Request(tailIndex, itemCount, requestKey, displayedTailKey, explicitJump: false);
                    return;
                }

                _version++;
                DetachLayout();
                CompleteInitialLanding();
            }

            // The prompt's row can arrive with this update or a later one.
            _sendLanding = true;
            _sendFromTailKey = _displayedTailKey;
            UpdateTail(tailIndex, itemCount, displayedTailKey);
            CorrectSendLanding(scrollView);
            return;
        }

        Request(tailIndex, itemCount, requestKey, displayedTailKey, explicitJump: false);
    }

    private void CorrectSendLanding(WinUIScrollView scrollView)
    {
        // The view can still read as the tail before the prompt's row is laid out, so only a
        // correction dispatched after that layout ends the send.
        if (!_sendLanding || !_following || _tailCorrectionPending || _tailCorrectionCorrelationId is not null
            || (!SendRowArrived && IsAtBottom(scrollView)))
            return;

        _tailCorrectionPending = true;
        AwaitLayout(renderAfterLayout: true);
    }

    private bool SendRowArrived => !string.Equals(_displayedTailKey, _sendFromTailKey, StringComparison.Ordinal);

    private void SettleSendLanding(WinUIScrollView scrollView)
    {
        if (SendRowArrived)
            _sendLanding = false;
        ReportTailVisibility(IsAtBottom(scrollView));
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
            StopLandingVerification();
            if (_initialPending)
            {
                _version++;
                _initialPending = false;
                _explicitJumpPending = false;
                _awaitingInitialRows = true;
                DetachLayout();
            }
            return;
        }

        if (changed)
            _verification.TailGrew();

        // The first rows of a request that started empty take the initial path, so
        // the bring waits for layout and its landing is confirmed. Input clears this.
        if (_awaitingInitialRows)
        {
            _awaitingInitialRows = false;
            _tailNavigationQueue.Clear();
            _initialPending = true;
            if (itemsView.IsLoaded)
                AwaitLayout(renderAfterLayout: _explicitJumpPending);
            return;
        }

        if (changed && itemsView.IsLoaded
            && TailNavigationPolicy.CanFollowTail(_following, _initialPending))
        {
            QueueTailRequest(_version, request);
        }
        if (changed && _scrollView is { } scrollView)
            CorrectSendLanding(scrollView);
    }

    public void UpdateContentRevision(object revision) => _reflowCorrection.ContentChanged(revision);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_valid)
            return;

        _initialPending = true;
        AwaitLayout(renderAfterLayout: _explicitJumpPending);
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
        var version = _version;
        // A Jump request may change no layout property. Drain pending layout, but do
        // not wait forever for a LayoutUpdated event that the stable view will not raise.
        if (_initialPending && !itemsView.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_disposed && version == _version && _awaitingLayout && _initialPending
                    && itemsView.IsLoaded && itemsView.ScrollView is { IsLoaded: true, ViewportHeight: > 0 })
                    CompleteLayoutWait();
            }))
            OpenClawTray.Services.Logger.Warn("Chat tail layout wait could not be dispatched.");
    }

    private void OnScrollViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is WinUIScrollView scrollView)
            scrollView.Loaded -= OnScrollViewLoaded;

        _awaitingScrollView = null;
        AwaitLayout(renderAfterLayout: _explicitJumpPending);
    }

    private void OnLayoutUpdated(object? sender, object args) => CompleteLayoutWait();

    private void CompleteLayoutWait()
    {
        var renderAfterLayout = _renderAfterLayout;
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout(renderAfterLayout);
            return;
        }

        AttachScrollView();
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
            if (_disposed || !_valid || !itemsView.IsLoaded || initialRequest.Version != _version)
                return;

            // This dispatch ends a pending correction unless it has to wait for layout again.
            var correctionPending = renderAfterLayout && _tailCorrectionPending;
            _tailCorrectionPending = false;
            if (!TailNavigationPolicy.CanDispatch(renderAfterLayout, _following))
                return;

            if (!initialRequest.TryResolve(
                    _version, _tailIndex, _displayedTailKey, _itemCount, out var request))
                return;

            if (!renderAfterLayout)
                ConfirmInitialLanding(initialRequest.Version, request);
            else if (_scrollView is { IsLoaded: true } scrollView
                && ReferenceEquals(scrollView, itemsView.ScrollView)
                && TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount))
            {
                if (_explicitJumpPending)
                    ConfirmInitialLanding(initialRequest.Version, request);
                else
                {
                    // Reflow needs an offset correction, not another synchronous item realization.
                    RestoreTailAnchor();
                    // At the tail already, the landing settles now instead of after a correction.
                    if (!IsAtBottom(scrollView))
                    {
                        // Moving the realization window can swap a virtual row's estimate for its real
                        // height. Without a pinned tail that layout shift is lost to the in-flight
                        // correction, which then settles one estimate short of the new tail.
                        PinCorrectionAnchor(scrollView, request);
                        _tailCorrectionCorrelationId = scrollView.ScrollTo(scrollView.HorizontalOffset, scrollView.ScrollableHeight,
                            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                    }
                    else
                        SettleSendLanding(scrollView);
                }
            }
            else
            {
                _tailCorrectionPending = correctionPending;
                AwaitLayout(renderAfterLayout);
            }
        }))
            OpenClawTray.Services.Logger.Warn("Chat tail navigation could not be dispatched.");
    }

    private void PinCorrectionAnchor(WinUIScrollView scrollView, TailNavigationRequest request)
    {
        var targetItem = GetRequestItem(request);
        _offsetCorrectionAnchorRequested = (sender, args) =>
        {
            if (ReferenceEquals(sender, _scrollView)
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
    }

    private void ConfirmInitialLanding(int version, TailNavigationRequest request)
    {
        DetachConfirmation();
        var generation = _confirmation.Reset(GetRequestItem(request));
        if (!StartTailRequest(request))
        {
            _confirmation.Invalidate();
            AwaitLayout(renderAfterLayout: _explicitJumpPending);
            return;
        }

        ObserveInitialLanding(version, generation);
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
                AwaitLayout(renderAfterLayout: _explicitJumpPending);
                return;
            }

            switch (_confirmation.Observe(
                scrollView.State == ScrollingInteractionState.Idle, IsAtBottom(scrollView)))
            {
                case InitialTailLanding.Landed:
                    CompleteInitialLanding();
                    VerifyLanding(version);
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
                _following = true;
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
            else if (!StartTailRequest(request))
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

    // Watches the settled view for one bounded window after a confirmed landing. See InitialTailVerification.
    private void VerifyLanding(int version)
    {
        StopLandingVerification();
        if (_scrollView is not { } ownedScrollView)
            return;

        var generation = _verification.Start();
        _verifying = (_, _) =>
        {
            if (!IsLandingVerificationCurrent(version, generation, ownedScrollView))
            {
                StopLandingVerification();
                return;
            }

            var atBottom = IsAtBottom(ownedScrollView);
            if (atBottom)
                ReachLandingTail();
            switch (_verification.Observe(
                ownedScrollView.State == ScrollingInteractionState.Idle, LandingWorkPending(), _following, atBottom))
            {
                case InitialTailVerdict.Reissue:
                    QueueLandingReissue(version, generation, ownedScrollView);
                    break;
                case InitialTailVerdict.Exhausted:
                    StopLandingVerification();
                    ReportTailVisibility(atBottom);
                    OpenClawTray.Services.Logger.Warn(FormattableString.Invariant(
                        $"Chat initial tail landing did not reach the current tail. itemCount={_itemCount} tailIndex={_tailIndex} offset={ownedScrollView.VerticalOffset} scrollableHeight={ownedScrollView.ScrollableHeight}"));
                    break;
                case InitialTailVerdict.Done:
                    StopLandingVerification();
                    break;
            }
        };
        CompositionTarget.Rendering += _verifying;
    }

    private bool IsLandingVerificationCurrent(int version, int generation, WinUIScrollView ownedScrollView) =>
        !_disposed && version == _version && _valid && itemsView.IsLoaded && !_initialPending && _following
        && _verification.IsCurrent(generation) && ownedScrollView.IsLoaded
        && ReferenceEquals(ownedScrollView, _scrollView) && ReferenceEquals(ownedScrollView, itemsView.ScrollView);

    // Layout, render, confirmation, realization, queued follow work, or a dispatched tail
    // bring that has not completed. The window's frame limit bounds a missing completion.
    private bool LandingWorkPending() =>
        _realizing is not null || _rendering is not null || _confirming is not null
        || _awaitingLayout || _awaitingScrollView is not null || _awaitingIdle
        || _tailNavigationQueue.IsScheduled || _tailBringTarget is not null || _tailBringCorrelationId is not null;

    private void QueueLandingReissue(int version, int generation, WinUIScrollView ownedScrollView)
    {
        // Starting a native bring from Rendering can deadlock the WinUI layout pass.
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_verification.IsCurrent(generation))
                return;

            _verification.ReissueDispatched();
            // Work admitted after the rendering decision keeps its owner; the spent credit is not returned.
            if (IsLandingVerificationCurrent(version, generation, ownedScrollView) && _verification.OwesReissue
                && ownedScrollView.State == ScrollingInteractionState.Idle && !LandingWorkPending()
                && !IsAtBottom(ownedScrollView)
                && TailNavigationPolicy.TryCapture(_tailIndex, _displayedTailKey, _itemCount, out var request)
                && StartTailRequest(request))
                _verificationRealization = _realizationVersion;
        }))
        {
            StopLandingVerification();
            OpenClawTray.Services.Logger.Warn("Chat initial tail verification could not be dispatched.");
        }
    }

    // Ends the window and revokes its queued dispatch and its own unfinished re-issue.
    private void StopLandingVerification()
    {
        _verification.Stop();
        if (_verifying is not null)
            CompositionTarget.Rendering -= _verifying;
        _verifying = null;
        CancelLandingReissue();
    }

    // Arrival at the tail spends the growth and revokes the window's unfinished re-issue,
    // so a reader who then moves slightly away is not pulled back. The window stays open.
    private void ReachLandingTail()
    {
        _verification.ReachedTail();
        CancelLandingReissue();
    }

    private void CancelLandingReissue()
    {
        if (_verificationRealization == _realizationVersion)
            CancelRealization();
        _verificationRealization = null;
    }
    private void CompleteInitialLanding()
    {
        RestoreTailAnchor();
        CancelRealization();
        DetachConfirmation();
        _confirmation.Invalidate();
        _initialPending = false;
        _explicitJumpPending = false;
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
            _scrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
            _extentHeight = _scrollView.ExtentHeight;
            _scrollView.VerticalAnchorRatio = TailAnchorRatio;
            _scrollView.ViewChanged += OnViewChanged;
            _scrollView.StateChanged += OnScrollStateChanged;
            _scrollView.ExtentChanged += OnExtentChanged;
            _scrollView.BringingIntoView += OnBringingIntoView;
            _scrollView.ScrollCompleted += OnScrollCompleted;
            _inputScrollController = itemsView.VerticalScrollController;
            if (_inputScrollController is not null)
                _inputScrollController.IsScrollingWithMouseChanged += OnControllerMouseChanged;
            ReportTailVisibility(IsAtBottom(_scrollView));
            HistoryNavigation.Refresh();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        HistoryNavigation.Refresh();
        if (args.PreviousSize.Width <= 0 || args.PreviousSize.Height <= 0
            || (args.NewSize.Width == args.PreviousSize.Width
                && args.NewSize.Height == args.PreviousSize.Height))
            return;

        _reflowCorrection.Arm();
        if (_scrollView is { } scrollView)
            ObserveGeometry(scrollView);
    }

    private void OnExtentChanged(WinUIScrollView sender, object args)
    {
        HistoryNavigation.Refresh();
        var extentHeight = sender.ExtentHeight;
        if (extentHeight > _extentHeight)
        {
            _verification.TailGrew();
            _reflowCorrection.ArmForExtentGrowth(
                _following, IsAtBottom(sender), _initialPending,
                _disclosureAnchored || _readerAnchored || _readerBringCorrelationId is not null);
        }
        _extentHeight = extentHeight;
        ObserveGeometry(sender);
    }

    private void ObserveGeometry(WinUIScrollView scrollView)
    {
        _confirmation.GeometryChanged();
        _verification.GeometryChanged();
        var atBottom = IsAtBottom(scrollView);
        ReportTailVisibility(atBottom);
        if (_reflowCorrection.TryConsume(_following, atBottom, _initialPending))
        {
            _tailCorrectionPending = true;
            AwaitLayout(renderAfterLayout: true);
        }
    }

    private void OnBringingIntoView(WinUIScrollView sender, ScrollingBringingIntoViewEventArgs args)
    {
        var requested = args.RequestEventArgs?.TargetElement;
        if (requested is not null && ReferenceEquals(requested, _tailBringTarget))
        {
            _tailBringTarget = null;
            _tailBringCorrelationId = args.CorrelationId;
        }
        else if (ReferenceEquals(sender, _scrollView) && !args.Cancel && IsStaleCurrentItemReveal(sender, requested))
        {
            // ItemsView re-reveals its last keyboard-current row without focus when it is resized or
            // shown again (for example when a side panel closes). With focus elsewhere, that would
            // pull the reader away from their place.
            args.Cancel = true;
            return;
        }

        if (!args.Cancel && _historyRequest is { } history && ReferenceEquals(sender, _scrollView)
            && requested is FrameworkElement historyTarget
            && ReferenceEquals(historyTarget.DataContext, GetRequestItem(history)))
        {
            _readerBringCorrelationId = args.CorrelationId;
            _historyRequest = null;
        }

        if (ReferenceEquals(sender, _scrollView) && !_initialPending && !args.Cancel
            && args.RequestEventArgs?.TargetElement is Control { FocusState: FocusState.Keyboard }
            && sender.ScrollableHeight - args.TargetVerticalOffset > FollowThreshold)
        {
            // A real reader destination leaves the tail before the native offset can move.
            // Edge anchoring can otherwise keep remeasuring virtual rows and starve this request.
            _following = false;
            _readerAnchored = true;
            _readerBringCorrelationId = args.CorrelationId;
            sender.VerticalAnchorRatio = 0;
        }

        if (!_initialPending || !ReferenceEquals(sender, _scrollView)
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
        if (ReferenceEquals(sender, _scrollView) && _tailBringCorrelationId == args.CorrelationId)
        {
            _tailBringCorrelationId = null;
            _verification.GeometryChanged();
        }

        if (ReferenceEquals(sender, _scrollView) && _tailCorrectionCorrelationId == args.CorrelationId)
        {
            _tailCorrectionCorrelationId = null;
            if (_offsetCorrectionAnchorRequested is not null)
                RestoreTailAnchor();
            if (_reflowCorrection.ArmAfterLanding(_following, IsAtBottom(sender)))
                ObserveGeometry(sender);
            else
                SettleSendLanding(sender);
        }

        if (ReferenceEquals(sender, _scrollView) && _readerBringCorrelationId == args.CorrelationId)
        {
            _readerBringCorrelationId = null;
            if (IsNearBottom(sender))
            {
                _following = true;
                if (!_disclosureAnchored)
                    RestoreTailAnchor();
            }
        }

        if (_initialPending && ReferenceEquals(sender, _scrollView))
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
            OnReaderInput(null, mayToggleDisclosure: false);
        else if (_awaitingIdle && sender.State == ScrollingInteractionState.Idle)
        {
            _awaitingIdle = false;
            AwaitLayout(renderAfterLayout: true);
        }
    }

    private void OnNavigationKeyDown(object sender, KeyRoutedEventArgs args) =>
        OnReaderInput(
            args.OriginalSource,
            mayToggleDisclosure: args.Key is global::Windows.System.VirtualKey.Enter or global::Windows.System.VirtualKey.Space);

    // Bottom anchoring keeps the tail glued while text streams, but it would push a
    // reader-toggled disclosure header upward as its details grow. Like the web
    // transcript, the toggled header stays put and its details grow downward.
    private void OnReaderInput(object? source, bool mayToggleDisclosure)
    {
        HistoryNavigation.ResetSelection();
        CancelPendingNavigation();
        if (mayToggleDisclosure && _scrollView is { } scrollView && DisclosureHeader(source) is { } header)
        {
            _disclosureAnchored = true;
            scrollView.VerticalAnchorRatio = 0;
            // Top anchoring alone lets the native scroller pin the realized row whose top is
            // nearest the viewport top. For a disclosure inside a tall message that can be the
            // next row, so the details would still grow upward. Pin the toggled header itself.
            if (_disclosureAnchorRequested is not null)
                scrollView.AnchorRequested -= _disclosureAnchorRequested;
            var repeater = scrollView.Content as Microsoft.UI.Xaml.Controls.ItemsRepeater;
            var row = RepeaterRow(repeater, header);
            var rowItem = row?.DataContext;
            _disclosureAnchorRequested = (sender, args) =>
            {
                // A recycled row stays loaded off screen and may later show another message;
                // only pin the header while it still belongs to the toggled message.
                if (ReferenceEquals(sender, _scrollView) && header.IsLoaded && header.Visibility == Visibility.Visible
                    && row is not null && ReferenceEquals(row.DataContext, rowItem)
                    && ReferenceEquals(RepeaterRow(repeater, header), row))
                    args.AnchorElement = header;
            };
            scrollView.AnchorRequested += _disclosureAnchorRequested;
        }
    }

    private static FrameworkElement? RepeaterRow(Microsoft.UI.Xaml.Controls.ItemsRepeater? repeater, DependencyObject element)
    {
        if (repeater is null)
            return null;
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null;
             element = parent, parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, repeater))
                return element as FrameworkElement;
        }

        return null;
    }

    private FrameworkElement? DisclosureHeader(object? source)
    {
        for (var element = source as UIElement;
             element is not null && !ReferenceEquals(element, itemsView);
             element = VisualTreeHelper.GetParent(element) as UIElement)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Name: "ExpanderHeader" } header)
                return header;
        }

        return null;
    }

    private void RestoreTailAnchor()
    {
        _readerBringCorrelationId = null;
        if (!_disclosureAnchored && !_readerAnchored && _offsetCorrectionAnchorRequested is null)
            return;

        _disclosureAnchored = false;
        _readerAnchored = false;
        if (_scrollView is not null)
        {
            if (_offsetCorrectionAnchorRequested is not null)
                _scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested;
            if (_disclosureAnchorRequested is not null)
                _scrollView.AnchorRequested -= _disclosureAnchorRequested;
            _scrollView.VerticalAnchorRatio = TailAnchorRatio;
        }
        _offsetCorrectionAnchorRequested = null;
        _disclosureAnchorRequested = null;
    }

    private void OnReaderFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.FocusState == FocusState.Keyboard)
            CancelPendingNavigation();
    }

    private void OnControllerMouseChanged(
        Microsoft.UI.Xaml.Controls.Primitives.IScrollController sender, object args)
    {
        if (sender.IsScrollingWithMouse)
            CancelPendingNavigation();
    }

    private void CancelPendingNavigation()
    {
        RestoreTailAnchor();
        StopLandingVerification();
        _historyRequest = null;
        _tailCorrectionCorrelationId = null;
        _tailCorrectionPending = false;
        _sendLanding = false;
        _version++;
        _initialPending = false;
        _explicitJumpPending = false;
        _awaitingInitialRows = false;
        // Input without movement (copy, focus, wheel at the tail) keeps follow;
        // input that already left the tail stops it so the reader is not pulled back.
        _following = _scrollView is { } scrollView && IsNearBottom(scrollView);
        ReportTailVisibility(_scrollView is { } currentScrollView && IsAtBottom(currentScrollView));
        _reflowCorrection.Clear();
        _tailNavigationQueue.Clear();
        DetachLayout();
    }

    private void OnViewChanged(WinUIScrollView sender, object args)
    {
        HistoryNavigation.Refresh();
        var correcting = _following && (_tailCorrectionPending || _tailCorrectionCorrelationId is not null || _sendLanding);
        _following = _readerBringCorrelationId is null && IsNearBottom(sender) && _historyRequest is null;
        // A tail correction waits for layout and then lands at an absolute offset, so
        // growth can surface transient views short of the tail meanwhile. The correction's
        // completion, not those views, decides whether follow continues.
        if (correcting && _readerBringCorrelationId is null && _historyRequest is null)
            _following = true;
        else
            _sendLanding = false;
        if (!_following)
            StopLandingVerification();
        else if (IsAtBottom(sender))
            ReachLandingTail();
        ReportTailVisibility(IsAtBottom(sender));
        if (ReferenceEquals(sender, _scrollView))
            CorrectSendLanding(sender);
        _confirmation.GeometryChanged();
        _verification.GeometryChanged();
        if (_initialPending && ReferenceEquals(sender, _scrollView))
        {
            try { _confirmation.ObserveOffset(sender.VerticalOffset); }
            catch (Exception) { }
        }
    }

    private void ReportTailVisibility(bool atTail)
    {
        // A send still landing owns the tail, so the jump control does not flash meanwhile.
        if (!atTail && _sendLanding)
            return;
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
                || !TailNavigationPolicy.CanFollowTail(_following, _initialPending))
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

    private void NavigateHistory(ChatHistoryAnchor anchor)
    {
        CancelPendingNavigation();
        if (!HistoryNavigation.IsCurrent(anchor.RowIndex, anchor.Key))
            return;
        AttachScrollView();
        if (_scrollView is null)
            return;
        _following = false;
        _readerAnchored = true;
        _scrollView.VerticalAnchorRatio = 0;
        _historyRequest = new TailNavigationRequest(anchor.RowIndex, anchor.Key);
        if (!StartTailRequest(_historyRequest.Value, history: true))
        {
            CancelPendingNavigation();
            OpenClawTray.Services.Logger.Warn("Chat history navigation could not start.");
        }
    }

    private bool StartTailRequest(TailNavigationRequest request, bool history = false)
    {
        if (itemsView.ScrollView is not { IsLoaded: true } scrollView)
            return false;

        bool IsCurrent() => history
            ? HistoryNavigation.IsCurrent(request.Index, request.DisplayedTailKey)
                && GetRequestItem(request) is not null
            : TailNavigationPolicy.CanExecute(
                request,
                _tailIndex,
                _displayedTailKey,
                _itemCount);
        if (!IsCurrent())
        {
            return false;
        }

        if (!history)
        {
            _following = true;
            RestoreTailAnchor();
        }
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
        var requiresFollowing = !history && !_initialPending;
        var frames = 0;
        var settledFrames = 0;
        (double Extent, double Viewport, double Top, double Height)? previous = null;
        _realizing = (_, _) =>
        {
            if (_disposed || version != _version || realizationVersion != _realizationVersion || !itemsView.IsLoaded
                || !ReferenceEquals(scrollView, itemsView.ScrollView)
                || !TailNavigationPolicy.CanDispatch(requiresFollowing, _following)
                || !IsCurrent())
            {
                DetachRealization();
                if (history && version == _version)
                    CancelPendingNavigation();
                return;
            }
            if (++frames > InitialTailConfirmation.MaxObservedFrames)
            {
                DetachRealization();
                if (history)
                    CancelPendingNavigation();
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
                    || !TailNavigationPolicy.CanDispatch(requiresFollowing, _following)
                    || !ReferenceEquals(scrollView, itemsView.ScrollView)
                    || !IsCurrent()
                    || repeater.GetElementIndex(target) != request.Index
                    || !ReferenceEquals(target.DataContext, GetRequestItem(request)))
                {
                    if (history && version == _version)
                        CancelPendingNavigation();
                    return;
                }
                _tailBringTarget = target;
                var options = new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 1.0,
                };
                if (history)
                    options.VerticalAlignmentRatio = 0.0;
                target.StartBringIntoView(options);
            }))
            {
                if (history && version == _version)
                    CancelPendingNavigation();
                OpenClawTray.Services.Logger.Warn("Chat tail realization could not be dispatched.");
            }
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

    private bool IsStaleCurrentItemReveal(WinUIScrollView scrollView, UIElement? target) =>
        target is ItemContainer container
        && itemsView is { CurrentItemIndex: >= 0 and var currentIndex }
        && scrollView.Content is ItemsRepeater repeater
        && repeater.GetElementIndex(container) == currentIndex
        && !ContainsFocus(itemsView);

    private static bool ContainsFocus(UIElement container)
    {
        if (container.XamlRoot is not { } root) return false;
        for (var element = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, container)) return true;
        return false;
    }

    private static bool IsNearBottom(WinUIScrollView scrollView) =>
        scrollView.ScrollableHeight - scrollView.VerticalOffset <= FollowThreshold;

    private static bool IsAtBottom(WinUIScrollView scrollView) =>
        TailNavigationPolicy.IsAtBottom(scrollView.VerticalOffset, scrollView.ScrollableHeight);

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _version++;
        _initialPending = false;
        StopLandingVerification();
        _reflowCorrection.Clear();
        _tailCorrectionPending = false;
        _tailNavigationQueue.Clear();
        _tailBringTarget = null;
        DetachLayout();
        DetachScrollView();
    }

    private void DetachLayout()
    {
        if (_offsetCorrectionAnchorRequested is not null)
            RestoreTailAnchor();
        CancelRealization();
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
        StopLandingVerification();
        if (_inputScrollController is not null)
            _inputScrollController.IsScrollingWithMouseChanged -= OnControllerMouseChanged;
        _inputScrollController = null;
        if (_scrollView is not null)
        {
            if (_offsetCorrectionAnchorRequested is not null)
                _scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested;
            if (_disclosureAnchorRequested is not null)
                _scrollView.AnchorRequested -= _disclosureAnchorRequested;
            _scrollView.VerticalAnchorRatio = double.NaN;
            _scrollView.ViewChanged -= OnViewChanged;
            _scrollView.StateChanged -= OnScrollStateChanged;
            _scrollView.ExtentChanged -= OnExtentChanged;
            _scrollView.BringingIntoView -= OnBringingIntoView;
            _scrollView.ScrollCompleted -= OnScrollCompleted;
        }

        _scrollView = null;
        _tailBringCorrelationId = null;
        _disclosureAnchored = false;
        _readerAnchored = false;
        _readerBringCorrelationId = null;
        _tailCorrectionCorrelationId = null;
        _tailCorrectionPending = false;
        _historyRequest = null;
        _offsetCorrectionAnchorRequested = null;
        _disclosureAnchorRequested = null;
        _reportedAtTail = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        HistoryNavigation.Dispose();
        TailVisibilityChanged = null;
        _version++;
        _reflowCorrection.Clear();
        _tailNavigationQueue.Clear();
        _explicitJumpPending = false;
        _tailBringTarget = null;
        StopLandingVerification();
        DetachLayout();
        DetachScrollView();
        itemsView.Loaded -= OnLoaded;
        itemsView.Unloaded -= OnUnloaded;
        itemsView.SizeChanged -= OnSizeChanged;
        itemsView.PreviewKeyDown -= OnNavigationKeyDown;
        itemsView.RemoveHandler(UIElement.GettingFocusEvent, _readerGettingFocus);
        itemsView.RemoveHandler(UIElement.PointerPressedEvent, _readerPointerPressed);
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
        int jumpRevision,
        int sendRevision,
        string initialTailRequestKey,
        string? displayedTailKey,
        object contentRevision,
        Action<bool>? tailVisibilityChanged = null,
        IReadOnlyList<ChatHistoryAnchor>? historyAnchors = null) =>
        new ItemsViewVerticalScrollControllerElement(
            itemsView,
            scrollBarRef,
            initialTailIndex,
            itemCount,
            jumpRevision,
            sendRevision,
            initialTailRequestKey,
            displayedTailKey,
            contentRevision,
            tailVisibilityChanged,
            historyAnchors ?? []);
}
