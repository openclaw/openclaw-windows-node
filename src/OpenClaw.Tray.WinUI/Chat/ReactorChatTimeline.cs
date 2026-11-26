using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Markdown;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using System.Text.Json.Nodes;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;
using WinUIAnnotatedScrollBar = Microsoft.UI.Xaml.Controls.AnnotatedScrollBar;

namespace OpenClawTray.Chat;

public enum ReactorChatTimelineMode
{
    Timeline,
    Loading,
    Empty,
    /// <summary>History retries are exhausted and no transcript is cached.</summary>
    HistoryUnavailable,
}

/// <param name="HistoryFailed">History retries are exhausted; with a cached transcript this shows an inline Retry status.</param>
/// <param name="OnRetryHistory">Starts a user-initiated history reload.</param>
public sealed record ReactorChatTimelineProps(
    ReactorChatTimelineMode Mode,
    ChatTimelinePresentationContext Timeline,
    ChatAgentIdentity? AssistantIdentity = null,
    long HistoryRevision = 0,
    double ViewportWidth = 800,
    Func<string, bool>? TryCopyText = null,
    bool HistoryFailed = false,
    Action? OnRetryHistory = null,
    string? AssistantAgentId = null,
    OpenClaw.Shared.ChatPlacementCatalog? AssistantCatalog = null,
    IChatAgentAvatarImageSource? AssistantAvatarImageSource = null,
    ChatAgentPresentation? AssistantRosterAgent = null,
    bool ReducedMotion = false,
    bool AdmitInitialEntrances = false);

/// <summary>
/// Reactor-owned production timeline. Reactor's keyed ItemsView handles row
/// reconciliation, container realization, scrolling, and virtualization.
/// </summary>
public sealed class ReactorChatTimeline : Component<ReactorChatTimelineProps>
{
    private static readonly TimeSpan MessageFooterFadeDuration = TimeSpan.FromMilliseconds(160);
    private const int TranscriptRevealMilliseconds = 150;
    // Like web, only a newly sent prompt enters: its bubble rises into place, fully opaque.
    // Replies, tool, thinking and status rows never enter, since a fade or rise would hide or
    // snap text that is still arriving. History, recycling and session switches never animate.
    private const int UserEntranceMilliseconds = 180;
    private const float UserEntranceRise = 8;
    // The working indicator settles in like web's.
    private const int WorkingIndicatorMilliseconds = 200;
    private const float WorkingIndicatorRise = 4;
    // A row admitted for an entrance keeps it this long, so an item the list realizes a render
    // or two later (for example between streaming deltas) still animates.
    private const long EntranceAdmissionWindowMilliseconds = 500;
    // Rises 16px into place and grows from 80% while fading in; leaves the same way in reverse.
    private const int JumpLatestRevealMilliseconds = 240;
    private const float JumpLatestHiddenScale = .8f;
    private const float JumpLatestHiddenOffset = 16;

