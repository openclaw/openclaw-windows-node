using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTimelinePresentationTests
{
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

        Assert.Contains("context.BindFor(itemsView, element).Reference", binding);
        Assert.Contains("VerticalScrollController = scrollBar?.ScrollController", binding);
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
        Assert.Contains("itemsView.StartBringItemIntoView(", binding);
        Assert.Contains("VerticalAlignmentRatio = 1.0", binding);
        Assert.Contains("!string.Equals(_displayedTailKey, displayedTailKey, StringComparison.Ordinal)", binding);
        Assert.Contains("_following = IsNearBottom(sender)", binding);
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
        Assert.DoesNotContain("ChangeView", binding);
        Assert.DoesNotContain("UpdateLayout", binding);
        Assert.DoesNotContain("TailSettle", binding);
        Assert.DoesNotContain("ScrollTo(", binding);
        Assert.DoesNotContain("ScrollCompleted", binding);
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
    public void ReactorTimeline_RequeuesOnlyForCompletedHistoryReplacement()
    {
        var provider = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawChatDataProvider.cs"));
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

        Assert.Contains("_historyRevisions[threadId] = GetHistoryRevisionLocked(threadId) + 1", provider);
        Assert.Contains("HistoryRevisions: historyRevisionsCopy", provider);
        Assert.Contains("snapshot.HistoryRevisions", root);
        Assert.Contains("HistoryRevision: historyRevision", root);
        Assert.Contains("props.HistoryRevision", timeline);
        Assert.DoesNotContain("|{props.Mode}", timeline);
    }

    [Fact]
    public void ReactorComposer_OffsetsPickerChevronRightAndUp()
    {
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));

        Assert.Contains("textBlock.Margin = new Thickness(2, 4, 0, 0)", root);
    }

    [Fact]
    public void ReactorComposer_BoundsAndAnnouncesQueuedMessages()
    {
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));

        Assert.Contains("ScrollView(VStack(4, queuedRows))", root);
        Assert.Contains(".MaxHeight(props.IsCompact ? 144 : 220)", root);
        Assert.Contains("AutomationLiveSetting.Polite", root);
    }

    [Fact]
    public void ReactorComposer_UsesReactorThemeResourcesWithoutManualThemeObservation()
    {
        var root = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));
        var composer = root[root.IndexOf(
            "public sealed class ReactorChatComposer",
            StringComparison.Ordinal)..];

        Assert.Contains("UseColorScheme()", composer);
        Assert.Contains(".Background(Theme.ControlFill)", composer);
        Assert.Contains(".BorderBrush(Theme.ControlStroke)", composer);
        Assert.Contains("Theme.Ref(\"AcrylicBackgroundFillColorDefaultBrush\")", composer);
        Assert.Contains("Theme.Ref(\"SurfaceStrokeColorFlyoutBrush\")", composer);
        Assert.Contains("Theme.Ref(\"SubtleFillColorTertiaryBrush\")", composer);
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
    public void ReactorTimeline_GroupsAssistantRunsInPresentationOrder()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains(
            "var orderedEntries = OrderEntriesForPresentation(props.Timeline.Entries);",
            timeline);
        Assert.Contains("ChatTimelineAssistantRuns.Describe(orderedEntries)", timeline);
        Assert.Contains("includeMetadata: row.IsAssistantRunEnd", timeline);
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
