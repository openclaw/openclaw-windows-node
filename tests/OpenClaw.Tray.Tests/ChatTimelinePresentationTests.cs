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
    public void ReactorTimeline_ProjectsActivityInSourceChronology()
    {
        var timeline = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatTimeline.cs"));

        Assert.Contains("var chronologicalEntries = props.Timeline.Entries;", timeline);
        Assert.Contains("ChatToolActivityPresentation.Project(", timeline);
        Assert.Contains("ChatTimelineAssistantRuns.Describe(chronologicalEntries)", timeline);
        Assert.DoesNotContain("OrderEntriesForPresentation", timeline);
        Assert.Contains("includeMetadata: row.IsAssistantRunEnd", timeline);
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
        Assert.Contains("FormatToolInput(entry.ToolArgs)", renderer);
        Assert.Contains("private const int ToolDetailMaxChars = 4000;", renderer);
        Assert.Contains("\"Chat_Tool_InputSection\"", renderer);
        Assert.Contains("\"Chat_Tool_OutputLabel\"", renderer);
        Assert.Contains(".Padding(18, 8, 18, 10)", renderer);
        Assert.Contains("var body = RichTextBlock(content)", renderer);
        Assert.Contains(".MaxHeight(240)", renderer);
        Assert.Contains("text.IsTextSelectionEnabled = true", renderer);
        Assert.DoesNotContain("var stateText =", renderer);
        Assert.DoesNotContain("var glyph =", renderer);
        Assert.Contains("AutomationProperties.SetAutomationId(", renderer);
        Assert.Contains("ChatToolActivity_", renderer);
        Assert.Contains("ChatToolCall_", renderer);
        Assert.Contains("internal sealed class ToolActivityCard : Component<ToolActivityCardProps>", renderer);
        Assert.Contains("Element details = isExpanded", renderer);
        Assert.Contains("? VStack(", renderer);
        Assert.Contains("control.MinHeight = 28;", renderer);
        Assert.Contains("control.FontSize = 12;", renderer);
        Assert.Contains("border.BorderThickness = isNested", renderer);
        Assert.Contains("? new Thickness(0)", renderer);
        Assert.Contains("? \"SubtleFillColorTransparentBrush\"", renderer);
        Assert.Contains(": Empty();", renderer);
        Assert.DoesNotContain("activity.Tools.Select(BuildStandalone)", renderer);
    }
}