    public override Element Render()
    {
        var props = Props;
        var (speakingEntryId, setSpeakingEntryId) = UseState<string?>(null, threadSafe: true);
        var (hoveredEntryKey, setHoveredEntryKey) = UseState<string?>(null, threadSafe: true);
        // Enter and exit can both arrive before the preceding state update renders.
        var hoverTarget = UseRef<string?>(null);
        var (focusedEntryKey, setFocusedEntryKey) = UseState<string?>(null);
        // Start from the parent's measured width, so a timeline mounted by the first send does not
        // lay out at a default width and then jump sideways once its own measurement arrives.
        var (viewportWidth, setViewportWidth) = UseState(props.ViewportWidth);
        var (atTail, setAtTail) = UseState(true);
        var (jumpRevision, setJumpRevision) = UseState(0);
        var (loadingOlder, setLoadingOlder) = UseState(false, threadSafe: true);
        var olderOperation = UseRef(0);
        var olderLoadingScope = UseRef<string?>(null);
        var lastReadTail = UseRef<IReadOnlyList<ChatTimelineItem>?>(null);
        var timelineScope = UseRef<string?>(null);
        var currentScope = $"{props.Timeline.SessionId}|{props.Timeline.TimelineGeneration}";
        if (timelineScope.Current != currentScope)
        {
            timelineScope.Current = currentScope;
            setAtTail(true);
            setLoadingOlder(false);
            olderOperation.Current++;
            olderLoadingScope.Current = null;
            loadingOlder = false;
        }
        var speechOperation = UseRef(0);
        var mounted = UseRef(true);
        var toolActivityExpansionState = UseRef<ChatToolActivityExpansionState>(new());
        var toolActivityMotionState = UseRef<ChatToolActivityMotionState>(new());
        // The same admit-once rule gives new rows their entrance, never history replays.
        var messageMotionState = UseRef<ChatToolActivityMotionState>(new());
        var entranceDeadlines = UseRef(new Dictionary<string, long>(StringComparer.Ordinal));
        var reflowRef = UseRef<ChatTranscriptReflow?>(null);
        var reflow = reflowRef.Current ??= new ChatTranscriptReflow();
        var messageMotionScope = UseRef<string?>(null);
        var annotatedScrollBarRef = this.UseElementRef<WinUIAnnotatedScrollBar>();
        var reducedMotion = ChatMotion.IsReducedMotion(UseReducedMotion());
        // Web's history skeleton is static under reduced motion or forced colors.
        var animateLoading = !reducedMotion && UseColorScheme() != ColorScheme.HighContrast;
        var loadingPulse = UseRef<FrameworkElement?>(null);
        var loadingPulseAllowed = UseRef(false);
        loadingPulseAllowed.Current = props.Mode == ReactorChatTimelineMode.Loading && animateLoading;
        // ItemsView can retain the retired row without unloading it.
        if (!loadingPulseAllowed.Current)
        {
            if (loadingPulse.Current is { } retired)
                StopLoadingPulse(retired);
            loadingPulse.Current = null;
        }
        void MountLoadingPulse(FrameworkElement element)
        {
            if (loadingPulse.Current is { } previous)
                StopLoadingPulse(previous);
            loadingPulse.Current = element;
            if (loadingPulseAllowed.Current)
                StartLoadingPulse(element);
        }
        var loadOlder = props.Timeline.OnLoadMoreHistory;
        async Task LoadOlderAsync()
        {
            if (loadOlder is null || olderLoadingScope.Current == currentScope)
                return;
            var operation = ++olderOperation.Current;
            olderLoadingScope.Current = currentScope;
            setLoadingOlder(true);
            try { await loadOlder(); }
            finally
            {
                if (mounted.Current && olderOperation.Current == operation)
                {
                    olderLoadingScope.Current = null;
                    setLoadingOlder(false);
                }
            }
        }
        props = props with
        {
            ReducedMotion = reducedMotion,
            Timeline = props.Timeline with
            {
                LoadingOlderHistory = loadingOlder,
                OnLoadMoreHistory = LoadOlderAsync,
                ToolExpansionState = toolActivityExpansionState.Current,
            },
        };

        // History arriving after the loading skeleton fades in instead of swapping in one frame.
        var transcriptHostRef = this.UseElementRef<ChatMotionHost>();
        var previousMode = UseRef<ReactorChatTimelineMode?>(null);
        var revealTranscript = previousMode.Current == ReactorChatTimelineMode.Loading
            && props.Mode == ReactorChatTimelineMode.Timeline && !reducedMotion;
        previousMode.Current = props.Mode;
        UseEffect((Func<Action>)(() =>
        {
            if (revealTranscript && transcriptHostRef.Current is { } host)
                ChatMotion.FadeIn(host, TranscriptRevealMilliseconds);
            return static () => { };
        }), props.Mode);

        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () =>
            {
                mounted.Current = false;
                speechOperation.Current++;
                loadingPulseAllowed.Current = false;
                if (loadingPulse.Current is { } element)
                    StopLoadingPulse(element);
                loadingPulse.Current = null;
            };
        }), Array.Empty<object>());

        async Task ToggleSpeechAsync(ChatTimelineItem entry)
        {
            var text = entry.Text ?? string.Empty;
            if (text.Length == 0)
                return;

            if (string.Equals(speakingEntryId, entry.Id, StringComparison.Ordinal))
            {
                speechOperation.Current++;
                setSpeakingEntryId(null);
                props.Timeline.OnStopSpeaking?.Invoke();
                return;
            }

            if (props.Timeline.OnReadAloud is not { } readAloud)
                return;

            var operation = ++speechOperation.Current;
            setSpeakingEntryId(entry.Id);
            try
            {
                await readAloud(StripMarkdownForSpeech(text));
            }
            catch (Exception ex)
            {
                OpenClawTray.Services.Logger.Debug($"Reactor chat timeline: read aloud failed: {ex.Message}");
            }
            finally
            {
                if (mounted.Current && speechOperation.Current == operation)
                    setSpeakingEntryId(null);
            }
        }

        var rows = BuildRows(props with { ViewportWidth = viewportWidth });
        var historyAnchors = ChatHistoryNavigation.Project(rows);
        var enteringWorkKeys = toolActivityMotionState.Current.Update(
            rows.Where(row => row.Activity?.IsCompletedWork == true).Select(row => row.Key),
            props.Timeline.SessionId,
            props.Timeline.TimelineGeneration,
            props.HistoryRevision,
            historyReady: props.Mode == ReactorChatTimelineMode.Timeline);
        var enteringPromptKeys = messageMotionState.Current.Update(
            rows.Select(row => row.Key),
            props.Timeline.SessionId,
            props.Timeline.TimelineGeneration,
            props.HistoryRevision,
            historyReady: props.Mode == ReactorChatTimelineMode.Timeline,
            admitInitial: props.AdmitInitialEntrances,
            eligibleKeys: rows.Where(row => row.Entry?.Kind == ChatTimelineItemKind.User).Select(row => row.Key));
        var now = Environment.TickCount64;
        var deadlines = entranceDeadlines.Current;
        var rowScope = $"{props.Timeline.SessionId}|{props.Timeline.TimelineGeneration}";
        if (messageMotionScope.Current != rowScope)
            deadlines.Clear();
        foreach (var expired in deadlines.Where(pair => pair.Value < now).Select(pair => pair.Key).ToList())
            deadlines.Remove(expired);
        foreach (var key in enteringWorkKeys.Concat(enteringPromptKeys))
            deadlines[key] = now + EntranceAdmissionWindowMilliseconds;
        // Checked when the list builds the row, which can be after this render.
        bool AnimatesEntrance(string key) =>
            !reducedMotion && deadlines.TryGetValue(key, out var deadline) && deadline >= Environment.TickCount64;
        // Existing rows never glide when a message arrives or a reply streams. Tail follow is
        // immediate; only newly admitted rows animate. Explicit reader reflows use CaptureAll.
        messageMotionScope.Current = rowScope;
        var initialTailRequestKey =
            $"{props.Timeline.SessionId ?? "none"}|{props.Timeline.TimelineGeneration}|{props.HistoryRevision}|{jumpRevision}";
        var displayedTailKey = rows.Count > 0 ? rows[^1].Key : null;
        var contentRevision = props.Timeline.Entries;
        if (atTail)
            lastReadTail.Current = contentRevision;
        void SetEntryHovered(string entryKey, bool isHovered)
        {
            if (isHovered)
            {
                if (!string.Equals(hoverTarget.Current, entryKey, StringComparison.Ordinal))
                {
                    hoverTarget.Current = entryKey;
                    setHoveredEntryKey(entryKey);
                }
            }
            else if (string.Equals(hoverTarget.Current, entryKey, StringComparison.Ordinal))
            {
                hoverTarget.Current = null;
                setHoveredEntryKey(null);
            }
        }

        var itemsView = ItemsView(
            rows,
            static row => row.Key,
            (row, _) => ItemContainer(
                    Border(ChatMotion.Host(BuildRow(
                            row,
                            speakingEntryId,
                            hoveredEntryKey,
                            focusedEntryKey,
                            ToggleSpeechAsync,
                            SetEntryHovered,
                            setFocusedEntryKey,
                            toolActivityExpansionState.Current,
                            AnimatesEntrance(row.Key),
                            MountLoadingPulse))
                            .Set(host => reflow.Track(host, row.Key))
                            .OnUnmountAdd(reflow.Untrack))
                        .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                        .MaxWidth(ChatVisuals.ReadingWidth - 2 * ChatVisuals.ProseInset + 2 * AvatarGutter(row))
                        .Margin(
                            ChatVisuals.Gutter(viewportWidth) + ChatVisuals.ProseInset - AvatarGutter(row),
                            0,
                            ChatVisuals.Gutter(viewportWidth) + ChatVisuals.ProseInset - AvatarGutter(row),
                            string.Equals(row.Key, rows[^1].Key, StringComparison.Ordinal)
                                ? ChatVisuals.TranscriptBottomInset
                                : 0)
                        .HAlign(HorizontalAlignment.Stretch))
                .Resources(resources => resources
                    .Set("ItemContainerPointerOverBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerPointerOverBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerPressedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerPressedBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectionVisualBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectionVisualPointerOverBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectionVisualPressedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectedPointerOverBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectedPressedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ItemContainerSelectedInnerBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
                .IsTabStop(false)
                .Set(itemContainer =>
                {
                    itemContainer.IsSelected = false;
                    itemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    // The template's rounded root clips its child, which would cut a row gliding
                    // or held outside its slot (ChatTranscriptReflow) at the slot edge.
                    itemContainer.CornerRadius = new CornerRadius(0);
                })
                .HAlign(HorizontalAlignment.Stretch)
                .WithKey(row.Key)) with
        {
            LayoutKind = ItemsViewLayoutKind.StackLayout,
            SelectionMode = ItemsViewSelectionMode.None,
            IsItemInvokedEnabled = false,
        };
        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            ChatMotion.Host(Border(Border(itemsView
                    .BindVerticalScrollController(
                        annotatedScrollBarRef,
                        rows.Count - 1,
                        rows.Count,
                        jumpRevision,
                        props.Timeline.ScrollToBottomToken,
                        initialTailRequestKey,
                        displayedTailKey,
                        contentRevision,
                        setAtTail,
                        historyAnchors)
                    .AutomationName("Chat messages")
                    .HAlign(HorizontalAlignment.Stretch)
                    .VAlign(VerticalAlignment.Stretch)))
                .Margin(0, ChatVisuals.TranscriptTopInset, 0, 0)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch)
                .OnMount(control => ChatTranscriptFade.Attach((Microsoft.UI.Xaml.Controls.Border)control))
                .OnUnmount(control => ChatTranscriptFade.Detach((Microsoft.UI.Xaml.Controls.Border)control)))
                .Grid(row: 1, column: 0)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch)
                .OnUnmountAdd(ChatMotion.Reset)
                .OnMountAdd(control => reflow.Attach(control))
                .OnUnmountAdd(_ => reflow.Detach())
                .Ref(transcriptHostRef),
            AnnotatedScrollBar()
                .Ref(annotatedScrollBarRef)
                .Width(ChatVisuals.RailWidth(viewportWidth))
                .MinWidth(0)
                .Set(control => control.Style = (Microsoft.UI.Xaml.Style)
                    Microsoft.UI.Xaml.Application.Current.Resources["ChatHistoryScrollBarStyle"])
                .MaxHeight(Math.Max(1, historyAnchors.Count - 1) * ChatVisuals.HistoryTickSpacing
                    + 2 * ChatVisuals.HistoryRailInset)
                .VAlign(VerticalAlignment.Stretch)
                .IsTabStop(historyAnchors.Count > 0)
                .IsHitTestVisible(historyAnchors.Count > 0)
                .Opacity(historyAnchors.Count > 0 ? 1 : 0)
                .Grid(row: 1, column: 0)
                .HAlign(HorizontalAlignment.Right)
                .AutomationId("ChatHistoryNavigation")
                .AutomationName(LocalizedOrDefault("Chat_HistoryNavigation", "Chat message navigation"))
                .HelpText(LocalizedOrDefault("Chat_HistoryNavigation_Help",
                    "Use Up and Down to jump between messages, or Home and End for the first and latest message.")),
            (props.HistoryFailed && props.Mode == ReactorChatTimelineMode.Timeline
                    ? BuildHistoryFailedBanner(props, viewportWidth)
                    : Empty())
                .Grid(row: 0, column: 0),
            // The reveal (fade, rise and scale) lives on an unpooled motion host; the button itself
            // keeps its native focus, enabled and accessibility state. Its disabled look matches its
            // rest look, so leaving the tail does not swap colors while it fades out.
            ChatMotion.Host(Button(TextBlock(FluentIconCatalog.ChatJumpLatest)
                        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                        .FontSize(16)
                        .AccessibilityView(AccessibilityView.Raw)
                        .Set(text => text.IsTextScaleFactorEnabled = false),
                    () => setJumpRevision(jumpRevision + 1))
                    .Width(ChatVisuals.JumpLatestSize).Height(ChatVisuals.JumpLatestSize)
                    .MinWidth(ChatVisuals.JumpLatestSize).MinHeight(ChatVisuals.JumpLatestSize)
                    .Padding(0).CornerRadius(ChatVisuals.JumpLatestSize / 2)
                    .BorderThickness(1)
                    .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
                    .Resources(resources => resources
                        .Set("ButtonBackground", Theme.Ref("LayerFillColorDefaultBrush"))
                        .Set("ButtonBorderBrush", Theme.Ref("ChatStrokeBrush"))
                        .Set("ButtonBackgroundDisabled", Theme.Ref("LayerFillColorDefaultBrush"))
                        .Set("ButtonBorderBrushDisabled", Theme.Ref("ChatStrokeBrush"))
                        .Set("ButtonForegroundDisabled", Theme.Ref("ChatSecondaryTextBrush")))
                    .HAlign(HorizontalAlignment.Center).AutomationId("ChatJumpLatest")
                    .AutomationName(contentRevision != lastReadTail.Current
                        ? LocalizedOrDefault("Chat_NewMessages", "New messages. Jump to latest")
                        : LocalizedOrDefault("Chat_JumpLatest", "Jump to latest"))
                    .ToolTip(contentRevision != lastReadTail.Current
                        ? LocalizedOrDefault("Chat_NewMessages", "New messages. Jump to latest")
                        : LocalizedOrDefault("Chat_JumpLatest", "Jump to latest"))
                    .IsEnabled(!atTail).IsTabStop(!atTail)
                    .AccessibilityView(atTail ? AccessibilityView.Raw : AccessibilityView.Content)
                    .Set(button => button.IsHitTestVisible = !atTail))
                .Opacity(atTail ? 0 : 1)
                .Translation(0, atTail && !reducedMotion ? JumpLatestHiddenOffset : 0, 0)
                .Set(host =>
                {
                    var scale = atTail && !reducedMotion ? JumpLatestHiddenScale : 1f;
                    host.CenterPoint = new System.Numerics.Vector3(
                        (float)ChatVisuals.JumpLatestSize / 2, (float)ChatVisuals.JumpLatestSize / 2, 0);
                    host.Scale = new System.Numerics.Vector3(scale, scale, 1);
                    host.IsHitTestVisible = !atTail;
                })
                .DeferredRevealTransitions(JumpLatestRevealMilliseconds)
                .AutomationId("ChatJumpLatestReveal")
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Bottom).Margin(0, 0, 0, ChatVisuals.BlockGap)
                .Grid(row: 1, column: 0))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnMount(control => ChatVisuals.Observe(control, setViewportWidth))
            .OnUnmount(ChatVisuals.StopObserving);
    }

    public static string RowKey(ChatTimelinePresentationContext props, ChatTimelineItem entry) =>
        entry.Kind == ChatTimelineItemKind.ToolCall
            ? ChatToolActivityPresentation.ActivityKey(
                props.SessionId,
                props.TimelineGeneration,
                entry.Id)
            : $"thread:{props.SessionId ?? "none"}|generation:{props.TimelineGeneration}|kind:{entry.Kind}|id:{entry.Id}";

    // Assistant messages always reserve the avatar column; a tool row or group reserves it only
    // when it opens an agent run, because it then carries that run's avatar.
    private static double AvatarGutter(ReactorTimelineRow row) =>
        (row.Entry?.Kind == ChatTimelineItemKind.Assistant ||
            row.IsAssistantRunStart && row.Kind is ReactorTimelineRowKind.Entry or ReactorTimelineRowKind.Activity) &&
        row.Props.ViewportWidth >= ChatVisuals.AvatarBreakpoint
            ? ChatVisuals.AvatarSlot : 0;

    public static string SyntheticRowKey(ChatTimelinePresentationContext props, string id, ChatTimelineItemKind kind) =>
        $"thread:{props.SessionId ?? "none"}|generation:{props.TimelineGeneration}|kind:{kind}|synthetic:{id}";

    private static IReadOnlyList<ReactorTimelineRow> BuildRows(ReactorChatTimelineProps props)
    {
        if (props.Mode == ReactorChatTimelineMode.Loading)
            return [ReactorTimelineRow.Loading(props)];

        if (props.Mode == ReactorChatTimelineMode.HistoryUnavailable)
            return [ReactorTimelineRow.HistoryUnavailable(props)];

        if (props.Mode == ReactorChatTimelineMode.Empty)
            return [];

        var rows = new List<ReactorTimelineRow>(props.Timeline.Entries.Count + 2);
        if (props.Timeline.HasMoreHistory)
            rows.Add(ReactorTimelineRow.LoadEarlier(props));

        var chronologicalEntries = ChatReasoningPresentation.Project(
            props.Timeline.Entries, props.Timeline.ReasoningStream,
            props.Timeline.ShowReasoning, props.Timeline.ShowReasoningPreview, props.Timeline.EntryMetadata);
        // Repeated identical run failures collapse into the latest row before turn projection.
        var failureStreaks = ChatNoticePresentation.ProjectRunFailureStreaks(
            chronologicalEntries, props.Timeline.EntryMetadata);
        if (failureStreaks.HiddenEntryIds.Count > 0)
            chronologicalEntries = chronologicalEntries
                .Where(entry => !failureStreaks.HiddenEntryIds.Contains(entry.Id))
                .ToList();
        var latestAssistantEntryId = chronologicalEntries
            .LastOrDefault(static entry => entry.Kind == ChatTimelineItemKind.Assistant)
            ?.Id;

        var projectedRows = ChatToolActivityPresentation.Project(
            chronologicalEntries,
            props.Timeline.SessionId,
            props.Timeline.TimelineGeneration,
            metadata: props.Timeline.EntryMetadata,
            runWorking: props.Timeline.ShowThinkingIndicator || props.Timeline.RunProgress is not null,
            session: props.Timeline.Session,
            showToolCalls: props.Timeline.ShowToolCalls);
        var assistantRunPositions = ChatTimelineAssistantRuns.Describe(
            projectedRows,
            entry => entry.Presentation ?? props.Timeline.EntryMetadata?.GetValueOrDefault(entry.Id)?.Presentation,
            // Only a generating turn hides its footer; waiting for subagents or an answer does not.
            lastRunWorking: props.Timeline.ShowThinkingIndicator || props.Timeline.RunProgress is { Active: true });
        for (var index = 0; index < projectedRows.Count; index++)
        {
            var projectedRow = projectedRows[index];
            if (projectedRow.IsActivityGroup)
            {
                rows.Add(ReactorTimelineRow.FromActivity(props, projectedRow, assistantRunPositions[index]));
                continue;
            }

            var entry = projectedRow.Entry!;
            rows.Add(ReactorTimelineRow.FromEntry(
                props,
                entry,
                string.Equals(entry.Id, latestAssistantEntryId, StringComparison.Ordinal),
                assistantRunPositions[index]) with { NoticeRepeatCount = failureStreaks.RepeatCount(entry.Id) });
        }

        if (props.Timeline.ShowThinkingIndicator || props.Timeline.RunProgress is not null)
            rows.Add(ReactorTimelineRow.Thinking(props));

        return rows;
    }

    private static Element BuildRow(
        ReactorTimelineRow row,
        string? speakingEntryId,
        string? hoveredEntryKey,
        string? focusedEntryKey,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered,
        Action<string?> setFocusedEntryKey,
        ChatToolActivityExpansionState toolActivityExpansionState,
        bool animateEntrance,
        Action<FrameworkElement> mountLoadingPulse)
    {
        var content = BuildRowContent(row, speakingEntryId, hoveredEntryKey, focusedEntryKey, toggleSpeechAsync,
            setEntryHovered, setFocusedEntryKey, toolActivityExpansionState, animateEntrance, mountLoadingPulse);
        // Assistant messages place their own avatar. Tool rows, groups and reasoning keep one grid
        // shape, so gaining or losing the run's avatar never remounts the row and its state.
        if (row.Entry?.Kind == ChatTimelineItemKind.Assistant ||
            row.Kind is not (ReactorTimelineRowKind.Entry or ReactorTimelineRowKind.Activity) ||
            row.Kind == ReactorTimelineRowKind.Entry &&
            row.Entry?.Kind is not (ChatTimelineItemKind.ToolCall or ChatTimelineItemKind.Reasoning))
            return content;
        var gutter = AvatarGutter(row);
        return Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            (gutter > 0 ? BuildAssistantAvatarSlot(row) : Empty()).Grid(column: 0).VAlign(VerticalAlignment.Top),
            content.Grid(column: 1),
            Border(Empty()).Width(gutter)
                .Set(border => border.IsHitTestVisible = false)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                .Grid(column: 2))
            .Margin(0, row.IsAssistantRunStart ? ChatVisuals.TurnGap : 0, 0, 0)
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static Element BuildRowContent(
        ReactorTimelineRow row,
        string? speakingEntryId,
        string? hoveredEntryKey,
        string? focusedEntryKey,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered,
        Action<string?> setFocusedEntryKey,
        ChatToolActivityExpansionState toolActivityExpansionState,
        bool animateEntrance,
        Action<FrameworkElement> mountLoadingPulse) => row.Kind switch
    {
        ReactorTimelineRowKind.Loading => BuildLoading(row, mountLoadingPulse),
        ReactorTimelineRowKind.HistoryUnavailable => BuildHistoryUnavailable(row),
        ReactorTimelineRowKind.LoadEarlier => BuildLoadEarlier(row),
        ReactorTimelineRowKind.Thinking => BuildThinking(row),
        ReactorTimelineRowKind.Activity when row.Activity is { } activity =>
            ToolCallCardRenderer.BuildActivity(
                row.Props.Timeline,
                activity,
                toolActivityExpansionState,
                entry => BuildEntry(
                    ReactorTimelineRow.FromEntry(row.Props, entry, false, default) with { IsWorkEntry = true },
                    entry, speakingEntryId, hoveredEntryKey, focusedEntryKey, toggleSpeechAsync,
                    setEntryHovered, setFocusedEntryKey,
                    includeAvatar: false),
                animateEntrance: animateEntrance),
        _ when row.Entry is { } entry => BuildEntry(
            row,
            entry,
            speakingEntryId,
            hoveredEntryKey,
            focusedEntryKey,
            toggleSpeechAsync,
            setEntryHovered,
            setFocusedEntryKey,
            animateEntrance: animateEntrance),
        _ => Empty(),
    };

    private static Element BuildLoading(ReactorTimelineRow row, Action<FrameworkElement> mountLoadingPulse)
    {
        var available = Math.Max(32, ChatVisuals.ProseWidth(row.Props.ViewportWidth));
        var placeholders = new[] { 260d, 180d, 320d, 140d }
            .Select(width => Border(Empty())
                .Width(Math.Min(width, available))
                .Height(12)
                .CornerRadius(4)
                .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                .HAlign(width is 180d or 140d
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left))
            .Cast<Element>()
            .ToArray();

        return VStack(12,
                Text(LocalizedOrDefault("Chat_Timeline_Loading", "Loading messages..."), 12,
                    FontWeights.Normal, "ChatSecondaryTextBrush"),
                VStack(12, placeholders)
                    .AutomationId("ChatTimelineLoadingSkeleton")
                    .OnMount(mountLoadingPulse)
                    .OnUnmount(StopLoadingPulse))
            .Margin(0, 32)
            .HAlign(HorizontalAlignment.Stretch)
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement,
        Microsoft.UI.Xaml.Media.Animation.Storyboard> s_loadingPulses = new();

    // Web's history skeleton shimmers on a 2 s ease-in-out loop; mirror it with an opacity pulse.
    private static void StartLoadingPulse(FrameworkElement element)
    {
        StopLoadingPulse(element);
        var pulse = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = .45,
            Duration = TimeSpan.FromSeconds(1),
            AutoReverse = true,
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.SineEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(pulse, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(pulse, "Opacity");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(pulse);
        s_loadingPulses.AddOrUpdate(element, storyboard);
        element.Unloaded -= OnLoadingPulseUnloaded;
        element.Unloaded += OnLoadingPulseUnloaded;
        storyboard.Begin();
    }

    private static void OnLoadingPulseUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element)
            StopLoadingPulse(element);
    }

    private static void StopLoadingPulse(FrameworkElement element)
    {
        if (!s_loadingPulses.TryGetValue(element, out var storyboard))
            return;
        storyboard.Stop();
        s_loadingPulses.Remove(element);
        element.Unloaded -= OnLoadingPulseUnloaded;
        element.Opacity = 1;
    }

    private static Element HistoryRetryButton(ReactorChatTimelineProps props)
    {
        var label = LocalizedOrDefault("Chat_History_Retry", "Retry");
        return Button(HStack(8,
                TextBlock(FluentIconCatalog.Refresh)
                    .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .FontSize(14)
                    .VAlign(VerticalAlignment.Center)
                    .AccessibilityView(AccessibilityView.Raw),
                TextBlock(label).VAlign(VerticalAlignment.Center)),
                () => props.OnRetryHistory?.Invoke())
            .IsEnabled(props.OnRetryHistory is not null)
            .AutomationName(label)
            .AutomationId("ChatHistoryRetry");
    }

    // Web chat-conversation-history-error.tsx: full stage when nothing is cached.
    private static Element BuildHistoryUnavailable(ReactorTimelineRow row)
    {
        var title = LocalizedOrDefault("Chat_History_UnavailableTitle", "Couldn't load this conversation");
        return VStack(12,
                TextBlock(FluentIconCatalog.StatusWarn)
                    .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .FontSize(32)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                    .HAlign(HorizontalAlignment.Center)
                    .AccessibilityView(AccessibilityView.Raw),
                Text(title, 20, FontWeights.SemiBold)
                    .HAlign(HorizontalAlignment.Center)
                    .TextAlignment(TextAlignment.Center)
                    .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2),
                Text(LocalizedOrDefault("Chat_History_UnavailableBody", "Try again to load its messages."),
                        14, FontWeights.Normal, "TextFillColorSecondaryBrush")
                    .HAlign(HorizontalAlignment.Center)
                    .TextAlignment(TextAlignment.Center),
                HistoryRetryButton(row.Props).HAlign(HorizontalAlignment.Center))
            .Margin(0, 48)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("ChatHistoryUnavailable")
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
    }

    // Web's inline failure keeps the cached transcript and adds a polite status with Retry.
    private static Element BuildHistoryFailedBanner(ReactorChatTimelineProps props, double viewportWidth) =>
        Border(HStack(12,
                Text(LocalizedOrDefault("Chat_History_UnavailableTitle", "Couldn't load this conversation"),
                        13, FontWeights.Normal, "TextFillColorSecondaryBrush")
                    .VAlign(VerticalAlignment.Center),
                HistoryRetryButton(props)))
            .Padding(12, 8)
            .Margin(ChatVisuals.Gutter(viewportWidth) + ChatVisuals.ProseInset, 8)
            .MaxWidth(ChatVisuals.ReadingWidth)
            .CornerRadius(8)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("ChatHistoryFailedStatus")
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

    private static Element BuildLoadEarlier(ReactorTimelineRow row)
    {
        var label = row.Props.Timeline.LoadingOlderHistory
            ? LocalizedOrDefault("Chat_Timeline_Loading", "Loading messages...")
            : LocalizedOrDefault("Chat_Timeline_LoadEarlier", "Load earlier messages");
        return Button(label, async () =>
            {
                if (row.Props.Timeline.OnLoadMoreHistory is { } load)
                    await load();
            })
            .IsEnabled(!row.Props.Timeline.LoadingOlderHistory)
            .Margin(0, 8)
            .HAlign(HorizontalAlignment.Center)
            .AutomationId("ChatLoadEarlier")
            .AutomationName(label);
    }

    private static Element BuildThinking(ReactorTimelineRow row)
    {
        if (row.Props.Timeline.RunProgress is { } progress)
            return Component<ReactorRunProgress, ReactorRunProgressProps>(new(progress));
        var format = LocalizedOrDefault("Chat_Timeline_AssistantThinkingFormat", "{0} is thinking…");
        return ChatMotion.Host(Text(
                string.Format(format, row.Props.Timeline.AssistantSenderLabel),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush")
            .FontStyle(global::Windows.UI.Text.FontStyle.Italic)
            .WithTextShimmer(true)
            .Margin(0, 12))
            .EnterOnMount(WorkingIndicatorRise, WorkingIndicatorMilliseconds, fade: true, enabled: !row.Props.ReducedMotion);
    }

    private static Element BuildEntry(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        string? speakingEntryId,
        string? hoveredEntryKey,
        string? focusedEntryKey,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered,
        Action<string?> setFocusedEntryKey,
        bool includeAvatar = true,
        bool animateEntrance = false)
    {
        var isHovered = string.Equals(hoveredEntryKey, row.Key, StringComparison.Ordinal);
        var isFocused = string.Equals(focusedEntryKey, row.Key, StringComparison.Ordinal);
        var content = entry.Kind switch
        {
            ChatTimelineItemKind.User => BuildUser(row, entry, isHovered, isFocused, animateEntrance),
            ChatTimelineItemKind.Assistant => BuildAssistant(
                row,
                entry,
                string.Equals(speakingEntryId, entry.Id, StringComparison.Ordinal),
                isHovered || isFocused,
                toggleSpeechAsync,
                includeAvatar),
            ChatTimelineItemKind.ToolCall => ToolCallCardRenderer.BuildStandalone(row.Props.Timeline, entry),
            ChatTimelineItemKind.Reasoning => Component<ReactorReasoningDisclosure, ReactorReasoningDisclosureProps>(new(
                    row.Props.Timeline, entry,
                    ChatReasoningPresentation.IsActive(entry, row.Props.Timeline.ReasoningStream,
                        row.Props.Timeline.ActiveReasoningId)))
                // Like tool rows, collapse-all remounts the disclosure so its local open state resets too.
                .WithKey($"reasoning:{entry.Id}:collapse:{row.Props.Timeline.ToolCallsCollapseVersion}"),
            ChatTimelineItemKind.PermissionRequest => BuildPermission(row, entry),
            ChatTimelineItemKind.Status => BuildStatus(row, entry),
            _ => BuildGenericStatus(entry),
        };
        if (entry.Kind is not (ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant))
            return content;

        return Border(content)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .OnPointerEntered((_, _) => setEntryHovered(row.Key, true))
            .OnPointerExited((_, _) => setEntryHovered(row.Key, false))
            .OnGotFocus((_, _) => setFocusedEntryKey(row.Key))
            .OnLostFocus((_, _) => setFocusedEntryKey(null));
    }

    private static Element BuildUser(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isHovered,
        bool isFocused,
        bool animateEntrance)
    {
        var (messageText, legacyAttachments) = ParseAttachments(entry.Text);
        var attachments = row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var metadata) == true &&
            metadata.Attachments is { Count: > 0 } structuredAttachments
                ? structuredAttachments
                : legacyAttachments;
        var accessibleText = BuildAccessibleUserText(messageText, attachments);
        var showFooter = isHovered || isFocused;
        var content = attachments.Select(BuildAttachment).ToList();
        if (messageText.Length > 0)
        {
            content.Add(TextBlock(messageText)
                .TextWrapping(TextWrapping.Wrap)
                .FontSize(ChatVisuals.BodySize)
                .FontWeight(FontWeights.Normal)
                .Foreground(Theme.Ref("ChatUserTextBrush"))
                .FontFamily(ChatVisuals.BodyFontFamily)
                // Natural font metrics scale with accessibility text size and add no trailing leading.
                .LineHeight(0)
                .Set(block => block.LineStackingStrategy = LineStackingStrategy.MaxHeight)
                .IsTextSelectionEnabled(true)
                // The bubble is outside the Markdown restyle pass, so it follows a theme switch while mounted.
                .OnMountAdd(control => ChatThemeChanges.Watch(control, () => ChatVisuals.ApplyBodyFont(control)))
                .OnUnmountAdd(control =>
                {
                    ChatThemeChanges.Unwatch(control);
                    ChatVisuals.ClearFontRole(control);
                }));
        }

        var bubble = Border(VStack(8, content.ToArray()))
            .Background(Theme.Ref("ChatUserBrush"))
            .CornerRadius(ChatVisuals.SurfaceRadius)
            .Padding(16, 12)
            .MaxWidth(720)
            .HAlign(HorizontalAlignment.Right);
        // Like web, only the bubble of a newly sent prompt rises in; it stays opaque and its row
        // geometry never moves.
        var bubbleHost = ChatMotion.Host(bubble)
            .EnterOnMount(UserEntranceRise, UserEntranceMilliseconds, fade: false, enabled: animateEntrance);

        return VStack(
                8,
                bubbleHost,
                WithMessageFooterFade(HStack(
                        8,
                        UserMetadata(row, entry),
                        CopyAction(accessibleText, row.Key, row.Props.TryCopyText)), showFooter, row.Props.ReducedMotion)
                    .AutomationId($"ChatMessageFooter_{entry.Id}")
                    .Margin(16, 2, 4, 0)
                    .HAlign(HorizontalAlignment.Right))
            .Margin(32, ChatVisuals.CompactGap, 0, ChatVisuals.CompactGap)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(accessibleText);
    }

    private static Element BuildAssistant(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isSpeaking,
        bool isHovered,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        bool includeAvatar)
    {
        var content = new List<Element>();
        ChatEntryMetadata? metadata = null;
        if (!string.IsNullOrWhiteSpace(entry.Text))
            content.Add(BuildSafeMarkdown(entry.Text, row.Props.TryCopyText));
        if (row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var resolvedMetadata) == true)
            metadata = resolvedMetadata;
        if (metadata?.AssistantContent is { Media.Count: > 0 } assistantContent)
        {
            var renderPlan = ChatAssistantContentProjector.BuildRenderPlan(assistantContent.Media);
            content.AddRange(renderPlan.Media.Select(media =>
                ChatAssistantMediaRenderer.Render(
                    media,
                    row.Props.Timeline.SessionId,
                    row.Props.Timeline.ResolveAssistantMediaAsync,
                    row.Props.Timeline.DownloadAssistantFileAsync,
                    row.Props.Timeline.OnOpenAssistantFile)));
            if (renderPlan.OmittedImages > 0)
            {
                content.Add(TextBlock(string.Format(
                        ChatAssistantMediaRenderer.LocalizedOrDefault(
                            "Chat_AssistantMedia_ImagesOmitted",
                            "{0} more images not shown"),
                        renderPlan.OmittedImages))
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText));
            }
        }
        if (content.Count == 0)
            content.Add(BuildSafeMarkdown(string.Empty));

        var bubble = Border(VStack(ChatVisuals.MessageBlockGap, content.ToArray()))
            .Padding(0)
            .HAlign(HorizontalAlignment.Stretch);

        var turnInset = ChatVisuals.TurnGap;
        // A reply never enters: like web, its text is visible on its first frame and extends in place.
        return Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                (includeAvatar ? BuildAssistantAvatarSlot(row) : Empty())
                    .Grid(column: 0)
                    .VAlign(VerticalAlignment.Top),
                VStack(
                        4,
                        bubble,
                        // Like the web run frame, only the run's final answer owns the actions footer,
                        // so interleaved paragraphs keep no hidden footer gap. A message being read
                        // aloud keeps its Stop control until playback ends.
                        (entry.Presentation ?? metadata?.Presentation)?.Phase == "commentary" ||
                            !row.IsAssistantRunEnd && !row.IsWorkEntry && !isSpeaking
                            ? Empty()
                            : BuildAssistantFooter(
                            row,
                            entry,
                            isSpeaking,
                            isHovered,
                            toggleSpeechAsync))
                    .HAlign(HorizontalAlignment.Stretch)
                    .Grid(column: 1),
                Border(Empty()).Width(includeAvatar ? AvatarGutter(row) : 0)
                    .Set(border => border.IsHitTestVisible = false)
                    .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                    .Grid(column: 2))
            .Margin(0, row.IsAssistantRunStart ? turnInset : 4, 0, row.IsAssistantRunEnd ? turnInset : 4)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName($"{row.Props.AssistantIdentity?.DisplayName ?? row.Props.Timeline.AssistantSenderLabel}: {BuildAccessibleAssistantText(entry.Text, metadata?.AssistantContent)}");
    }

    private static string BuildAccessibleAssistantText(
        string? text,
        ChatAssistantContentPresentation? content)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
            lines.Add(text);
        if (content is not null)
        {
            var attachmentLabel = ChatAssistantMediaRenderer.LocalizedOrDefault(
                "Chat_AssistantMedia_MediaAttachment",
                "Media attachment");
            lines.AddRange(content.Media.Select(media =>
                $"{ChatAssistantMediaRenderer.DisplayName(media)}. {attachmentLabel}"));
        }
        return string.Join('\n', lines);
    }

    private static Element BuildAssistantAvatarSlot(ReactorTimelineRow row)
    {
        if (row.Props.ViewportWidth < ChatVisuals.AvatarBreakpoint)
            return Empty();

        if (!row.IsAssistantRunStart)
            return Border(Empty())
                .Size(ChatVisuals.AvatarSize, ChatVisuals.AvatarSize)
                .Margin(0, 0, ChatVisuals.AvatarGap, 0)
                .VAlign(VerticalAlignment.Top);

        // Same identity rules as the welcome header and sidebar: image, text avatar, then generated face.
        var agent = ChatWelcomePresentation.AgentIdentity(
            row.Props.AssistantCatalog, row.Props.AssistantAgentId,
            row.Props.AssistantIdentity, row.Props.AssistantRosterAgent);
        return NativeChatWelcomeAvatarElement.NativeChatWelcomeAvatar()
            .Set(avatar => avatar.Update(agent, ChatVisuals.AvatarSize, row.Props.AssistantAvatarImageSource))
            .Size(ChatVisuals.AvatarSize, ChatVisuals.AvatarSize)
            .Margin(0, 0, ChatVisuals.AvatarGap, 0)
            .VAlign(VerticalAlignment.Top)
            .AutomationId("ChatAssistantAvatar")
            .AutomationName(agent.Name);
    }

    // Links follow the web chat: http/https anchors (including bare and www.
    // URLs) are real WinUI hyperlinks in the Windows accent color, without an
    // underline, that open in the default browser. Reactor admits only http/https/mailto; any other
    // scheme renders as plain label text. Images and raw HTML stay inert.
    // Realized rows refresh on every streamed delta; the component's props gate skips
    // sanitizing, parsing and rebuilding messages whose text has not changed.
    internal static Element BuildSafeMarkdown(
        string? text, Func<string, bool>? tryCopy = null, bool compactSecondaryReading = false) =>
        Component<ChatMarkdownView, ChatMarkdownProps>(new(text, tryCopy, compactSecondaryReading));

    internal static Element RenderSafeMarkdown(string? text, Func<string, bool>? tryCopy, bool compactSecondaryReading)
    {
        var sanitized = ChatMarkdownSanitizer.Sanitize(text ?? string.Empty, preserveWebLinks: true);
        var parts = ChatMarkdownDisclosures.Parse(sanitized);
        return ChatMarkdownPresentation.Disclosures(
            parts, markdown => BuildInertMarkdown(markdown, tryCopy, compactSecondaryReading));
    }

    private static Element BuildInertMarkdown(string text, Func<string, bool>? tryCopy, bool compactSecondaryReading)
    {
        var options = new MarkdownOptions
        {
            Heading = ChatMarkdownPresentation.Heading,
            Paragraph = ChatMarkdownPresentation.Paragraph,
            UnorderedList = items => ChatMarkdownPresentation.List(items, compactSecondaryReading),
            OrderedList = (_, items) => ChatMarkdownPresentation.List(items, compactSecondaryReading),
            ListItem = row => ChatMarkdownPresentation.ListItem(row, compactSecondaryReading),
            ThematicBreak = ChatMarkdownPresentation.ThematicBreak,
            BlockQuote = ChatMarkdownPresentation.BlockQuote,
            TableHeaderBackground = null,
            TableCellPadding = new Thickness(ChatVisuals.TableCellHorizontalInset, ChatVisuals.TableCellVerticalInset,
                ChatVisuals.TableCellHorizontalInset, ChatVisuals.TableCellVerticalInset),
            CodeBlock = (code, language) => ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy),
            CodeFontFamily = ChatVisuals.CodeFontFamily,
            // GitHub dialect (tables, strikethrough, task lists, URL/www/email autolinks)
            // without HTML, plus the web chat's `breaks: true` single-newline line breaks.
            ParserFlags = MarkdownParserFlags.DialectGitHub | MarkdownParserFlags.NoHtml |
                MarkdownParserFlags.HardSoftBreaks,
            Image = (alt, _) => InertText(
                string.IsNullOrWhiteSpace(alt) ? "[Image]" : $"[Image: {alt}]", compactSecondaryReading),
            HtmlBlock = raw => InertText(
                ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(raw), compactSecondaryReading),
        };

        return ChatMarkdownPresentation.WithInlineStyling(
            ChatMarkdownPresentation.MessageBlocks(
                Microsoft.UI.Reactor.Advanced.Factories.Markdown(text, options), compactSecondaryReading),
            compactSecondaryReading);
    }

    private static Element InertText(string label, bool compactSecondaryReading) =>
        Text(label,
                compactSecondaryReading ? ChatVisuals.ReasoningSize : ChatVisuals.BodySize,
                FontWeights.Normal,
                "ChatProseTextBrush")
            .FontFamily(ChatVisuals.BodyFontFamily)
            .IsTextSelectionEnabled(true);

    private static Element BuildAssistantFooter(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isSpeaking,
        bool isHovered,
        Func<ChatTimelineItem, Task> toggleSpeechAsync)
    {
        var children = new List<Element>();
        var sender = row.Props.AssistantIdentity?.DisplayName ?? row.Props.Timeline.AssistantSenderLabel;
        var metadataText = FooterText(row, entry);
        var identity = string.IsNullOrWhiteSpace(metadataText) ? sender : $"{sender} · {metadataText}";
        children.Add(CopyAction(entry.Text, row.Key, row.Props.TryCopyText));

        if (row.Props.Timeline.OnReadAloud is not null || row.Props.Timeline.OnStopSpeaking is not null)
        {
            var label = isSpeaking
                ? LocalizedOrDefault("Chat_Assistant_Action_Stop", "Stop")
                : LocalizedOrDefault("Chat_Assistant_Action_ReadAloud", "Read aloud");
            children.Add(CompactIconAction(
                isSpeaking ? "\uE71A" : "\uE767",
                label,
                () => _ = toggleSpeechAsync(entry),
                isHovered));
        }

        var showFooter = isHovered || isSpeaking;
        return WithMessageFooterFade(Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                Text(identity, 12, FontWeights.Normal, "ChatSecondaryTextBrush")
                    .MaxWidth(Math.Max(80, ChatVisuals.ProseWidth(row.Props.ViewportWidth)
                        - children.Count * 36 - 8))
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .TextWrapping(TextWrapping.NoWrap)
                    .VAlign(VerticalAlignment.Center)
                    .ToolTip(identity)
                    .Grid(column: 0),
                HStack(4, children.ToArray()).Margin(8, 0, 0, 0)
                    .HAlign(HorizontalAlignment.Left).Grid(column: 1)), showFooter, row.Props.ReducedMotion)
            .AutomationId($"ChatMessageFooter_{entry.Id}")
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static NativeChatMessageFooterElement WithMessageFooterFade(Element content, bool visible, bool reducedMotion) =>
        NativeChatMessageFooterElement.NativeChatMessageFooter(content: content)
            .Opacity(visible ? 1 : 0)
            .OpacityTransition(reducedMotion ? TimeSpan.Zero : MessageFooterFadeDuration)
            .Set(footer => footer.IsHitTestVisible = visible);

    private static Element CopyAction(
        string? text,
        string identity,
        Func<string, bool>? tryCopy)
    {
        var label = LocalizedOrDefault("Chat_Assistant_Action_Copy", "Copy");
        return Component<ChatCopyButton, ChatCopyButtonProps>(new(
            identity, text ?? string.Empty, label, tryCopy));
    }

    private static Element CompactIconAction(
        string glyph,
        string label,
        Action onClick,
        bool isVisible)
    {
        return Button(
                TextBlock(glyph)
                    .FontSize(14)
                    .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .Set(text => text.IsTextScaleFactorEnabled = false)
                    .Foreground(Theme.SecondaryText),
                onClick)
            .Width(32)
            .Height(32)
            .MinWidth(32)
            .MinHeight(32)
            .Padding(0)
            .Resources(resources => resources
                .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
                .Set("ButtonBackgroundPressed", Theme.ControlFillTertiary)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
            .AutomationName(label)
            .ToolTip(label)
            .Opacity(isVisible ? 1 : 0.7)
            .IsTabStop(true)
            .Set(button => button.IsHitTestVisible = true);
    }

    internal static (string Message, IReadOnlyList<ChatAttachmentPresentation> Attachments) ParseAttachments(string? text)
    {
        const string imagePrefix = "\u200B🖼️ ";
        const string filePrefix = "\u200B📎 ";
        var messageLines = new List<string>();
        var attachments = new List<ChatAttachmentPresentation>();

        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(imagePrefix, StringComparison.Ordinal))
            {
                var name = trimmed[imagePrefix.Length..].Trim();
                if (name.Length > 0)
                    attachments.Add(new ChatAttachmentPresentation(
                        ChatAttachmentOrigin.Local,
                        name,
                        "application/octet-stream",
                        IsImage: true));
            }
            else if (trimmed.StartsWith(filePrefix, StringComparison.Ordinal))
            {
                var name = trimmed[filePrefix.Length..].Trim();
                if (name.Length > 0)
                    attachments.Add(new ChatAttachmentPresentation(
                        ChatAttachmentOrigin.Local,
                        name,
                        "application/octet-stream",
                        IsImage: false));
            }
            else
            {
                messageLines.Add(line);
            }
        }

        return (string.Join('\n', messageLines).Trim(), attachments);
    }

    internal static string BuildAccessibleUserText(
        string message,
        IReadOnlyList<ChatAttachmentPresentation> attachments)
    {
        var lines = new List<string>();
        if (message.Length > 0)
            lines.Add(message);
        lines.AddRange(attachments.Select(attachment =>
            $"{attachment.DisplayFileName} ({attachment.MimeType})"));
        return string.Join('\n', lines);
    }

    internal static Element BuildAttachment(ChatAttachmentPresentation attachment)
    {
        if (attachment.IsImage
            && ChatAttachmentPreviewResolver.TryGetBytes(
                attachment,
                out var bytes)
            && TryDecodeAttachmentBitmap(bytes) is { } bitmap)
        {
            const double maxWidth = 280;
            const double maxHeight = 200;
            var pixelWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : (int)maxWidth;
            var pixelHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : (int)maxHeight;
            var scale = Math.Min(Math.Min(maxWidth / pixelWidth, maxHeight / pixelHeight), 1.0);
            return Viewbox(Border(Empty())
                    .Background(new ImageBrush { ImageSource = bitmap, Stretch = Stretch.Uniform })
                    .Size(pixelWidth * scale, pixelHeight * scale)
                    .CornerRadius(8))
                .Set(viewbox =>
                {
                    viewbox.Stretch = Stretch.Uniform;
                    viewbox.StretchDirection = StretchDirection.DownOnly;
                })
                .MaxWidth(pixelWidth * scale).MaxHeight(pixelHeight * scale)
                .HAlign(HorizontalAlignment.Right)
                .AutomationName(attachment.DisplayFileName);
        }

        var glyph = Text(
                attachment.IsImage ? "\uEB9F" : "\uE8A5",
                16,
                FontWeights.Normal,
                "ChatTextBrush")
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .Set(text => text.IsTextScaleFactorEnabled = false)
            .Center();
        var glyphBackground = Border(glyph)
            .Size(32, 32)
            .CornerRadius(6)
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"));
        var name = Text(
                attachment.DisplayFileName,
                14,
                FontWeights.Normal,
                "ChatTextBrush")
            .TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .MaxWidth(240)
            .VAlign(VerticalAlignment.Center);

        var mimeType = Text(
                attachment.MimeType,
                12,
                FontWeights.Normal,
                "ChatSecondaryTextBrush")
            .TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .MaxWidth(240);

        return Border(Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                glyphBackground.Margin(0, 0, 8, 0).Grid(column: 0),
                VStack(4, name, mimeType).Grid(column: 1)))
            .Padding(8)
            .CornerRadius(8)
            .BorderThickness(1)
            .BorderBrush(Theme.Ref("ChatStrokeBrush"))
            .Background(Theme.Ref("ChatCardBrush"))
            .AutomationName($"{attachment.DisplayFileName}, {attachment.MimeType}");
    }

    private static BitmapImage? TryDecodeAttachmentBitmap(byte[] bytes)
    {
        if (s_attachmentBitmaps.TryGetValue(bytes, out var existing))
            return existing;

        try
        {
            var bitmap = ChatAttachmentBitmapDecoder.TryDecode(bytes);
            if (bitmap is null)
                return null;
            s_attachmentBitmaps.Add(bytes, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    internal static Element BuildReasoning(ChatTimelineItem entry, bool active, bool expanded, Action<bool> onExpanded,
        bool revealDetails = false, bool closing = false)
    {
        var header = LocalizedOrDefault("Chat_Run_Thinking", "Thinking");
        // Like a tool row, the Expander carries only the header and the thought renders below it.
        // It is built only while it shows, grows open when the reader opens it, and shrinks away
        // on close, so the rows below follow with layout instead of the stock content slide.
        Element details = expanded || closing
            ? ChatMotion.Host(ScrollViewer(BuildSafeMarkdown(entry.Text, compactSecondaryReading: true))
                    .MaxHeight(240).Margin(ChatVisuals.ToolRowTitleInset, 4, 0, 8)
                    .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled))
                .ExpandOnMount(ChatMotion.DisclosureRevealMilliseconds, revealDetails && expanded)
                .ShrinkWhen(closing && !expanded, ChatMotion.DisclosureCloseMilliseconds)
                .MinWidth(ChatVisuals.DisclosureDetailsMinWidth)
            : Empty();
        var expander = Expander(
                header,
                Empty(),
                (Optional<bool>)expanded,
                onExpanded)
            .HeaderContent(ToolCallCardRenderer.BuildDisclosureHeader(expanded, ChatVisuals.ActivityText(header, active)))
            .OnUnmount(ChatVisuals.StopToolRowShimmers)
            .OnUnmountAdd(ChatVisuals.RestoreHeaderOnlyExpander)
            .MinHeight(ChatVisuals.ToolRowMinHeight)
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderBrush(Theme.Ref("SubtleFillColorTransparentBrush"))
            .Resources(ChatVisuals.ToolRowExpanderResources)
            .Set(ChatVisuals.FlushHeaderOnlyExpander)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationName(header)
            .AutomationId("ChatReasoning");
        return VStack(0, expander.WithKey("reasoning-header"), details.WithKey("reasoning-details"))
            .Margin(0, 4, 0, 0);
    }

    private static Element BuildPermission(ReactorTimelineRow row, ChatTimelineItem entry)
    {
        var children = new List<Element>
        {
            Text(
                string.IsNullOrWhiteSpace(entry.IntentSummary)
                    ? LocalizedOrDefault("Chat_Permission_Title", "Permission requested")
                    : entry.IntentSummary,
                14,
                FontWeights.SemiBold),
        };
        var detail = entry.Text ?? string.Empty;

        if (entry.PermissionDecision == ChatPermissionDecision.Pending)
        {
            children.Add(Text(
                LocalizedOrDefault(
                   "Chat_Permission_Subtitle",
                   "Review the requested operation before allowing it."),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"));
            AddPermissionDetails(children, detail);

            var requestId = entry.PermissionRequestId ?? string.Empty;
            var actions = ChatPermissionActionKeys.NormalizeActions(entry.PermissionActions)
                .Select(actionKey =>
                {
                    var label = PermissionActionLabel(actionKey);
                    return (Element)Button(
                            label,
                            () => row.Props.Timeline.OnPermissionResponse?.Invoke(requestId, actionKey))
                        .IsEnabled(row.Props.Timeline.OnPermissionResponse is not null && requestId.Length > 0)
                        .AutomationName(label);
                })
                .ToArray();
            children.Add(row.Props.ViewportWidth < ChatVisuals.FooterBreakpoint ? VStack(8, actions) : HStack(8, actions));
            children.Add(Text(
                LocalizedOrDefault(
                    "Chat_Permission_Caption",
                    "Only allow operations you trust."),
                11,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"));
        }
        else
        {
            children.Add(Text(
                PermissionDecisionLabel(entry.PermissionDecision),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"));
            AddPermissionDetails(children, detail);
        }

        return Border(VStack(8, children.ToArray()))
            .Margin(0, 8)
            .Padding(16)
            .Background(BrushFor(
                "CardBackgroundFillColorDefaultBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .CornerRadius(12);
    }

    private static void AddPermissionDetails(List<Element> children, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return;

        children.Add(Border(Text(
                detail,
                12,
                FontWeights.Normal,
                "TextFillColorPrimaryBrush")
            .FontFamily(new FontFamily("Cascadia Code, Consolas"))
            .IsTextSelectionEnabled(true))
            .Padding(10, 8)
            .CornerRadius(6)
            .Background(BrushFor(
                "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1));
    }

    private static Element BuildStatus(ReactorTimelineRow row, ChatTimelineItem entry)
    {
        var noticeKind = ChatNoticePresentation.KindOf(entry, row.Props.Timeline.EntryMetadata);
        if (noticeKind != ChatNoticeKind.None)
            return ReactorChatNotices.Build(row, entry, noticeKind);

        var presentation = ChatCompactionPresenter.TryCreateForEntry(
            entry,
            row.Props.Timeline.EntryMetadata,
            LocalizedOrDefault("Chat_Compaction_Title", "Context compacted"),
            LocalizedOrDefault(
                "Chat_Compaction_MetricsFormat", "saved {0} tokens ({1}%)"));
        return presentation is null
            ? BuildGenericStatus(entry)
            : BuildCompaction(row, presentation);
    }

    internal static Element BuildGenericStatus(ChatTimelineItem entry)
    {
        var isError = entry.Tone == ChatTone.Error;
        return Border(Text(
                entry.Text ?? string.Empty,
                12,
                FontWeights.Normal,
                isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush")
            .TextAlignment(TextAlignment.Center))
            .Margin(0, 8)
            .Padding(12, 8)
            .HAlign(HorizontalAlignment.Center)
            .CornerRadius(12)
            .Background(Theme.Ref(isError ? "SystemFillColorCriticalBackgroundBrush" : "SubtleFillColorTertiaryBrush"));
    }

    private static Element BuildCompaction(
        ReactorTimelineRow row,
        ChatCompactionPresentation presentation) =>
        ReactorChatNotices.SystemLine(
                FluentIconCatalog.Check, presentation.Title, ReactorChatNotices.LabelMaxWidth(row), presentation.Detail)
            .Margin(0, 8)
            .AutomationName(presentation.AutomationName);

    internal static string FooterText(
        ReactorTimelineRow row,
        ChatTimelineItem entry)
    {
        ChatEntryMetadata? metadata = null;
        if (row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var resolvedMetadata) == true)
            metadata = resolvedMetadata;

        return metadata?.Timestamp?.ToLocalTime().ToString("h:mm tt") ?? string.Empty;
    }

    private static Element UserMetadata(
        ReactorTimelineRow row,
        ChatTimelineItem entry)
    {
        return Border(
            Text(
                FooterText(row, entry),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"))
            .VAlign(VerticalAlignment.Center)
            .Set(border => border.IsHitTestVisible = false);
    }

    internal static TextBlockElement Text(
        string text,
        double fontSize = 14,
        global::Windows.UI.Text.FontWeight? weight = null,
        string foregroundResource = "TextFillColorPrimaryBrush") =>
        TextBlock(text)
            .TextWrapping(TextWrapping.Wrap)
            .FontSize(fontSize)
            .FontWeight(weight ?? FontWeights.Normal)
            .Foreground(Theme.Ref(foregroundResource))
            .Set(block =>
            {
                block.LineHeight = fontSize * 1.5;
                block.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            });

    private static string PermissionActionLabel(string action) =>
        string.Equals(action, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase)
            ? LocalizedOrDefault("Chat_Permission_Allow", "Allow")
            : string.Equals(action, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase)
                ? LocalizedOrDefault("Chat_Permission_AllowAlways", "Always allow")
                : string.Equals(action, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase)
                    ? LocalizedOrDefault("Chat_Permission_Deny", "Deny")
                    : action;

    private static string PermissionDecisionLabel(ChatPermissionDecision decision) => decision switch
    {
        ChatPermissionDecision.Allowed => LocalizedOrDefault("Chat_Permission_DecisionAllowed", "Allowed"),
        ChatPermissionDecision.AllowedAlways => LocalizedOrDefault("Chat_Permission_DecisionAlwaysAllowed", "Always allowed"),
        ChatPermissionDecision.Denied => LocalizedOrDefault("Chat_Permission_DecisionDenied", "Denied"),
        _ => LocalizedOrDefault("Chat_Permission_DecisionExpired", "Expired"),
    };

    internal static string LocalizedOrDefault(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static Brush BrushFor(string resourceKey, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private static string StripMarkdownForSpeech(string text)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", " code block ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"`([^`]+)`", "$1");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"!\[[^\]]*\]\([^)]*\)", " image ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\[([^\]]+)\]\([^)]*\)", "$1");
        return System.Text.RegularExpressions.Regex.Replace(result, @"[*_#>]+", " ");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], BitmapImage>
        s_attachmentBitmaps = new();

}

internal sealed record ReactorTimelineRow(
    string Key,
    ReactorTimelineRowKind Kind,
    ReactorChatTimelineProps Props,
    ChatTimelineItem? Entry,
    ChatToolActivityRow? Activity = null,
    bool IsLatestAssistant = false,
    bool IsAssistantRunStart = false,
    bool IsAssistantRunEnd = false)
{
    /// <summary>A message inside an expanded Worked group, which keeps its own hover footer.</summary>
    public bool IsWorkEntry { get; init; }

    /// <summary>Identical consecutive run failures this row stands for; earlier rows are hidden.</summary>
    public int NoticeRepeatCount { get; init; } = 1;

    public static ReactorTimelineRow FromEntry(
        ReactorChatTimelineProps props,
        ChatTimelineItem entry,
        bool isLatestAssistant,
        ChatAssistantRunPosition assistantRunPosition) =>
        new(
            ReactorChatTimeline.RowKey(props.Timeline, entry),
            ReactorTimelineRowKind.Entry,
            props,
            entry,
            null,
            isLatestAssistant,
            assistantRunPosition.IsStart,
            assistantRunPosition.IsEnd);

    public static ReactorTimelineRow FromActivity(
        ReactorChatTimelineProps props,
        ChatToolActivityRow activity,
        ChatAssistantRunPosition assistantRunPosition = default) =>
        new(
            activity.Key,
            ReactorTimelineRowKind.Activity,
            props,
            null,
            activity,
            IsAssistantRunStart: assistantRunPosition.IsStart,
            IsAssistantRunEnd: assistantRunPosition.IsEnd);

    public static ReactorTimelineRow Thinking(ReactorChatTimelineProps props) =>
        new(
            ReactorChatTimeline.SyntheticRowKey(
                props.Timeline,
                "__thinking__",
                ChatTimelineItemKind.Assistant),
            ReactorTimelineRowKind.Thinking,
            props,
            null);

    public static ReactorTimelineRow LoadEarlier(ReactorChatTimelineProps props) =>
        new(
            ReactorChatTimeline.SyntheticRowKey(
                props.Timeline,
                "__load-earlier__",
                ChatTimelineItemKind.Status),
            ReactorTimelineRowKind.LoadEarlier,
            props,
            null);

    public static ReactorTimelineRow Loading(ReactorChatTimelineProps props) =>
        new("timeline:loading", ReactorTimelineRowKind.Loading, props, null);

    public static ReactorTimelineRow HistoryUnavailable(ReactorChatTimelineProps props) =>
        new("timeline:history-unavailable", ReactorTimelineRowKind.HistoryUnavailable, props, null);

}

internal enum ReactorTimelineRowKind
{
    Entry,
    Activity,
    Thinking,
    LoadEarlier,
    Loading,
    HistoryUnavailable,
}

internal sealed record ReactorReasoningDisclosureProps(
    ChatTimelinePresentationContext Timeline, ChatTimelineItem Entry, bool Active);

/// <summary>
/// Thinking disclosure styled like a tool row: a leading chevron that reflects the open state.
/// The choice lives in the tool-row expansion store, so a recycled row never inherits another
/// thought's state and collapse-all closes thinking with the tool rows.
/// </summary>
internal sealed class ReactorReasoningDisclosure : Component<ReactorReasoningDisclosureProps>
{
    public override Element Render()
    {
        var (localExpanded, setLocalExpanded) = UseState(false, threadSafe: true);
        var (_, setRenderVersion) = UseState(0, threadSafe: true);
        var renderVersion = UseRef(0);
        var revealDetails = UseRef(false);
        var closing = UseRef(false);
        var closeVersion = UseRef(0);
        var reducedMotion = ChatMotion.IsReducedMotion(UseReducedMotion());
        var timeline = Props.Timeline;
        var key = "reasoning|" + ChatToolActivityPresentation.ToolRowKey(
            timeline.SessionId, timeline.TimelineGeneration, Props.Entry.Id);
        var closeTimers = UseRef(new ChatDisclosureCloseTimerSet());
        var identity = (timeline.SessionId, timeline.TimelineGeneration, key);
        var previousIdentity = UseRef(identity);
        if (previousIdentity.Current != identity)
        {
            closeTimers.Current.CancelAll();
            closing.Current = false;
            revealDetails.Current = false;
            closeVersion.Current++;
            previousIdentity.Current = identity;
        }
        UseEffect((Func<Action>)(() => () => closeTimers.Current.CancelAll()),
            timeline.SessionId ?? string.Empty, timeline.TimelineGeneration, key);
        var expanded = timeline.ToolExpansionState?.IsExpanded(key, timeline.ToolCallsCollapseVersion) ?? localExpanded;
        return ReactorChatTimeline.BuildReasoning(Props.Entry, Props.Active, expanded, value =>
        {
            revealDetails.Current = true;
            timeline.ToolExpansionState?.SetExplicit(key, value, timeline.ToolCallsCollapseVersion);
            // Closing keeps the thought mounted while it shrinks, then releases it.
            var version = ++closeVersion.Current;
            closing.Current = !value && !reducedMotion && ChatMotion.AnimationsEnabled;
            closeTimers.Current.Cancel("reasoning");
            if (closing.Current)
            {
                closeTimers.Current.Schedule("reasoning",
                    TimeSpan.FromMilliseconds(ChatMotion.DisclosureCloseHoldMilliseconds), () =>
                {
                    if (closeVersion.Current != version)
                        return;
                    closing.Current = false;
                    setRenderVersion(++renderVersion.Current);
                });
            }
            setLocalExpanded(value);
            setRenderVersion(++renderVersion.Current);
        }, revealDetails.Current && !reducedMotion, closing.Current);
    }
}
