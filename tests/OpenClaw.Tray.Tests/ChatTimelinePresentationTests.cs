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
        Assert.Contains("initialTailRequestKey", timeline);
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
    public void ReactorTimeline_QueuesInitialTailAfterLoadedLayoutAndCleansUp()
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
        Assert.Contains("_valid = tailIndex >= 0", binding);
        Assert.Contains("itemsView.Unloaded += OnUnloaded", binding);
        Assert.Contains("itemsView.Loaded -= OnLoaded", binding);
        Assert.Contains("itemsView.LayoutUpdated -= OnLayoutUpdated", binding);
        Assert.DoesNotContain("ChangeView", binding);
        Assert.DoesNotContain("UpdateLayout", binding);
        Assert.Contains("scrollView.VerticalAnchorRatio = _following ? 1.0 : double.NaN", binding);
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
}
