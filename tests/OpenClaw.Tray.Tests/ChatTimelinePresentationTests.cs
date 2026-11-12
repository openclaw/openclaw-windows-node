using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelinePresentationTests
{
    [Theory]
    [InlineData("Light", "SystemAccentColorDark1")]
    [InlineData("Default", "SystemAccentColorLight2")]
    [InlineData("HighContrast", "SystemColorWindowTextColor")]
    public void SandboxOnBrush_UsesNativeThemeColor(string theme, string resource)
    {
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Themes", "ChatResources.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = document.Descendants().Single(element =>
            element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(x + "Key") == theme);
        var brush = dictionary.Elements().Single(element => (string?)element.Attribute(x + "Key") == "ChatSandboxOnBrush");
        Assert.Equal($"{{ThemeResource {resource}}}", (string?)brush.Attribute("Color"));
    }

    [Fact]
    public void GatewayDashboard_StaysInConnectionCardNotChat()
    {
        var pages = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages");
        var chat = File.ReadAllText(Path.Combine(pages, "ChatPage.xaml"));
        Assert.DoesNotContain("ChatDashboard", chat);
        Assert.DoesNotContain("OnOpenDashboard", File.ReadAllText(Path.Combine(pages, "ChatPage.xaml.cs")));
        var connection = System.Xml.Linq.XDocument.Load(Path.Combine(pages, "ConnectionPage.xaml"));
        var card = connection.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "ConnectionDashboardCard");
        Assert.Contains(card.Descendants(), element => (string?)element.Attribute("Click") == "OnOpenDashboard");
        Assert.Contains(card.Descendants(), element =>
            (string?)element.Attribute("Text") == "Channels, integrations and gateway settings");
    }

    [Fact]
    public void WelcomeSuggestions_UseBorderlessGraySubtleChrome()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatWelcome.cs"));
        Assert.Contains(".BorderThickness(0)", source);
        Assert.Contains("ChatVisuals.ToolbarButtonResources(resources)", source);
        Assert.Contains("Theme.Ref(\"ControlAltFillColorSecondaryBrush\")", source);
        Assert.Contains("!disabled && onSuggestionPicked is not null", source);
        Assert.Contains(".IsEnabled(enabled)", source);
    }

    [Fact]
    public void ReactorTimeline_UsesNonSelectableItemsViewContainersAndAnnotatedScrollBar()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains("ItemsView(", timeline);
        Assert.Contains("ItemContainer(", timeline);
        Assert.Contains("static row => row.Key", timeline);
        Assert.Contains(".WithKey(row.Key)", timeline);
        Assert.Contains("SelectionMode = ItemsViewSelectionMode.None", timeline);
        Assert.Contains("IsItemInvokedEnabled = false", timeline);
        Assert.Contains("itemContainer.IsSelected = false", timeline);
        Assert.Contains("ItemContainerPointerOverBackground", timeline);
        Assert.Contains("ItemContainerPressedBackground", timeline);
        Assert.Contains("ItemContainerSelectedBackground", timeline);
        Assert.Contains("ItemContainerSelectedPointerOverBackground", timeline);
        Assert.Contains("ItemContainerSelectedPressedBackground", timeline);
        Assert.Contains("ItemContainerSelectionVisualPointerOverBackground", timeline);
        Assert.Contains("AnnotatedScrollBar()", timeline);
        Assert.Contains(".BindVerticalScrollController(", timeline);
        Assert.Contains("annotatedScrollBarRef,", timeline);
        Assert.Contains("rows.Count - 1", timeline);
        Assert.Contains("rows.Count,", timeline);
        Assert.Contains("jumpRevision,", timeline);
        Assert.Contains("initialTailRequestKey", timeline);
        Assert.Contains("var displayedTailKey = rows.Count > 0 ? rows[^1].Key : null", timeline);
        Assert.DoesNotContain("ItemsRepeater(", timeline);
        Assert.DoesNotContain("ScrollView(", timeline);
    }

    [Fact]
    public void ReactorTimeline_UsesReactiveAnnotatedScrollBarControllerBinding()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorItemsViewScrollController.cs"));

        Assert.Contains("context.BindFor(itemsView, element)", binding);
        Assert.Contains("binding.Reference(", binding);
        Assert.Contains("VerticalScrollController = scrollBar?.ScrollController", binding);
        Assert.Contains("explicitJump: false", binding);
        Assert.Contains("oldElement.JumpRevision != newElement.JumpRevision", binding);
        Assert.DoesNotContain(".Current", binding);
    }

    [Fact]
    public void ReactorTimeline_UsesStableBottomAnchoringAndDiscreteTailRequests()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorItemsViewScrollController.cs"));

        Assert.Contains("itemsView.Loaded += OnLoaded", binding);
        Assert.Contains("itemsView.LayoutUpdated += OnLayoutUpdated", binding);
        Assert.Contains("itemsView.DispatcherQueue.TryEnqueue", binding);
        Assert.Contains("repeater.GetOrCreateElement(request.Index)", binding);
        Assert.Contains("target.StartBringIntoView(", binding);
        Assert.DoesNotContain("itemsView.StartBringItemIntoView(", binding);
        Assert.Contains("VerticalAlignmentRatio = 1.0", binding);
        Assert.Contains("!string.Equals(_displayedTailKey, displayedTailKey, StringComparison.Ordinal)", binding);
        Assert.Contains("_following = _readerBringCorrelationId is null && IsNearBottom(sender)", binding);
        Assert.Contains("_scrollView.VerticalAnchorRatio = 1.0", binding);
        Assert.Contains("_scrollView.VerticalAnchorRatio = double.NaN", binding);
        Assert.Contains("_tailNavigationQueue.Enqueue(version, request)", binding);
        Assert.Contains("_tailNavigationQueue.TryDequeue(_version, out var queuedRequest)", binding);
        Assert.Contains("_valid = TailNavigationPolicy.TryCapture", binding);
        Assert.Contains("_itemCount = itemCount", binding);
        Assert.Contains("TailNavigationPolicy.CanExecute(", binding);
        Assert.Contains("itemsView.Unloaded += OnUnloaded", binding);
        Assert.Contains("itemsView.Loaded -= OnLoaded", binding);
        Assert.Contains("itemsView.LayoutUpdated -= OnLayoutUpdated", binding);
        Assert.Contains("scrollView.Loaded -= OnScrollViewLoaded", binding);
        Assert.DoesNotContain("ChangeView", binding);
        Assert.DoesNotContain("UpdateLayout", binding);
        Assert.DoesNotContain("TailSettle", binding);
        // One bounded resize correction and one confirmed initial-landing correction.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(binding, @"\.ScrollTo\(").Count);
        var retryStart = binding.IndexOf("private void QueueInitialTailRetry", StringComparison.Ordinal);
        var retryEnd = binding.IndexOf("private void CompleteInitialLanding", retryStart, StringComparison.Ordinal);
        var retry = binding[retryStart..retryEnd];
        Assert.Contains("correctOffset &= _confirmation.CanCorrectOffset(targetItem)", retry);
        Assert.Contains("scrollView.HorizontalOffset, scrollView.ScrollableHeight", retry);
        Assert.Contains("_confirmation.OffsetCorrectionStarted(correlationId)", retry);
        Assert.Contains("scrollView.AnchorRequested += _offsetCorrectionAnchorRequested", retry);
        Assert.True(retry.IndexOf("scrollView.AnchorRequested += _offsetCorrectionAnchorRequested", StringComparison.Ordinal) <
            retry.IndexOf("scrollView.ScrollTo(", StringComparison.Ordinal));
        Assert.DoesNotContain("VerticalAnchorRatio = double.NaN", retry);
        Assert.Contains("args.AnchorElement = target", retry);
        Assert.Contains("repeater.TryGetElement(request.Index)", retry);
        Assert.Contains("ReferenceEquals(target.DataContext, targetItem)", retry);
        Assert.Contains("TailNavigationPolicy.CanExecute(request, _tailIndex, _displayedTailKey, _itemCount)", retry);
        Assert.Contains("IsInitialConfirmationCurrent(version, generation)", retry);
        Assert.Contains("itemsView.DispatcherQueue.TryEnqueue", retry);
        Assert.DoesNotContain("DispatcherTimer", binding);
        Assert.DoesNotContain("TextLength != current.TextLength", binding);
        Assert.DoesNotContain("ReactorStreamingTailState", binding);
        Assert.DoesNotContain("QueueBottomAnchoringUpdate", binding);
        Assert.DoesNotContain("ApplyBottomAnchoring", binding);

        var viewChangedStart = binding.IndexOf("private void OnViewChanged", StringComparison.Ordinal);
        var tailRequestStart = binding.IndexOf("private void QueueTailRequest", viewChangedStart, StringComparison.Ordinal);
        var viewChanged = binding[viewChangedStart..tailRequestStart];
        Assert.DoesNotContain("VerticalAnchorRatio", viewChanged);
        Assert.DoesNotContain("StartBringItemIntoView", viewChanged);
        Assert.DoesNotContain("ScrollTo(", viewChanged);

        var bringStart = binding.IndexOf("private bool StartTailRequest", StringComparison.Ordinal);
        var bringEnd = binding.IndexOf("private object? GetRequestItem", bringStart, StringComparison.Ordinal);
        var bring = binding[bringStart..bringEnd];
        Assert.Contains("repeater.GetOrCreateElement(request.Index)", bring);
        Assert.Contains("target.StartBringIntoView(", bring);
        Assert.DoesNotContain("scrollView.ScrollTo(", bring);

        var completedStart = binding.IndexOf("private void OnScrollCompleted", StringComparison.Ordinal);
        var stateChangedStart = binding.IndexOf("private void OnScrollStateChanged", completedStart, StringComparison.Ordinal);
        Assert.True(completedStart >= 0 && stateChangedStart > completedStart);
        var completed = binding[completedStart..stateChangedStart];
        Assert.DoesNotContain("StartTailRequest(", completed);
        Assert.DoesNotContain("QueueTailRequest(", completed);
        Assert.DoesNotContain("AwaitLayout(", completed);
    }

    [Fact]
    public void ReactorTimeline_BoundedOffsetCorrectionReleasesItsAnchorOnCompletionOrCancellation()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        static string Method(string source, string start, string end) =>
            source[source.IndexOf(start, StringComparison.Ordinal)..source.IndexOf(end, StringComparison.Ordinal)];
        Assert.Contains("RestoreTailAnchor();",
            Method(binding, "private void CompleteInitialLanding(", "private void AttachScrollView()"));
        Assert.Contains("if (_offsetCorrectionAnchorRequested is not null)\n            RestoreTailAnchor();",
            Method(binding.Replace("\r\n", "\n"), "private void DetachLayout()", "private void DetachRendering()"));
        var restore = Method(binding, "private void RestoreTailAnchor()", "private void OnReaderFocus(");
        Assert.Contains("_scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested", restore);
        Assert.Contains("_offsetCorrectionAnchorRequested = null", restore);
        Assert.Contains("_scrollView.VerticalAnchorRatio = 1.0", restore);
        var detach = Method(binding, "private void DetachScrollView()", "public void Dispose()");
        Assert.Contains("_scrollView.AnchorRequested -= _offsetCorrectionAnchorRequested", detach);
        Assert.Contains("_offsetCorrectionAnchorRequested = null", detach);
        Assert.Contains("RestoreTailAnchor();",
            Method(binding, "private void CancelPendingNavigation()", "private void OnViewChanged("));
    }

    [Fact]
    public void ReactorTimeline_KeyboardReaderBringYieldsEdgeAnchorWithoutAddingNavigation()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        var start = binding.IndexOf("private void OnBringingIntoView(", StringComparison.Ordinal);
        var end = binding.IndexOf("private void OnScrollCompleted(", start, StringComparison.Ordinal);
        var bring = binding[start..end];
        Assert.Contains("!_initialPending && !args.Cancel", bring);
        Assert.Contains("Control { FocusState: FocusState.Keyboard }", bring);
        Assert.Contains("sender.ScrollableHeight - args.TargetVerticalOffset > FollowThreshold", bring);
        Assert.True(bring.IndexOf("_following = false;", StringComparison.Ordinal) <
            bring.IndexOf("sender.VerticalAnchorRatio = 0;", StringComparison.Ordinal));
        Assert.Contains("_following = false;", bring);
        Assert.Contains("_readerBringCorrelationId = args.CorrelationId;", bring);
        Assert.DoesNotContain("ScrollTo(", bring);
        Assert.DoesNotContain("StartBringIntoView(", bring);
        Assert.DoesNotContain("GetOrCreateElement(", bring);
        Assert.DoesNotContain("UpdateLayout(", bring);

        var completionEnd = binding.IndexOf("private void OnScrollStateChanged(", end, StringComparison.Ordinal);
        var completion = binding[end..completionEnd];
        Assert.Contains("_readerBringCorrelationId == args.CorrelationId", completion);
        Assert.Contains("if (IsNearBottom(sender))", completion);
        Assert.Contains("RestoreTailAnchor();", completion);
        var detach = binding[binding.IndexOf("private void DetachScrollView()", StringComparison.Ordinal)..
            binding.IndexOf("public void Dispose()", binding.IndexOf("private void DetachScrollView()", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("_readerBringCorrelationId = null;", detach);
    }

    [Fact]
    public void ReactorTimeline_NeverUsesSynchronousItemsViewBring()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));

        Assert.DoesNotContain("StartBringItemIntoView(", binding);
        Assert.DoesNotContain("UpdateLayout(", binding);
        var start = binding.IndexOf("private bool StartTailRequest(", StringComparison.Ordinal);
        var end = binding.IndexOf("private void DetachRealization()", start, StringComparison.Ordinal);
        var bring = binding[start..end];
        var realization = bring.IndexOf("repeater.GetOrCreateElement(request.Index)", StringComparison.Ordinal);
        var rendering = bring.IndexOf("_realizing = (_, _) =>", StringComparison.Ordinal);
        var dispatch = bring.IndexOf("itemsView.DispatcherQueue.TryEnqueue", rendering, StringComparison.Ordinal);
        var navigation = bring.IndexOf("target.StartBringIntoView(", dispatch, StringComparison.Ordinal);
        Assert.True(realization >= 0 && rendering > realization && dispatch > rendering && navigation > dispatch);
        Assert.Contains("InitialTailConfirmation.MaxObservedFrames", bring);
        Assert.Contains("previous == geometry && scrollView.State == ScrollingInteractionState.Idle", bring);
        Assert.Contains("InitialTailConfirmation.SettledFramesBeforeDecision", bring[..dispatch]);
        Assert.Contains("realizationVersion != _realizationVersion", bring[dispatch..navigation]);
        Assert.Contains("repeater.GetElementIndex(target) != request.Index", bring[dispatch..navigation]);
        Assert.Contains("ReferenceEquals(target.DataContext, GetRequestItem(request))", bring[dispatch..navigation]);
        Assert.Contains("CancelRealization();", binding[binding.IndexOf("private void DetachLayout(", StringComparison.Ordinal)..]);
        Assert.Contains("_realizationVersion++;", binding);
        Assert.Contains("CompositionTarget.Rendering -= _realizing", binding);
    }

    [Fact]
    public void ReactorTimeline_InitialTailResolvesLiveCoordinatesAfterLayoutAndDispatch()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorItemsViewScrollController.cs"));

        var requestStart = binding.IndexOf("public void Request(", StringComparison.Ordinal);
        var updateStart = binding.IndexOf("public void UpdateTail(", requestStart, StringComparison.Ordinal);
        Assert.Contains("AwaitLayout(renderAfterLayout: explicitJump)", binding[requestStart..updateStart]);

        var loadStart = binding.IndexOf("private void OnLoaded(", StringComparison.Ordinal);
        var attachStart = binding.IndexOf("private void AttachScrollView(", loadStart, StringComparison.Ordinal);
        var initialPositioning = binding[loadStart..attachStart];
        Assert.Contains("_disposed || !_valid || !itemsView.IsLoaded", initialPositioning);
        Assert.Contains("itemsView.ScrollView is { IsLoaded: false } scrollView", initialPositioning);
        Assert.Contains("scrollView.Loaded += OnScrollViewLoaded", initialPositioning);
        Assert.Contains("itemsView.LayoutUpdated += OnLayoutUpdated", initialPositioning);
        Assert.Contains("DetachLayout();", initialPositioning);
        Assert.Contains("AttachScrollView();", initialPositioning);
        Assert.Contains("initialRequest.TryResolve(", initialPositioning);
        Assert.Contains("StartTailRequest(request)", initialPositioning);
        Assert.Contains("itemsView.DispatcherQueue.TryEnqueue", initialPositioning);
        Assert.Contains("new InitialTailNavigationRequest(_version)", initialPositioning);
        Assert.Contains("!itemsView.IsLoaded || initialRequest.Version != _version", initialPositioning);
        var dispatchStart = initialPositioning.IndexOf("itemsView.DispatcherQueue.TryEnqueue", StringComparison.Ordinal);
        Assert.DoesNotContain("TryCapture(", initialPositioning[..dispatchStart]);
        Assert.DoesNotContain("TryResolve(", initialPositioning[..dispatchStart]);
        Assert.DoesNotContain("_tailIndex", initialPositioning[..dispatchStart]);
        Assert.DoesNotContain("_displayedTailKey", initialPositioning[..dispatchStart]);
        Assert.DoesNotContain("_itemCount", initialPositioning[..dispatchStart]);
        Assert.Contains("initialRequest.TryResolve(", initialPositioning[dispatchStart..]);
        Assert.Contains("CompositionTarget.Rendering += _rendering", initialPositioning);

        var unloadStart = binding.IndexOf("private void OnUnloaded(", StringComparison.Ordinal);
        var detachLayoutStart = binding.IndexOf("private void DetachLayout(", unloadStart, StringComparison.Ordinal);
        Assert.Contains("DetachLayout();", binding[unloadStart..detachLayoutStart]);
        var disposeStart = binding.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.Contains("DetachLayout();", binding[disposeStart..]);
    }

    [Fact]
    public void InitialTailNavigationRequest_ResolvesSameKeyAtCurrentIndexAfterDispatch()
    {
        var initialRequest = new InitialTailNavigationRequest(Version: 1);
        var tailIndex = 1;
        var itemCount = 2;
        const string thinkingKey = "thinking";
        Assert.True(TailNavigationPolicy.TryCapture(tailIndex, thinkingKey, itemCount, out var staleRequest));

        TailNavigationRequest? executed = null;
        var dispatcher = new Queue<Action>();
        dispatcher.Enqueue(() =>
        {
            if (initialRequest.TryResolve(1, tailIndex, thinkingKey, itemCount, out var request)
                && TailNavigationPolicy.CanExecute(request, tailIndex, thinkingKey, itemCount))
                executed = request;
        });

        // A tool row arrives before the same thinking row while initial navigation is queued.
        tailIndex = 2;
        itemCount = 3;
        Assert.False(TailNavigationPolicy.CanExecute(staleRequest, tailIndex, thinkingKey, itemCount));
        Assert.Null(executed);

        dispatcher.Dequeue()();

        Assert.Equal(new TailNavigationRequest(2, thinkingKey), executed);
    }

    [Fact]
    public void InitialTailNavigationRequest_RejectsChangedSessionGeneration()
    {
        var initialRequest = new InitialTailNavigationRequest(Version: 1);

        Assert.False(initialRequest.TryResolve(
            currentVersion: 2,
            currentTailIndex: 0,
            currentDisplayedTailKey: "other-session",
            itemCount: 1,
            out _));
    }

    [Fact]
    public void InitialTailNavigationRequest_RejectsTailThatBecameEmptyBeforeDispatch()
    {
        var initialRequest = new InitialTailNavigationRequest(Version: 1);

        Assert.False(initialRequest.TryResolve(
            currentVersion: 1,
            currentTailIndex: -1,
            currentDisplayedTailKey: null,
            itemCount: 0,
            out _));
    }

    [Fact]
    public void TailNavigationPolicy_RejectsQueuedRequestAfterValidTailBecomesEmpty()
    {
        Assert.True(TailNavigationPolicy.TryCapture(2, "assistant-3", 3, out var queued));
        Assert.False(TailNavigationPolicy.TryCapture(-1, null, 0, out _));

        Assert.False(TailNavigationPolicy.CanExecute(
            queued,
            currentTailIndex: -1,
            currentDisplayedTailKey: null,
            itemCount: 0));
    }

    [Fact]
    public void TailNavigationPolicy_AllowsNewRequestAfterEmptyTailBecomesValid()
    {
        Assert.False(TailNavigationPolicy.TryCapture(-1, null, 0, out _));
        Assert.True(TailNavigationPolicy.TryCapture(0, "assistant-1", 1, out var queued));

        Assert.True(TailNavigationPolicy.CanExecute(
            queued,
            currentTailIndex: 0,
            currentDisplayedTailKey: "assistant-1",
            itemCount: 1));
    }

    [Fact]
    public void TailNavigationPolicy_RejectsStaleIdentityAndOutOfRangeIndex()
    {
        Assert.True(TailNavigationPolicy.TryCapture(1, "assistant-2", 2, out var queued));

        Assert.False(TailNavigationPolicy.CanExecute(
            queued,
            currentTailIndex: 1,
            currentDisplayedTailKey: "assistant-3",
            itemCount: 2));
        Assert.False(TailNavigationPolicy.CanExecute(
            queued,
            currentTailIndex: 1,
            currentDisplayedTailKey: "assistant-2",
            itemCount: 1));
    }

    [Fact]
    public void TailNavigationQueue_OldCallbackConsumesNewestMatchingGeneration()
    {
        var queue = new TailNavigationQueue();
        var first = new TailNavigationRequest(1, "assistant-2");
        var replacement = new TailNavigationRequest(0, "assistant-1");

        Assert.True(queue.Enqueue(version: 1, first));
        queue.Clear();
        Assert.False(queue.Enqueue(version: 2, replacement));

        Assert.True(queue.TryDequeue(currentVersion: 2, out var dequeued));
        Assert.Equal(replacement, dequeued);
        Assert.False(queue.IsScheduled);
    }

    [Fact]
    public void TailNavigationQueue_RejectsPendingRequestFromStaleGeneration()
    {
        var queue = new TailNavigationQueue();
        Assert.True(queue.Enqueue(
            version: 1,
            new TailNavigationRequest(1, "assistant-2")));

        Assert.False(queue.TryDequeue(currentVersion: 2, out _));
        Assert.False(queue.IsScheduled);
    }

    [Fact]
    public void TailReflowCorrection_AllowsOnlyOneCorrectionPerResize()
    {
        var correction = new TailReflowCorrection();

        Assert.False(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
        correction.Arm();
        Assert.True(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
        Assert.False(correction.TryConsume(following: true, nearBottom: false, initialPending: false));

        correction.Arm();
        Assert.True(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
        Assert.False(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
    }

    [Fact]
    public void TailReflowCorrection_DoesNotPullBackAReaderOrConsumeAnUnneededCorrection()
    {
        var correction = new TailReflowCorrection();
        correction.Arm();

        Assert.False(correction.TryConsume(following: false, nearBottom: false, initialPending: false));
        Assert.False(correction.TryConsume(following: true, nearBottom: true, initialPending: false));
        Assert.True(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
    }

    [Fact]
    public void TailReflowCorrection_CancellationRejectsLateExtentChanges()
    {
        var correction = new TailReflowCorrection();
        correction.Arm();
        correction.Clear();

        Assert.False(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
    }

    [Fact]
    public void TailReflowCorrection_DoesNotStartWhileInitialPositioningIsPending()
    {
        var correction = new TailReflowCorrection();
        correction.Arm();

        Assert.False(correction.TryConsume(following: true, nearBottom: false, initialPending: true));
        // The resize budget survives, so reflow after the initial bring lands can still correct once.
        Assert.True(correction.TryConsume(following: true, nearBottom: false, initialPending: false));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void TailNavigationPolicy_OnlyReflowCorrectionRequiresFollowingAtDispatch(
        bool reflowCorrection, bool following, bool expected)
    {
        Assert.Equal(expected, TailNavigationPolicy.CanDispatch(reflowCorrection, following));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void TailNavigationPolicy_FollowCannotInterruptInitialPositioning(
        bool following, bool initialPending, bool expected)
    {
        Assert.Equal(expected, TailNavigationPolicy.CanFollowTail(following, initialPending));
    }

    [Fact]
    public void InitialTailDiagnostics_ReadsDoNotChangeCorrelationSettlingOrGeneration()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        var generation = confirmation.Reset(target);
        Assert.Equal(new InitialTailDiagnosticSnapshot(0, 0, 0, true, false, false, true), confirmation.Diagnostics);
        confirmation.BringingIntoView(17, new object());
        confirmation.ScrollCompleted(17);
        Assert.False(confirmation.Diagnostics.HasCorrelation);
        confirmation.BringingIntoView(18, target);
        confirmation.ScrollCompleted(17);
        Assert.False(confirmation.Diagnostics.BringCompleted);
        confirmation.ScrollCompleted(18);
        for (var frame = 1; frame <= 3; frame++)
        {
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));
            var expected = new InitialTailDiagnosticSnapshot(frame, 0, frame, true, true, true, true);
            Assert.Equal(expected, confirmation.Diagnostics);
            Assert.Equal(expected, confirmation.Diagnostics);
            Assert.True(confirmation.IsCurrent(generation));
        }
        Assert.Equal(InitialTailLanding.Landed, confirmation.Observe(idle: true, nearBottom: true));
        var landed = confirmation.Diagnostics;
        Assert.Equal(new InitialTailDiagnosticSnapshot(4, 0, 4, true, true, true, false), landed);
        confirmation.Invalidate();
        Assert.False(confirmation.IsCurrent(generation));
        Assert.False(confirmation.Diagnostics.HasExpectedTarget);
        Assert.True(landed.HasExpectedTarget); // Value copy survives cleanup without retaining the target.
    }

    [Fact]
    public void InitialTailDiagnostics_ReadsPreserveThreeRetriesAndMissingBringFrameBudget()
    {
        var confirmation = new InitialTailConfirmation();
        confirmation.Reset();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            for (var frame = 1; frame <= 30; frame++)
            {
                _ = confirmation.Diagnostics;
                var decision = confirmation.Observe(idle: true, nearBottom: false);
                Assert.Equal(frame < 30 ? InitialTailLanding.Wait
                    : attempt < 3 ? InitialTailLanding.Retry : InitialTailLanding.GiveUp, decision);
                var snapshot = confirmation.Diagnostics;
                Assert.Equal(attempt * 30 + frame, snapshot.ObservedFrames);
                Assert.Equal(frame == 30 && attempt < 3 ? attempt + 1 : attempt, snapshot.Retries);
                Assert.False(snapshot.HasExpectedTarget);
                Assert.False(snapshot.HasCorrelation);
                Assert.False(snapshot.BringCompleted);
            }
            if (attempt < 3) confirmation.BeginAttempt();
        }
        Assert.Equal(new InitialTailDiagnosticSnapshot(120, 3, 30, false, false, false, false), confirmation.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialTailDiagnostics_ReadsDoNotExtendTheUnsettledOrInFlightBudget(bool inFlight)
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        if (inFlight) confirmation.BringingIntoView(17, target);
        for (var frame = 1; frame <= 120; frame++)
        {
            _ = confirmation.Diagnostics;
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: inFlight, nearBottom: false));
            Assert.Equal(new InitialTailDiagnosticSnapshot(frame, 0, 0, true, inFlight, false, true), confirmation.Diagnostics);
        }
        Assert.Equal(InitialTailLanding.GiveUp, confirmation.Observe(idle: inFlight, nearBottom: false));
        Assert.Equal(new InitialTailDiagnosticSnapshot(121, 0, 0, true, inFlight, false, true), confirmation.Diagnostics);
    }

    [Fact]
    public void InitialTailDiagnostics_ExposeOnlyAllowlistedCountsAndBooleans()
    {
        var fields = typeof(InitialTailDiagnosticSnapshot).GetProperties()
            .ToDictionary(property => property.Name, property => property.PropertyType);
        Assert.Equal(7, fields.Count);
        foreach (var name in new[] { "ObservedFrames", "Retries", "SettledFrames" })
            Assert.Equal(typeof(int), fields[name]);
        foreach (var name in new[] { "HasExpectedTarget", "HasCorrelation", "BringCompleted", "AttemptActive" })
            Assert.Equal(typeof(bool), fields[name]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2410)]
    public void InitialTailAttemptDiagnostics_DistinguishNoOpFromRecoilWithoutDeclaringLanding(bool moved, double expectedMaximum)
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target, (moved ? 2410 : 0, 0, 2410), targetIndex: 29);
        if (moved) confirmation.ObserveOffset(2410);
        confirmation.ScrollCompleted(17, (moved ? 2410 : 0, 2410), target, sourceReadSucceeded: true);
        confirmation.ObserveOffset(0);
        Assert.Equal(new InitialTailAttemptDiagnostics(moved ? 2410 : 0, 0, 2410, 29,
            moved ? 2410 : 0, 2410, true, expectedMaximum), confirmation.AttemptDiagnostics);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));
        confirmation.ObserveOffset(9999); // A finished attempt cannot absorb later movement.
        Assert.Equal(expectedMaximum, confirmation.AttemptDiagnostics.MaxObservedOffset);
    }

    [Fact]
    public void InitialTailAttemptDiagnostics_RejectWrongTargetAndCompletionCorrelation()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, new object(), (200, 100, 200), 9);
        confirmation.BringingIntoView(-1, target, (200, 100, 200), 9);
        confirmation.ObserveOffset(100);
        Assert.Equal(default(InitialTailAttemptDiagnostics), confirmation.AttemptDiagnostics);
        confirmation.BringingIntoView(17, target, (2410, 0, 2410), 29);
        var accepted = confirmation.AttemptDiagnostics;
        confirmation.BringingIntoView(18, new object(), (9999, 9999, 9999), 999);
        confirmation.ScrollCompleted(18, (9999, 9999), target, true);
        Assert.Equal(accepted, confirmation.AttemptDiagnostics);
        Assert.False(confirmation.Diagnostics.BringCompleted);
        confirmation.ScrollCompleted(17, (100, 2410), target, true);
        confirmation.BringingIntoView(19, target, (2500, 100, 2500), 30);
        Assert.Null(confirmation.AttemptDiagnostics.CompletedOffset);
        Assert.Null(confirmation.AttemptDiagnostics.CompletionSourceItemMatches);
        confirmation.ScrollCompleted(17, (9999, 9999), target, true);
        Assert.Null(confirmation.AttemptDiagnostics.CompletedOffset);
        Assert.Equal(100d, confirmation.AttemptDiagnostics.MaxObservedOffset);
    }

    [Fact]
    public void InitialTailAttemptDiagnostics_ResetPerAttemptReplacementAndInvalidation()
    {
        var confirmation = new InitialTailConfirmation();
        var oldTarget = new object();
        var generation = confirmation.Reset(oldTarget);
        confirmation.BringingIntoView(17, oldTarget, (300, 0, 300), 2);
        confirmation.BeginAttempt(oldTarget);
        Assert.True(confirmation.IsCurrent(generation));
        Assert.Equal(default(InitialTailAttemptDiagnostics), confirmation.AttemptDiagnostics);
        confirmation.ScrollCompleted(17, (300, 300), oldTarget, true);
        confirmation.ObserveOffset(300);
        Assert.Equal(default(InitialTailAttemptDiagnostics), confirmation.AttemptDiagnostics);
        var newTarget = new object();
        confirmation.Reset(newTarget);
        Assert.False(confirmation.IsCurrent(generation));
        confirmation.BringingIntoView(17, oldTarget, (300, 0, 300), 2);
        Assert.Equal(default(InitialTailAttemptDiagnostics), confirmation.AttemptDiagnostics);
        confirmation.BringingIntoView(18, newTarget, (500, 0, 500), 4);
        var captured = confirmation.AttemptDiagnostics;
        confirmation.Invalidate();
        confirmation.BringingIntoView(18, newTarget, (500, 500, 500), 4);
        confirmation.ScrollCompleted(18, (500, 500), newTarget, true);
        confirmation.ObserveOffset(500);
        Assert.Equal(default(InitialTailAttemptDiagnostics), confirmation.AttemptDiagnostics);
        Assert.Equal(500d, captured.ProposedOffset); // Value copy survives cleanup.
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void InitialTailAttemptDiagnostics_SeparateUnavailableSourceFromMismatch(bool available, bool matches, bool? expected)
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        confirmation.ScrollCompleted(17, currentSourceItem: matches ? target : new object(), sourceReadSucceeded: available);
        Assert.Equal(expected, confirmation.AttemptDiagnostics.CompletionSourceItemMatches);
        Assert.Null(confirmation.AttemptDiagnostics.CompletedOffset);
        Assert.Null(confirmation.AttemptDiagnostics.TargetIndex);
        Assert.True(confirmation.Diagnostics.BringCompleted);
    }

    [Fact]
    public void InitialTailAttemptDiagnostics_NonFiniteReadsStayUnknownAndDoNotEraseFiniteMaximum()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target, (double.NaN, 0, double.PositiveInfinity), -1);
        confirmation.ObserveOffset(100);
        confirmation.ObserveOffset(double.NaN);
        confirmation.ObserveOffset(double.NegativeInfinity);
        confirmation.ScrollCompleted(17, (double.PositiveInfinity, 500));
        Assert.Equal(new InitialTailAttemptDiagnostics(null, 0, null, -1, null, 500, null, 100), confirmation.AttemptDiagnostics);
    }

    [Fact]
    public void InitialTailAttemptDiagnostics_DoNotChangeThreeRetriesOrFourFrameDecisions()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            confirmation.BringingIntoView(17 + attempt, target, (10000, 10000, 10000), 29);
            confirmation.ScrollCompleted(17 + attempt, (10000, 10000), target, true);
            for (var frame = 0; frame < 3; frame++)
                Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
            Assert.Equal(attempt < 3 ? InitialTailLanding.CorrectOffset : InitialTailLanding.GiveUp,
                confirmation.Observe(idle: true, nearBottom: false));
            if (attempt < 3) confirmation.BeginAttempt(target);
        }
        Assert.Equal(new InitialTailDiagnosticSnapshot(16, 3, 4, true, true, true, false), confirmation.Diagnostics);
    }

    [Fact]
    public void InitialTailAttemptDiagnostics_ContainOnlyNullableNumbersAndBoolean()
    {
        var fields = typeof(InitialTailAttemptDiagnostics).GetProperties()
            .ToDictionary(property => property.Name, property => property.PropertyType);
        Assert.Equal(8, fields.Count);
        foreach (var name in new[] { "ProposedOffset", "BringOffset", "BringScrollableHeight",
            "CompletedOffset", "CompletedScrollableHeight", "MaxObservedOffset" })
            Assert.Equal(typeof(double?), fields[name]);
        Assert.Equal(typeof(int?), fields["TargetIndex"]);
        Assert.Equal(typeof(bool?), fields["CompletionSourceItemMatches"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData(true, null)]
    public void InitialTailBringOptions_KeepUnknownAndCallbackValuesWithoutChangingDecisions(bool? canceled, bool? animated)
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        // Null models either independent native getter failing/unavailable; false is observed.
        confirmation.ObserveBringOptions(17, target, canceled, animated);
        Assert.Equal(new InitialTailBringOptionsObservation(canceled, animated), confirmation.BringOptionsObservation);
        Assert.Equal(new InitialTailDiagnosticSnapshot(0, 0, 0, true, true, false, true), confirmation.Diagnostics);
        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(new InitialTailDiagnosticSnapshot(4, 1, 0, true, true, true, false), confirmation.Diagnostics);
    }

    [Fact]
    public void InitialTailBringOptions_RejectUnacceptedWrongTargetAndSupersededCorrelation()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.ObserveBringOptions(17, target, true, true);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
        confirmation.BringingIntoView(17, target);
        confirmation.ObserveBringOptions(18, target, true, true);
        confirmation.ObserveBringOptions(17, new object(), true, true);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
        confirmation.ObserveBringOptions(17, target, true, false);
        Assert.Equal(new InitialTailBringOptionsObservation(true, false), confirmation.BringOptionsObservation);
        confirmation.BringingIntoView(18, target);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
        confirmation.ObserveBringOptions(17, target, true, true);
        confirmation.ObserveBringOptions(18, target, null, null);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
    }

    [Fact]
    public void InitialTailBringOptions_ResetOnRetryRequestReplacementAndInvalidation()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        var generation = confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        confirmation.ObserveBringOptions(17, target, true, false);
        var captured = confirmation.BringOptionsObservation;
        confirmation.BeginAttempt(target);
        Assert.True(confirmation.IsCurrent(generation));
        confirmation.ObserveBringOptions(17, target, true, true);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
        confirmation.BringingIntoView(18, target);
        confirmation.ObserveBringOptions(18, target, false, true);
        var replacement = new object();
        confirmation.Reset(replacement);
        Assert.False(confirmation.IsCurrent(generation));
        confirmation.ObserveBringOptions(18, target, true, true);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
        confirmation.BringingIntoView(19, replacement);
        confirmation.ObserveBringOptions(19, replacement, false, false);
        confirmation.Invalidate();
        confirmation.ObserveBringOptions(19, replacement, true, true);
        Assert.Equal(default(InitialTailBringOptionsObservation), confirmation.BringOptionsObservation);
        Assert.Equal(new InitialTailBringOptionsObservation(true, false), captured);
    }

    [Fact]
    public void InitialTailBringOptions_ExposeOnlyTwoNullableCallbackBooleans()
    {
        var fields = typeof(InitialTailBringOptionsObservation).GetProperties()
            .ToDictionary(property => property.Name, property => property.PropertyType);
        Assert.Equal(2, fields.Count);
        Assert.Equal(typeof(bool?), fields["CancelAtCallback"]);
        Assert.Equal(typeof(bool?), fields["AnimationDesiredAtCallback"]);
    }

    [Fact]
    public void InitialTailConfirmation_CompletedBringThatDidNotMoveRequestsOffsetCorrection()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target, (2410, 0, 2410), 29);
        confirmation.ScrollCompleted(17, (0, 2410), target, sourceReadSucceeded: true);

        for (var frame = 0; frame < InitialTailConfirmation.SettledFramesBeforeDecision - 1; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));

        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(1, confirmation.Diagnostics.Retries);
    }

    [Fact]
    public void InitialTailConfirmation_OffsetCorrectionRequiresTheSameCompletedTarget()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        var replacement = new object();
        confirmation.Reset(target);
        Assert.False(confirmation.CanCorrectOffset(target));
        confirmation.BringingIntoView(17, target);
        Assert.False(confirmation.CanCorrectOffset(target));
        confirmation.ScrollCompleted(17);
        Assert.False(confirmation.CanCorrectOffset(target));
        for (var frame = 0; frame < InitialTailConfirmation.SettledFramesBeforeDecision; frame++)
            confirmation.Observe(idle: true, nearBottom: false);

        Assert.True(confirmation.CanCorrectOffset(target));
        Assert.False(confirmation.CanCorrectOffset(replacement));
        Assert.False(confirmation.CanCorrectOffset(null));
        confirmation.Invalidate();
        Assert.False(confirmation.CanCorrectOffset(target));
        confirmation.Reset(replacement);
        Assert.False(confirmation.CanCorrectOffset(replacement));
    }

    [Fact]
    public void InitialTailConfirmation_OffsetCorrectionWaitsForItsOwnCompletionAndSettling()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.OffsetCorrectionStarted(18);
        confirmation.BringingIntoView(17, target);
        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 8; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));

        confirmation.ScrollCompleted(18);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));
        Assert.Equal(InitialTailLanding.Landed, confirmation.Observe(idle: true, nearBottom: true));
    }

    [Fact]
    public void InitialTailConfirmation_OffsetCorrectionDoesNotExtendTheRetryBudget()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            confirmation.ScrollCompleted(17 + attempt);
            for (var frame = 0; frame < 3; frame++)
                Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
            Assert.Equal(attempt < 3 ? InitialTailLanding.CorrectOffset : InitialTailLanding.GiveUp,
                confirmation.Observe(idle: true, nearBottom: false));
            if (attempt < 3)
            {
                confirmation.BeginAttempt(target);
                confirmation.OffsetCorrectionStarted(18 + attempt);
            }
        }

        Assert.Equal(3, confirmation.Diagnostics.Retries);
        Assert.Equal(16, confirmation.Diagnostics.ObservedFrames);
    }

    [Fact]
    public void InitialTailConfirmation_LandsOnlyAfterSettledFramesAtTheTail()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        confirmation.ScrollCompleted(17);

        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));
        Assert.Equal(InitialTailLanding.Landed, confirmation.Observe(idle: true, nearBottom: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialTailConfirmation_DoesNotInterruptAnIdleButInFlightBring(bool nearBottom)
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);

        for (var frame = 0; frame < 20; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom));

        confirmation.ScrollCompleted(18);
        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom));

        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom));
        Assert.Equal(nearBottom ? InitialTailLanding.Landed : InitialTailLanding.CorrectOffset,
            confirmation.Observe(idle: true, nearBottom));
    }

    [Fact]
    public void InitialTailConfirmation_WaitsForTheLatestBringCorrelation()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        confirmation.BringingIntoView(18, target);
        confirmation.ScrollCompleted(17);

        for (var frame = 0; frame < 8; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));

        confirmation.ScrollCompleted(18);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));
    }

    [Fact]
    public void InitialTailConfirmation_WaitsWhileScrollingAndRestartsSettling()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        confirmation.ScrollCompleted(17);

        Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: false, nearBottom: false));
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));
    }

    [Fact]
    public void InitialTailConfirmation_RestartsSettlingWhenGeometryChanges()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);
        confirmation.ScrollCompleted(17);

        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        confirmation.GeometryChanged();
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));
    }

    [Fact]
    public void InitialTailConfirmation_TracksTheDispatchedItemWhenTheLiveTailChanges()
    {
        var confirmation = new InitialTailConfirmation();
        var original = new object();
        var appended = new object();
        confirmation.Reset(original);
        confirmation.BringingIntoView(18, appended);
        confirmation.ScrollCompleted(18);
        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));

        confirmation.BringingIntoView(17, original);
        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.CorrectOffset, confirmation.Observe(idle: true, nearBottom: false));

        confirmation.BeginAttempt(appended);
        confirmation.BringingIntoView(17, original);
        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        confirmation.BringingIntoView(18, appended);
        confirmation.ScrollCompleted(18);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));
        Assert.Equal(InitialTailLanding.Landed, confirmation.Observe(idle: true, nearBottom: true));
    }

    [Fact]
    public void InitialTailConfirmation_RetriesADroppedBringThenGivesUp()
    {
        var confirmation = new InitialTailConfirmation();
        confirmation.Reset();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            for (var frame = 0; frame < 29; frame++)
                Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
            Assert.Equal(attempt < 3 ? InitialTailLanding.Retry : InitialTailLanding.GiveUp,
                confirmation.Observe(idle: true, nearBottom: false));
            if (attempt < 3)
                confirmation.BeginAttempt();
        }

        confirmation.Reset();
        for (var frame = 0; frame < 29; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));
        Assert.Equal(InitialTailLanding.Landed, confirmation.Observe(idle: true, nearBottom: true));
    }

    [Fact]
    public void InitialTailConfirmation_NewRequestAndCancellationInvalidateDeferredRetry()
    {
        var confirmation = new InitialTailConfirmation();
        var original = confirmation.Reset();
        Assert.True(confirmation.IsCurrent(original));

        var replacement = confirmation.Reset();
        Assert.False(confirmation.IsCurrent(original));
        Assert.True(confirmation.IsCurrent(replacement));

        confirmation.Invalidate();
        Assert.False(confirmation.IsCurrent(replacement));
    }

    [Fact]
    public void InitialTailConfirmation_RejectsEventsFromAReplacedRequest()
    {
        var confirmation = new InitialTailConfirmation();
        var oldTarget = new object();
        var newTarget = new object();
        confirmation.Reset(oldTarget);
        confirmation.BringingIntoView(17, oldTarget);

        confirmation.Reset(newTarget);
        confirmation.BringingIntoView(17, oldTarget);
        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));

        confirmation.BringingIntoView(18, newTarget);
        confirmation.ScrollCompleted(17);
        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        confirmation.ScrollCompleted(18);
        for (var frame = 0; frame < 3; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: true));
        Assert.Equal(InitialTailLanding.Landed, confirmation.Observe(idle: true, nearBottom: true));
    }

    [Fact]
    public void ReactorTimeline_InitialTailStaysPendingUntilLandingIsConfirmed()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));

        var dispatchStart = binding.IndexOf("private void DispatchTailRequest", StringComparison.Ordinal);
        var confirmStart = binding.IndexOf("private void ConfirmInitialLanding", dispatchStart, StringComparison.Ordinal);
        Assert.True(confirmStart > dispatchStart);
        var dispatch = binding[dispatchStart..confirmStart];
        Assert.Contains("ConfirmInitialLanding(initialRequest.Version, request)", dispatch);
        Assert.DoesNotContain("_initialPending = false", dispatch);

        var attachStart = binding.IndexOf("private void AttachScrollView", confirmStart, StringComparison.Ordinal);
        var confirm = binding[confirmStart..attachStart];
        Assert.Contains("version == _version", confirm);
        Assert.Contains("_confirmation.IsCurrent(generation)", confirm);
        Assert.Contains("CompositionTarget.Rendering += _confirming", confirm);
        Assert.Contains("case InitialTailLanding.Landed:", confirm);
        Assert.Contains("QueueInitialTailRetry(version, generation)", confirm);
        Assert.Contains("StartTailRequest(request)", confirm);

        var detachLayoutStart = binding.IndexOf("private void DetachLayout(", StringComparison.Ordinal);
        var detachRenderingStart = binding.IndexOf("private void DetachRendering(", detachLayoutStart, StringComparison.Ordinal);
        Assert.Contains("DetachConfirmation();", binding[detachLayoutStart..detachRenderingStart]);
        Assert.Contains("_confirmation.Invalidate();", binding[detachLayoutStart..detachRenderingStart]);
        Assert.Contains("CompositionTarget.Rendering -= _confirming", binding);

        var updateStart = binding.IndexOf("public void UpdateTail(", StringComparison.Ordinal);
        var loadedStart = binding.IndexOf("private void OnLoaded(", updateStart, StringComparison.Ordinal);
        var update = binding[updateStart..loadedStart];
        var lateRows = update.IndexOf("if (_awaitingInitialRows)", StringComparison.Ordinal);
        Assert.True(lateRows > 0);
        Assert.True(lateRows < update.IndexOf("QueueTailRequest(", StringComparison.Ordinal));
        Assert.Contains("_awaitingInitialRows = false;", update[lateRows..]);
        Assert.Contains("AwaitLayout(renderAfterLayout: _explicitJumpPending)", update[lateRows..]);

        var cancelStart = binding.IndexOf("private void CancelPendingNavigation", StringComparison.Ordinal);
        var cancelEnd = binding.IndexOf("private void OnViewChanged", cancelStart, StringComparison.Ordinal);
        Assert.Contains("_awaitingInitialRows = false;", binding[cancelStart..cancelEnd]);
    }

    [Fact]
    public void ReactorTimeline_DoesNotStartATailRetryDuringRendering()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));

        var confirmStart = binding.IndexOf("private void ConfirmInitialLanding(", StringComparison.Ordinal);
        var handlerStart = binding.IndexOf("_confirming = (_, _) =>", confirmStart, StringComparison.Ordinal);
        var handlerEnd = binding.IndexOf("CompositionTarget.Rendering += _confirming", handlerStart, StringComparison.Ordinal);
        Assert.True(confirmStart >= 0 && handlerStart > confirmStart && handlerEnd > handlerStart);
        var handler = binding[handlerStart..handlerEnd];
        Assert.DoesNotContain("StartTailRequest(", handler);
        Assert.Contains("QueueInitialTailRetry(version, generation);", handler);

        var retryStart = binding.IndexOf("private void QueueInitialTailRetry(", handlerEnd, StringComparison.Ordinal);
        var completeStart = binding.IndexOf("private void CompleteInitialLanding(", retryStart, StringComparison.Ordinal);
        Assert.True(retryStart > handlerEnd && completeStart > retryStart);
        var retry = binding[retryStart..completeStart];
        var bring = retry.IndexOf("StartTailRequest(request)", StringComparison.Ordinal);
        Assert.True(bring > 0);
        Assert.Contains("itemsView.DispatcherQueue.TryEnqueue", retry[..bring]);
        Assert.Contains("IsInitialConfirmationCurrent(version, generation)", retry[..bring]);
        Assert.Contains("TailNavigationPolicy.TryCapture(", retry[..bring]);
        Assert.Contains("ObserveInitialLanding(version, generation);", retry[bring..]);
    }

    [Fact]
    public void ReactorTimeline_UnloadKeepsInitialRowsPendingForLateHistory()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));

        var unloadStart = binding.IndexOf("private void OnUnloaded(", StringComparison.Ordinal);
        var detachLayoutStart = binding.IndexOf("private void DetachLayout(", unloadStart, StringComparison.Ordinal);
        Assert.True(detachLayoutStart > unloadStart);
        Assert.DoesNotContain("_awaitingInitialRows = false;", binding[unloadStart..detachLayoutStart]);
    }

    [Fact]
    public void ReactorTimeline_ClearingRowsDuringInitialPositioningRearmsWhenRowsReturn()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        var updateStart = binding.IndexOf("public void UpdateTail(", StringComparison.Ordinal);
        var invalidStart = binding.IndexOf("if (!_valid)", updateStart, StringComparison.Ordinal);
        var lateRowsStart = binding.IndexOf("if (_awaitingInitialRows)", invalidStart, StringComparison.Ordinal);
        Assert.True(updateStart >= 0 && invalidStart > updateStart && lateRowsStart > invalidStart);
        var invalid = binding[invalidStart..lateRowsStart];
        Assert.Contains("if (_initialPending)", invalid);
        Assert.Contains("_version++;", invalid);
        Assert.Contains("_initialPending = false;", invalid);
        Assert.Contains("_awaitingInitialRows = true;", invalid);
        Assert.Contains("DetachLayout();", invalid);
    }

    [Fact]
    public void ReactorTimeline_RearmsInitialPositioningWhenTheScrollViewIsReplaced()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        var handlerStart = binding.IndexOf("_confirming = (_, _) =>", StringComparison.Ordinal);
        var handlerEnd = binding.IndexOf("CompositionTarget.Rendering += _confirming", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        var handler = binding[handlerStart..handlerEnd];
        Assert.Contains("!ReferenceEquals(scrollView, itemsView.ScrollView)", handler);
        Assert.Contains("_confirmation.Invalidate();", handler);
        Assert.Contains("AwaitLayout(renderAfterLayout: _explicitJumpPending)", handler);
    }

    [Fact]
    public void InitialTailConfirmation_StopsObservingAViewThatNeverSettles()
    {
        var confirmation = new InitialTailConfirmation();
        confirmation.Reset();

        for (var frame = 0; frame < InitialTailConfirmation.MaxObservedFrames; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: false, nearBottom: false));
        Assert.Equal(InitialTailLanding.GiveUp, confirmation.Observe(idle: false, nearBottom: false));
    }

    [Fact]
    public void InitialTailConfirmation_TimesOutWithoutInterruptingAnUnfinishedBring()
    {
        var confirmation = new InitialTailConfirmation();
        var target = new object();
        confirmation.Reset(target);
        confirmation.BringingIntoView(17, target);

        for (var frame = 0; frame < 120; frame++)
            Assert.Equal(InitialTailLanding.Wait, confirmation.Observe(idle: true, nearBottom: false));
        Assert.Equal(InitialTailLanding.GiveUp, confirmation.Observe(idle: true, nearBottom: false));
    }

    [Fact]
    public void ReactorTimeline_TracksOnlyTheCurrentTailBringAndDetachesNativeEvents()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));

        Assert.Contains("_scrollView.BringingIntoView += OnBringingIntoView", binding);
        Assert.Contains("_scrollView.BringingIntoView -= OnBringingIntoView", binding);
        Assert.Contains("_scrollView.ScrollCompleted += OnScrollCompleted", binding);
        Assert.Contains("_scrollView.ScrollCompleted -= OnScrollCompleted", binding);
        Assert.Contains("ReferenceEquals(sender, _scrollView)", binding);
        Assert.Contains("_confirmation.Reset(GetRequestItem(request))", binding);
        Assert.Contains("var targetItem = GetRequestItem(request)", binding);
        Assert.Contains("_confirmation.CanCorrectOffset(targetItem)", binding);
        Assert.Contains("_confirmation.BeginAttempt(targetItem)", binding);
        var bringStart = binding.IndexOf("private void OnBringingIntoView(", StringComparison.Ordinal);
        var completeStart = binding.IndexOf("private void OnScrollCompleted(", bringStart, StringComparison.Ordinal);
        var nextHandler = binding.IndexOf("private void OnScrollStateChanged(", completeStart, StringComparison.Ordinal);
        var bring = binding[bringStart..completeStart];
        var complete = binding[completeStart..nextHandler];
        Assert.Contains("!ReferenceEquals(sender, _scrollView)", bring);
        Assert.Contains("var correlationId = args.CorrelationId;", bring);
        Assert.Contains("var targetItem = target.DataContext;", bring);
        Assert.Contains("_confirmation.BringingIntoView(correlationId, targetItem, geometry, targetIndex)", bring);
        Assert.Contains("_initialPending && ReferenceEquals(sender, _scrollView)", complete);
        Assert.Contains("var correlationId = args.CorrelationId;", complete);
        Assert.Contains("_confirmation.ScrollCompleted(correlationId, geometry, currentSourceItem, sourceReadSucceeded)", complete);
        Assert.DoesNotContain("GetOrCreateElement(", bring + complete);
        Assert.DoesNotContain("UpdateLayout(", bring + complete);
    }

    [Fact]
    public void ReactorTimeline_ReflowNavigationDetachesRenderingAndInputSubscriptions()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));

        Assert.Contains("CompositionTarget.Rendering += _rendering", binding);
        Assert.Contains("CompositionTarget.Rendering -= _rendering", binding);
        Assert.Contains("itemsView.SizeChanged += OnSizeChanged", binding);
        Assert.Contains("itemsView.SizeChanged -= OnSizeChanged", binding);
        Assert.Contains("_scrollView.ExtentChanged += OnExtentChanged", binding);
        Assert.Contains("_scrollView.ExtentChanged -= OnExtentChanged", binding);
        Assert.Contains("itemsView.PreviewKeyDown += OnNavigationKeyDown", binding);
        Assert.Contains("itemsView.PreviewKeyDown -= OnNavigationKeyDown", binding);
        Assert.Contains("itemsView.AddHandler(UIElement.PointerWheelChangedEvent", binding);
        Assert.Contains("itemsView.RemoveHandler(UIElement.PointerWheelChangedEvent", binding);
    }

    [Fact]
    public void ReactorTimeline_ReaderToggledDisclosureGrowsDownwardThenRestoresTailAnchor()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        string Slice(string start, string end)
        {
            var from = binding.IndexOf(start, StringComparison.Ordinal);
            var to = binding.IndexOf(end, from, StringComparison.Ordinal);
            Assert.True(from >= 0 && to > from, $"Missing {start}..{end}");
            return binding[from..to];
        }

        Assert.Contains("itemsView.AddHandler(UIElement.PointerPressedEvent, _readerPointerPressed, true)", binding);
        Assert.Contains("itemsView.RemoveHandler(UIElement.PointerPressedEvent, _readerPointerPressed)", binding);
        Assert.Contains("global::Windows.System.VirtualKey.Enter or global::Windows.System.VirtualKey.Space", binding);

        // Disclosure growth keeps its header in place, independently of native reader navigation.
        var input = Slice("private void OnReaderInput", "private bool IsDisclosureHeader");
        Assert.True(input.IndexOf("CancelPendingNavigation();", StringComparison.Ordinal)
            < input.IndexOf("scrollView.VerticalAnchorRatio = 0;", StringComparison.Ordinal));
        Assert.Contains("ToggleButton { Name: \"ExpanderHeader\" }", Slice("private bool IsDisclosureHeader", "private void RestoreTailAnchor"));

        // Tail dispatch and ordinary no-motion input restore bottom anchoring.
        Assert.Contains("_scrollView.VerticalAnchorRatio = 1.0", Slice("private void RestoreTailAnchor", "private void OnReaderFocus"));
        Assert.Contains("RestoreTailAnchor();", Slice("private void CancelPendingNavigation", "_version++"));
        Assert.Contains("RestoreTailAnchor();", Slice("private bool StartTailRequest", "private object? GetRequestItem"));
        Assert.Contains("RestoreTailAnchor();", Slice("// Reflow needs an offset correction", "scrollView.ScrollTo("));
        Assert.Contains("_readerAnchored = false;", Slice("private void DetachScrollView", "public void Dispose"));
        Assert.Contains("_disclosureAnchored = false;", Slice("private void DetachScrollView", "public void Dispose"));
    }

    [Fact]
    public void ToolRowExpanders_StayBorderlessInEveryHeaderState()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var visuals = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Chat", "ChatVisuals.cs"));
        var resourcesStart = visuals.IndexOf("internal static void ToolRowExpanderResources", StringComparison.Ordinal);
        var resources = visuals[resourcesStart..visuals.IndexOf("internal static void StylePicker", resourcesStart, StringComparison.Ordinal)];
        foreach (var key in new[]
                 {
                     "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderPointerOverBrush",
                     "ExpanderHeaderBorderPressedBrush", "ExpanderHeaderDisabledBorderBrush",
                 })
        {
            Assert.Contains($"\"{key}\"", resources);
        }

        // The content border is a TemplateBinding to Expander.BorderBrush, not a theme key.
        var renderer = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Chat", "ToolCallCardRenderer.cs"));
        Assert.Equal(2, CountOccurrences(
            renderer,
            ".BorderBrush(Theme.Ref(\"SubtleFillColorTransparentBrush\"))\n            .Resources(ChatVisuals.ToolRowExpanderResources)"));
        // Expanded details have no card fill; nested calls hang off a thin guide line.
        Assert.Equal(2, CountOccurrences(
            renderer,
            ".Background(Theme.Ref(\"SubtleFillColorTransparentBrush\"))\n            .BorderBrush(Theme.Ref(\"SubtleFillColorTransparentBrush\"))"));
        Assert.Equal(1, CountOccurrences(
            renderer,
            ".Background(Theme.Ref(\"SubtleFillColorTransparentBrush\"))\n                .BorderBrush(Theme.Ref(\"SubtleFillColorTransparentBrush\"))"));
        Assert.Contains("WithKey(\"nested-guide\")", renderer);
        // Rows start on the prose edge: every tool Expander drops the built-in header inset.
        Assert.Equal(3, CountOccurrences(renderer, "ChatVisuals.FlushToolRowExpander"));
        Assert.Contains("header.Padding = new Thickness(0);", visuals);
        Assert.Contains("expander.Resources[\"ExpanderChevronMargin\"] = new Thickness(0);", visuals);
        Assert.Contains("Border(Empty()).Width(1).Margin(7, 0, 16, 0)", renderer);

        // Web tool card: transparent sections inside one thin-bordered, transparent
        // card with the settled outcome ("Completed"/"failed") under a divider.
        var detailStart = renderer.IndexOf("private static Element BuildDetailSection", StringComparison.Ordinal);
        var detail = renderer[detailStart..renderer.IndexOf("private static string FormatToolDisplayArgs", detailStart, StringComparison.Ordinal)];
        Assert.DoesNotContain(".BorderThickness(", detail);
        Assert.DoesNotContain(".Background(", detail);
        Assert.Contains(".AutomationName(label)", detail);
        var cardStart = renderer.IndexOf("var outcome = entry.ToolResult switch", StringComparison.Ordinal);
        var card = renderer[cardStart..renderer.IndexOf(".WithKey(\"details-card\")", cardStart, StringComparison.Ordinal)];
        Assert.Contains("\"Chat_Tool_Completed\", \"Completed\"", card);
        Assert.Contains("\"Chat_Tool_FailedInline\", \"failed\"", card);
        Assert.Contains(".HAlign(HorizontalAlignment.Right)", card);
        Assert.Contains(".BorderThickness(1)", card);
        Assert.Contains(".BorderBrush(Theme.Ref(\"ChatStrokeBrush\"))", card);
        Assert.Contains(".Background(Theme.Ref(\"SubtleFillColorTransparentBrush\"))", card);
        Assert.DoesNotContain(".ToolTip(", renderer);
    }

    [Fact]
    public void ActiveWorkLabels_UseTheWebTextShimmerOnlyWhileRunning()
    {
        var chat = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat");
        var visuals = File.ReadAllText(Path.Combine(chat, "ChatVisuals.cs"));
        var shimmerStart = visuals.IndexOf("internal static void TextShimmer", StringComparison.Ordinal);
        var shimmer = visuals[shimmerStart..visuals.IndexOf("private static void OnShimmerUnloaded", shimmerStart, StringComparison.Ordinal)];
        // Windows animation preference and high contrast keep static text, like the web's media gates.
        Assert.Contains("UISettings().AnimationsEnabled", shimmer);
        Assert.Contains("AccessibilitySettings().HighContrast", shimmer);
        Assert.Contains("RepeatBehavior.Forever", shimmer);
        Assert.Contains("TimeSpan.FromMilliseconds(TextShimmerDurationMilliseconds)", shimmer);
        Assert.Contains("TextShimmerDurationMilliseconds = 2400", visuals);
        Assert.Contains("text.Unloaded += OnShimmerUnloaded", shimmer);
        Assert.Contains(".OnUnmount(control => TextShimmer((TextBlock)control, false))", visuals);

        var renderer = File.ReadAllText(Path.Combine(chat, "ToolCallCardRenderer.cs"));
        Assert.Contains(".WithTextShimmer(entry.ToolResult is null or ChatToolCallStatus.InProgress)", renderer);
        Assert.Contains(".WithTextShimmer(summary.IsRunning)", renderer);
        Assert.Contains(".OnUnmount(ChatVisuals.StopToolRowShimmers)", renderer);
        var progress = File.ReadAllText(Path.Combine(chat, "ReactorRunProgress.cs"));
        Assert.DoesNotContain("TextShimmer", progress);
        var timeline = File.ReadAllText(Path.Combine(chat, "ReactorChatTimeline.cs"));
        Assert.Contains(".WithTextShimmer(entry.IsStreaming)", timeline);
    }

    private static int CountOccurrences(string text, string normalizedValue)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var count = 0;
        for (var index = normalized.IndexOf(normalizedValue, StringComparison.Ordinal);
             index >= 0;
             index = normalized.IndexOf(normalizedValue, index + normalizedValue.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void ReactorTimeline_ReflowDispatchRechecksFollowEligibility()
    {
        var binding = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        var dispatchStart = binding.IndexOf("private void DispatchTailRequest", StringComparison.Ordinal);
        var attachStart = binding.IndexOf("private void AttachScrollView", dispatchStart, StringComparison.Ordinal);
        var dispatch = binding[dispatchStart..attachStart];
        Assert.Contains("TailNavigationPolicy.CanDispatch(renderAfterLayout, _following)", dispatch);
        Assert.Contains("if (_explicitJumpPending)", dispatch);
        Assert.Contains("ConfirmInitialLanding(initialRequest.Version, request)", dispatch);
        var confirmationStart = dispatch.IndexOf("private void ConfirmInitialLanding", StringComparison.Ordinal);
        Assert.DoesNotContain("StartTailRequest(", dispatch[..confirmationStart]);
        Assert.Contains("scrollView.ScrollTo(scrollView.HorizontalOffset, scrollView.ScrollableHeight", dispatch[..confirmationStart]);
        Assert.Contains("var atBottom = IsAtBottom(scrollView);", binding);
        Assert.Contains("TryConsume(_following, atBottom, _initialPending)", binding);

        var cancelStart = binding.IndexOf("private void CancelPendingNavigation", StringComparison.Ordinal);
        var cancelEnd = binding.IndexOf("private void OnViewChanged", cancelStart, StringComparison.Ordinal);
        var cancel = binding[cancelStart..cancelEnd];
        Assert.Contains("_following = _scrollView is { } scrollView && IsNearBottom(scrollView);", cancel);
        Assert.DoesNotContain("_following = false;", cancel);
    }

    [Fact]
    public void ReactorTimeline_RequeuesOnlyForCompletedHistoryReplacement()
    {
        var provider = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawChatDataProvider.cs"));
        var state = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ChatConversationState.cs"));
        var historyState = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ChatHistoryState.cs"));
        var projector = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ChatSnapshotProjector.cs"));
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains("_revisions[token.ThreadId]", historyState);
        Assert.Contains("new Dictionary<string, long>(_revisions)", historyState);
        Assert.Contains("_history.SnapshotRevisions()", state);
        Assert.Contains("_historyLoader.LoadAsync(", provider);
        Assert.Contains("HistoryRevisions: input.HistoryRevisions", projector);
        Assert.Contains("snapshot.HistoryRevisions", root);
        Assert.Contains("HistoryRevision: historyRevision", root);
        Assert.Contains("props.HistoryRevision", timeline);
        Assert.DoesNotContain("|{props.Mode}", timeline);
    }

    [Fact]
    public void ReactorComposer_GatesClickableControlsUntilLayoutIsUsable()
    {
        var composer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatComposer.cs"));

        Assert.Contains("internal static class ComposerAutomationVisibility", composer);
        Assert.Contains("control.IsHitTestVisible = false;", composer);
        Assert.Contains("control.IsLoaded", composer);
        Assert.Contains("control.ActualWidth > 0", composer);
        Assert.Contains("control.ActualHeight > 0", composer);
        Assert.Contains("AccessibilityView.Raw", composer);
        Assert.Contains("AccessibilityView.Control", composer);
        Assert.True(
            composer.Split("AccessibilityView.Raw", StringSplitOptions.None).Length - 1 >= 4);
        Assert.Contains(".AutomationId(\"ChatComposerInput\")", composer);
        Assert.Contains(".AutomationId(automationId)", composer);
        Assert.Contains("RaisePropertyChangedEvent(", composer);
        Assert.Contains("AutomationElementIdentifiers.IsOffscreenProperty", composer);
        Assert.Equal(
            5,
            composer.Split(
                "ComposerAutomationVisibility.Prepare(",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("\"ChatComposerAttach\"", composer);
        Assert.DoesNotContain("\"ChatComposerMore\"", composer);
        Assert.DoesNotContain("\"ChatComposerSessionPicker\"", composer);
        Assert.Contains("\"ChatComposerModelPicker\"", composer);
        Assert.Contains("\"ChatComposerReasoningPicker\"", composer);
        Assert.Contains("\"ChatComposerVoice\"", composer);
        Assert.DoesNotContain("MenuItem(Localized(\"Chat_Composer_Tooltip_Settings\"", composer);
        Assert.Contains("\"ChatComposerPrimaryAction\"", composer);
    }

    [Fact]
    public void VoiceSettings_RetainsSpokenResponseToggle()
    {
        var pages = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages");
        var view = File.ReadAllText(Path.Combine(pages, "VoiceSettingsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(pages, "VoiceSettingsPage.xaml.cs"));

        Assert.Contains("x:Name=\"TtsResponseToggle\" Toggled=\"OnTtsResponseToggled\"", view);
        Assert.Contains("SetChatSpeakerMuted(!TtsResponseToggle.IsOn)", code);
    }

    [Theory]
    [InlineData("ChatModelPicker.cs")]
    [InlineData("ChatReasoningPicker.cs")]
    public void ReactorPickers_PreserveTargetMountActions(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", fileName));
        Assert.Contains("props.Target.OnMountAdd(", source);
        Assert.DoesNotContain("props.Target.OnMount(", source);
    }

    [Fact]
    public void PendingQueue_HasOnePresentationOwnerOutsideTranscriptAndComposer()
    {
        var composer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatComposer.cs"));

        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs"));
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawReactorChatRoot.cs"));

        Assert.DoesNotContain("queuedPanel", composer);
        Assert.DoesNotContain("queuedRows", composer);
        var pending = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatPendingMessages.cs"));
        Assert.DoesNotContain("FromQueuedMessage", timeline);
        Assert.DoesNotContain("ChatQueuedMessage_", timeline);
        Assert.Contains("ReactorChatPendingMessages.Render(timelineProps", root);
        Assert.Contains("Chat_Timeline_Pending", pending);
        Assert.Contains("AutomationLiveSetting.Polite", pending);
        Assert.Contains("queuedMessages.Count == 0", root);
        Assert.Contains("props.ComposerSession.Controller.CancelQueuedMessage", root);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void PendingMessages_UseNativeCardFill(string theme)
    {
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Themes", "ChatResources.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = document.Descendants().Single(element => (string?)element.Attribute(x + "Key") == theme);
        var pending = dictionary.Elements().Single(element => (string?)element.Attribute(x + "Key") == "ChatCardBrush");
        Assert.Equal(theme == "HighContrast"
            ? "{ThemeResource SystemColorWindowColor}"
            : "{ThemeResource CardBackgroundFillColorDefault}", (string?)pending.Attribute("Color"));
        Assert.Null(pending.Attribute("Opacity"));
    }

    [Fact]
    public void ReactorComposer_ReattachesStableImagePasteHandlerAfterRemount()
    {
        var composer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatComposer.cs"));

        const string callbackRef =
            "var controllerRef = UseRef(controller);";
        const string callbackAssignment =
            "controllerRef.Current = controller;";
        const string handlerRef =
            "var pasteHandler = UseRef<TextControlPasteEventHandler>(async (_, args) =>";
        const string mount =
            "textBox.Paste += pasteHandler.Current;";
        const string unmount =
            "textBox.Paste -= pasteHandler.Current;";

        var callbackRefIndex = composer.IndexOf(callbackRef, StringComparison.Ordinal);
        var callbackAssignmentIndex = composer.IndexOf(callbackAssignment, StringComparison.Ordinal);
        var handlerRefIndex = composer.IndexOf(handlerRef, StringComparison.Ordinal);
        var mountIndex = composer.IndexOf(mount, StringComparison.Ordinal);
        var unmountIndex = composer.IndexOf(unmount, StringComparison.Ordinal);

        Assert.True(callbackRefIndex >= 0);
        Assert.True(callbackAssignmentIndex > callbackRefIndex);
        Assert.True(handlerRefIndex > callbackAssignmentIndex);
        Assert.True(mountIndex > handlerRefIndex);
        Assert.True(unmountIndex > mountIndex);
        Assert.Equal(1, composer.Split(handlerRef, StringSplitOptions.None).Length - 1);
        var pasteHandlerBody = composer[handlerRefIndex..mountIndex];
        Assert.Contains(
            "if (GetBitmapClipboardContent() is not { } clipboardContent)",
            pasteHandlerBody);
        Assert.DoesNotContain(
            "Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()",
            pasteHandlerBody);
        Assert.Contains(
            "await controllerRef.Current.PasteImageAsync(clipboardContent);",
            composer);
        Assert.DoesNotContain("TryReadImageFromClipboardAsync", composer);
        Assert.DoesNotContain("pasteHooked", composer);
    }

    [Fact]
    public void ReactorComposer_UsesBitmapOnlyContextMenuThatReentersStablePastePath()
    {
        var composer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatComposer.cs"));

        Assert.Contains("textBox.ContextFlyout = CreateComposerContextFlyout(", composer);
        Assert.Contains("textBox.ContextFlyout = null;", composer);
        Assert.DoesNotContain("ContextRequested", composer);
        Assert.Contains("StandardUICommandKind.Undo", composer);
        Assert.Contains("StandardUICommandKind.Redo", composer);
        Assert.Contains("StandardUICommandKind.Cut", composer);
        Assert.Contains("StandardUICommandKind.Copy", composer);
        Assert.Contains("StandardUICommandKind.Paste", composer);
        Assert.Contains("StandardUICommandKind.SelectAll", composer);
        Assert.Contains("\"ChatComposerPasteMenuItem\"", composer);
        Assert.Contains("var menu = new MenuFlyout();", composer);
        Assert.Contains("menu.Items.Add(undoItem);", composer);
        Assert.Contains("menu.Items.Add(redoItem);", composer);
        Assert.Contains("menu.Items.Add(cutItem);", composer);
        Assert.Contains("menu.Items.Add(copyItem);", composer);
        Assert.Contains("menu.Items.Add(pasteItem);", composer);
        Assert.Contains("menu.Items.Add(selectAllItem);", composer);
        Assert.Contains("menu.Opening += (_, _) =>", composer);
        Assert.Contains("var state = ChatComposerContextMenuState.Project(", composer);
        Assert.Contains("pasteItem.Visibility = ToVisibility(state.ShowPaste);", composer);
        Assert.Contains("textBox.PasteFromClipboard();", composer);
        Assert.DoesNotContain("TextCommandBarFlyout", composer);

        var menuStart = composer.IndexOf(
            "private static MenuFlyout CreateComposerContextFlyout(",
            StringComparison.Ordinal);
        var standardItemStart = composer.IndexOf(
            "private static MenuFlyoutItem CreateStandardMenuItem(",
            StringComparison.Ordinal);
        var menuFactory = composer[menuStart..standardItemStart];

        Assert.DoesNotContain("TryReadImageFromClipboardAsync", menuFactory);
        Assert.Contains("GetBitmapClipboardContent()", menuFactory);
        Assert.Contains(
            "_ = getController().PasteImageAsync(clipboardContent);",
            menuFactory);
        Assert.Equal(
            1,
            composer.Split(
                "await controllerRef.Current.PasteImageAsync(clipboardContent);",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("PasteTextFromClipboard(textBox);", menuFactory);
        Assert.Contains("private static void PasteTextFromClipboard(TextBox textBox)", composer);
        Assert.Contains("catch (System.Runtime.InteropServices.COMException ex)", composer);
        Assert.Contains("clipboard text paste failed", composer);
        Assert.DoesNotContain("ClipboardContainsBitmap", composer);
    }

    [Fact]
    public void ChatComposerContextMenuState_ProjectsNativeCommandVisibility()
    {
        Assert.Equal(
            new ChatComposerContextMenuState(
                ShowUndo: false,
                ShowRedo: false,
                ShowCut: false,
                ShowCopy: false,
                ShowPaste: false,
                ShowSelectAll: false,
                ShowEditSeparator: false,
                ShowSelectAllSeparator: false),
            ChatComposerContextMenuState.Project(
                canUndo: false,
                canRedo: false,
                hasSelection: false,
                canPaste: false,
                hasText: false));

        Assert.Equal(
            new ChatComposerContextMenuState(
                ShowUndo: true,
                ShowRedo: true,
                ShowCut: true,
                ShowCopy: true,
                ShowPaste: true,
                ShowSelectAll: true,
                ShowEditSeparator: true,
                ShowSelectAllSeparator: true),
            ChatComposerContextMenuState.Project(
                canUndo: true,
                canRedo: true,
                hasSelection: true,
                canPaste: true,
                hasText: true));

        var pasteOnly = ChatComposerContextMenuState.Project(
            canUndo: false,
            canRedo: false,
            hasSelection: false,
            canPaste: true,
            hasText: false);
        Assert.True(pasteOnly.ShowPaste);
        Assert.False(pasteOnly.ShowEditSeparator);
        Assert.False(pasteOnly.ShowSelectAllSeparator);

        var selectedText = ChatComposerContextMenuState.Project(
            canUndo: false,
            canRedo: false,
            hasSelection: true,
            canPaste: false,
            hasText: true);
        Assert.True(selectedText.ShowCut);
        Assert.True(selectedText.ShowCopy);
        Assert.True(selectedText.ShowSelectAll);
        Assert.True(selectedText.ShowSelectAllSeparator);
    }

    [Fact]
    public void ReactorComposer_UsesReactorThemeResourcesWithoutManualThemeObservation()
    {
        var chatDirectory = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat");
        var composer = File.ReadAllText(Path.Combine(chatDirectory, "ReactorChatComposer.cs"));
        var visuals = File.ReadAllText(Path.Combine(chatDirectory, "ChatVisuals.cs"));

        Assert.Contains("UseColorScheme()", composer);
        Assert.Contains(".Background(Theme.Ref(\"ChatComposerBrush\"))", composer);
        Assert.Contains(".BorderBrush(Theme.Ref(\"ChatStrokeBrush\"))", composer);
        Assert.Contains("Theme.Ref(\"AcrylicBackgroundFillColorDefaultBrush\")", composer);
        Assert.Contains("Theme.Ref(\"SurfaceStrokeColorFlyoutBrush\")", composer);
        Assert.Equal(3, composer.Split(
            ".Resources(ChatVisuals.ToolbarButtonResources)", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Theme.Ref(\"SubtleFillColorTertiaryBrush\")", composer);
        Assert.Contains("Theme.Ref(\"SubtleFillColorTertiaryBrush\")", visuals);
        Assert.Contains("colorScheme);", composer);
        Assert.Contains("CreateSlashPopupHost(BuildSlashPopup(", composer);

        Assert.DoesNotContain("AccessibilitySettings", composer);
        Assert.DoesNotContain("HighContrastChanged", composer);
        Assert.DoesNotContain("ConditionalWeakTable", composer);
        Assert.DoesNotContain("ApplyTheme(", composer);
        Assert.DoesNotContain("ResolveThemeBrush", composer);
        Assert.DoesNotContain("FindThemedResource", composer);
        Assert.DoesNotContain("SearchThemeDictionaries", composer);
        Assert.DoesNotContain("LookupResource", composer);
        Assert.DoesNotContain("Application.Current.Resources", composer);
    }

    [Fact]
    public void ReactorComposer_LocalizesSettingsTooltipInEveryLocale()
    {
        var stringsDirectory = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Strings");

        foreach (var resourceFile in Directory.EnumerateFiles(
                     stringsDirectory,
                     "Resources.resw",
                     SearchOption.AllDirectories))
        {
            var resources = File.ReadAllText(resourceFile);
            Assert.Contains("Chat_Composer_Tooltip_Settings", resources);
        }
    }

    [Fact]
    public void ReactorRoot_SettlesWelcomeEligibilityBeforeShowingEmptyState()
    {
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));

        Assert.Contains("var welcomeEligible = isEmptyConversation", root);
        Assert.Contains("var welcomeEligibilityKey =", root);
        Assert.Contains("var welcomeEligibilityKeyRef = UseRef<string?>", root);
        Assert.Contains("var (settledWelcomeKey, setSettledWelcomeKey) = UseState<string?>", root);
        Assert.Contains("await Task.Delay(800)", root);
        Assert.Contains("welcomeEligibilityKeyRef.Current", root);
        Assert.Contains("settledWelcomeKey,", root);
        Assert.Contains("welcomeEligibilityKey,", root);
        Assert.Contains("var emptyConversationIsAuthoritative = welcomeEligibilityKey is not null", root);
        Assert.Contains("isEmptyConversation && !emptyConversationIsAuthoritative", root);
    }

    [Fact]
    public void ReactorTimeline_ProjectsActivityInSourceChronology()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains("var chronologicalEntries = ChatReasoningPresentation.Project(", timeline);
        Assert.Contains("props.Timeline.Entries, props.Timeline.ReasoningStream", timeline);
        Assert.Contains("ChatToolActivityPresentation.Project(", timeline);
        Assert.Contains("ChatTimelineAssistantRuns.Describe(chronologicalEntries)", timeline);
        Assert.DoesNotContain("OrderEntriesForPresentation", timeline);
        Assert.Contains("includeMetadata: row.IsAssistantRunEnd", timeline);
    }

    [Fact]
    public void ReactorTimeline_RealizationFocusCannotCancelPendingTailNavigation()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorItemsViewScrollController.cs"));
        Assert.Contains("itemsView.AddHandler(UIElement.GettingFocusEvent, _readerGettingFocus, true);", source);
        Assert.Contains("itemsView.RemoveHandler(UIElement.GettingFocusEvent, _readerGettingFocus);", source);
        Assert.Contains("if (args.FocusState == FocusState.Keyboard)", source);
        Assert.DoesNotContain("itemsView.GotFocus +=", source);
    }

    [Fact]
    public void ReactorTimeline_UsesCanonicalToolActivityKeyForStandaloneAndGroupedRows()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs"));

        Assert.Contains("entry.Kind == ChatTimelineItemKind.ToolCall", timeline);
        Assert.Contains("ChatToolActivityPresentation.ActivityKey(", timeline);
        Assert.Contains("ReactorChatTimeline.RowKey(props.Timeline, entry)", timeline);
    }

    [Fact]
    public void ReactorTimeline_DelegatesToolAndActivityRenderingToFocusedOwner()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs"));
        var renderer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ToolCallCardRenderer.cs"));

        Assert.Contains("ToolCallCardRenderer.BuildStandalone", timeline);
        Assert.Contains("ToolCallCardRenderer.BuildActivity", timeline);
        Assert.DoesNotContain("private static Element BuildTool", timeline);
        Assert.Contains("public static Element BuildStandalone", renderer);
        Assert.Contains("public static Element BuildActivity", renderer);
        Assert.Contains("FormatToolDisplayArgs(entry.ToolArgs)", renderer);
        Assert.Contains("private const int ToolDetailMaxChars = 4000;", renderer);
        Assert.Contains("\"Chat_Tool_InputSection\"", renderer);
        Assert.Contains("\"Chat_Tool_OutputLabel\"", renderer);
        Assert.Contains(".Padding(ChatVisuals.DetailHorizontalInset, ChatVisuals.DetailVerticalInset)", renderer);
        Assert.Contains("var body = RichTextBlock(content)", renderer);
        Assert.Contains(".MaxHeight(240)", renderer);
        Assert.Contains("text.IsTextSelectionEnabled = true", renderer);
        Assert.DoesNotContain("var stateText =", renderer);
        Assert.DoesNotContain("var glyph =", renderer);
        Assert.Contains(".AutomationId(", renderer);
        Assert.Contains("ChatToolActivity_", renderer);
        Assert.Contains("ChatToolCall_", renderer);
        Assert.Contains("internal sealed class ToolActivityCard : Component<ToolActivityCardProps>", renderer);
        Assert.Contains("Element details = !isExpanded", renderer);
        Assert.Contains("? VStack(", renderer);
        Assert.Contains(".FontSize(12)", renderer);
        Assert.Contains(".BorderThickness(0)", renderer);
        Assert.Contains(".Background(Theme.Ref(\"SubtleFillColorTransparentBrush\"))", renderer);
        Assert.Contains(": Empty();", renderer);
        Assert.DoesNotContain("activity.Tools.Select(BuildStandalone)", renderer);
    }

    [Fact]
    public void ReactorTimeline_RendersStructuredCompactionCard()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains("ChatTimelineItemKind.Status => BuildStatus(row, entry)", timeline);
        Assert.Contains("ChatCompactionPresenter.TryCreateForEntry(", timeline);
        Assert.Contains("Chat_Compaction_Title", timeline);
        Assert.Contains("Chat_Compaction_FallbackDetail", timeline);
        Assert.Contains("Chat_Compaction_OpenCheckpoints", timeline);
        Assert.Contains("row.Props.OnOpenCheckpoints!(sessionKey!)", timeline);
        Assert.Contains(".BorderThickness(1)", timeline);
        Assert.DoesNotContain("ReactorChatComposer.IsHighContrast", timeline);
        Assert.Contains(".AutomationName(presentation.AutomationName)", timeline);
    }
}
