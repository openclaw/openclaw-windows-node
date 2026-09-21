using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Markdown;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
}

public sealed record ReactorChatTimelineProps(
    ReactorChatTimelineMode Mode,
    ChatTimelinePresentationContext Timeline,
    Action<string>? OnSuggestionPicked = null,
    bool SuggestionsDisabled = false,
    ReactorChatIdentity? AssistantIdentity = null,
    Action<string>? OnOpenCheckpoints = null,
    long HistoryRevision = 0,
    double ViewportWidth = 800,
    Func<string, bool>? TryCopyText = null);

public sealed record ReactorChatIdentity(
    string? DisplayName = null,
    string? Avatar = null,
    string? Emoji = null);

/// <summary>
/// Reactor-owned production timeline. Reactor's keyed ItemsView handles row
/// reconciliation, container realization, scrolling, and virtualization.
/// </summary>
public sealed class ReactorChatTimeline : Component<ReactorChatTimelineProps>
{
    public override Element Render()
    {
        var props = Props;
        var (speakingEntryId, setSpeakingEntryId) = UseState<string?>(null, threadSafe: true);
        var (hoveredEntryId, setHoveredEntryId) = UseState<string?>(null, threadSafe: true);
        var (viewportWidth, setViewportWidth) = UseState(800d);
        var (navigationVisible, setNavigationVisible) = UseState(false);
        var speechOperation = UseRef(0);
        var mounted = UseRef(true);
        var toolActivityExpansionState = UseRef<ChatToolActivityExpansionState>(new());
        var annotatedScrollBarRef = this.UseElementRef<WinUIAnnotatedScrollBar>();

        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () =>
            {
                mounted.Current = false;
                speechOperation.Current++;
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
        var initialTailRequestKey =
            $"{props.Timeline.SessionId ?? "none"}|{props.Timeline.TimelineGeneration}|{props.HistoryRevision}|{props.Timeline.ScrollToBottomToken}";
        var displayedTailKey = rows.Count > 0 ? rows[^1].Key : null;
        void SetEntryHovered(string entryId, bool isHovered)
        {
            if (isHovered)
            {
                if (!string.Equals(hoveredEntryId, entryId, StringComparison.Ordinal))
                    setHoveredEntryId(entryId);
            }
            else if (string.Equals(hoveredEntryId, entryId, StringComparison.Ordinal))
            {
                setHoveredEntryId(null);
            }
        }

        var itemsView = ItemsView(
            rows,
            static row => row.Key,
            (row, _) => ItemContainer(
                    Border(BuildRow(
                            row,
                            speakingEntryId,
                            hoveredEntryId,
                            ToggleSpeechAsync,
                            SetEntryHovered,
                            toolActivityExpansionState.Current))
                        .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                        .MaxWidth(ChatVisuals.ReadingWidth - 2 * ChatVisuals.ProseInset + 2 * AvatarGutter(row))
                        .Margin(ChatVisuals.Gutter(viewportWidth) + ChatVisuals.ProseInset - AvatarGutter(row), 0)
                        .OnPointerEntered((_, _) => SetEntryHovered(row.Entry?.Id ?? row.Key, true))
                        .OnPointerExited((_, _) => SetEntryHovered(row.Entry?.Id ?? row.Key, false))
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
            [GridSize.Star()],
            itemsView
                .BindVerticalScrollController(
                    annotatedScrollBarRef,
                    rows.Count - 1,
                    rows.Count,
                    initialTailRequestKey,
                    displayedTailKey)
                .Grid(column: 0)
                .Margin(0, ChatVisuals.TranscriptTopInset, 0, 0)
                .AutomationName("Chat messages")
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch),
            AnnotatedScrollBar()
                .Ref(annotatedScrollBarRef)
                .Width(ChatVisuals.RailWidth(viewportWidth))
                .MinWidth(0)
                .Opacity(navigationVisible ? 1 : 0)
                .OnGotFocus((_, _) => setNavigationVisible(true))
                .OnLostFocus((_, _) => setNavigationVisible(false))
                .Grid(column: 0)
                .HAlign(HorizontalAlignment.Right)
                .AutomationName("Chat message navigation"))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnPointerEntered((_, _) => setNavigationVisible(true))
            .OnPointerExited((_, _) => setNavigationVisible(false))
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

    private static double AvatarGutter(ReactorTimelineRow row) =>
        row.Entry?.Kind == ChatTimelineItemKind.Assistant && row.Props.ViewportWidth >= ChatVisuals.AvatarBreakpoint
            ? ChatVisuals.AvatarSlot : 0;

    public static string SyntheticRowKey(ChatTimelinePresentationContext props, string id, ChatTimelineItemKind kind) =>
        $"thread:{props.SessionId ?? "none"}|generation:{props.TimelineGeneration}|kind:{kind}|synthetic:{id}";

    private static IReadOnlyList<ReactorTimelineRow> BuildRows(ReactorChatTimelineProps props)
    {
        if (props.Mode == ReactorChatTimelineMode.Loading)
            return [ReactorTimelineRow.Loading(props)];

        if (props.Mode == ReactorChatTimelineMode.Empty)
            return [ReactorTimelineRow.Empty(props)];

        var rows = new List<ReactorTimelineRow>(props.Timeline.Entries.Count + 2);
        if (props.Timeline.HasMoreHistory)
            rows.Add(ReactorTimelineRow.LoadEarlier(props));

        var chronologicalEntries = props.Timeline.Entries;
        var assistantRunPositions = ChatTimelineAssistantRuns.Describe(chronologicalEntries);
        var assistantRunsByEntryId = new Dictionary<string, ChatAssistantRunPosition>(
            chronologicalEntries.Count,
            StringComparer.Ordinal);
        for (var index = 0; index < chronologicalEntries.Count; index++)
            assistantRunsByEntryId[chronologicalEntries[index].Id] = assistantRunPositions[index];
        var latestAssistantEntryId = chronologicalEntries
            .LastOrDefault(static entry => entry.Kind == ChatTimelineItemKind.Assistant)
            ?.Id;

        var projectedRows = ChatToolActivityPresentation.Project(
            chronologicalEntries,
            props.Timeline.SessionId,
            props.Timeline.TimelineGeneration,
            props.Timeline.ShowToolCalls);
        foreach (var projectedRow in projectedRows)
        {
            if (projectedRow.IsActivityGroup)
            {
                rows.Add(ReactorTimelineRow.FromActivity(props, projectedRow));
                continue;
            }

            var entry = projectedRow.Entry!;
            rows.Add(ReactorTimelineRow.FromEntry(
                props,
                entry,
                string.Equals(entry.Id, latestAssistantEntryId, StringComparison.Ordinal),
                assistantRunsByEntryId.TryGetValue(entry.Id, out var position) ? position : default));
        }

        if (props.Timeline.ShowThinkingIndicator)
            rows.Add(ReactorTimelineRow.Thinking(props));

        return rows;
    }

    private static Element BuildRow(
        ReactorTimelineRow row,
        string? speakingEntryId,
        string? hoveredEntryId,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered,
        ChatToolActivityExpansionState toolActivityExpansionState) => row.Kind switch
    {
        ReactorTimelineRowKind.Loading => BuildLoading(row),
        ReactorTimelineRowKind.Empty => BuildEmpty(row),
        ReactorTimelineRowKind.LoadEarlier => BuildLoadEarlier(row),
        ReactorTimelineRowKind.Thinking => BuildThinking(row),
        ReactorTimelineRowKind.Activity when row.Activity is { } activity =>
            ToolCallCardRenderer.BuildActivity(
                row.Props.Timeline,
                activity,
                toolActivityExpansionState),
        _ when row.Entry is { } entry => BuildEntry(
            row,
            entry,
            speakingEntryId,
            hoveredEntryId,
            toggleSpeechAsync,
            setEntryHovered),
        _ => Empty(),
    };

    private static Element BuildLoading(ReactorTimelineRow row)
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
                VStack(12, placeholders))
            .Margin(0, 32)
            .HAlign(HorizontalAlignment.Stretch)
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
    }

    private static Element BuildEmpty(ReactorTimelineRow row)
    {
        var children = new List<Element>
        {
            Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                .Size(40, 40)
                .AutomationName("OpenClaw")
                .HAlign(HorizontalAlignment.Center),
            Text(
                    LocalizedOrDefault("Chat_ZeroState_WelcomeTitle", "Welcome to OpenClaw"),
                    20,
                    FontWeights.SemiBold)
                .TextAlignment(TextAlignment.Center)
                .HAlign(HorizontalAlignment.Stretch),
            Text(
                    LocalizedOrDefault("Chat_ZeroState_WelcomeSubtitle", "How can I help you today?"),
                    14,
                    FontWeights.Normal,
                    "TextFillColorSecondaryBrush")
                .HAlign(HorizontalAlignment.Center),
        };

        foreach (var suggestion in new[]
        {
            "Say hi 👋",
            "What can you do?",
            "Give me a quick tour of OpenClaw",
        })
        {
            children.Add(Button(TextBlock(suggestion).TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center),
                    () => row.Props.OnSuggestionPicked?.Invoke(suggestion))
                .IsEnabled(!row.Props.SuggestionsDisabled)
                .MinHeight(40)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationName(suggestion));
        }

        return VStack(12, children.ToArray())
            .Margin(0, 48, 0, 24)
            .MaxWidth(400)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
    }

    private static Element BuildLoadEarlier(ReactorTimelineRow row)
    {
        var label = LocalizedOrDefault("Chat_Timeline_LoadEarlier", "Load earlier messages");
        return Button(label, () => row.Props.Timeline.OnLoadMoreHistory?.Invoke())
            .Margin(0, 8)
            .HAlign(HorizontalAlignment.Center)
            .AutomationName(label);
    }

    private static Element BuildThinking(ReactorTimelineRow row)
    {
        var format = LocalizedOrDefault("Chat_Timeline_AssistantThinkingFormat", "{0} is thinking…");
        return Text(
                string.Format(format, row.Props.Timeline.AssistantSenderLabel),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush")
            .FontStyle(global::Windows.UI.Text.FontStyle.Italic)
            .Margin(0, 12);
    }

    private static Element BuildEntry(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        string? speakingEntryId,
        string? hoveredEntryId,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered) => entry.Kind switch
    {
        ChatTimelineItemKind.User => BuildUser(
            row,
            entry,
            string.Equals(hoveredEntryId, entry.Id, StringComparison.Ordinal),
            setEntryHovered),
        ChatTimelineItemKind.Assistant => BuildAssistant(
            row,
            entry,
            string.Equals(speakingEntryId, entry.Id, StringComparison.Ordinal),
            string.Equals(hoveredEntryId, entry.Id, StringComparison.Ordinal),
            toggleSpeechAsync,
            setEntryHovered),
        ChatTimelineItemKind.ToolCall => ToolCallCardRenderer.BuildStandalone(row.Props.Timeline, entry),
        ChatTimelineItemKind.Reasoning => BuildReasoning(entry),
        ChatTimelineItemKind.PermissionRequest => BuildPermission(row, entry),
        ChatTimelineItemKind.Status => BuildStatus(row, entry),
        _ => BuildGenericStatus(entry),
    };

    private static Element BuildUser(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isHovered,
        Action<string, bool> setEntryHovered)
    {
        var (messageText, legacyAttachments) = ParseAttachments(entry.Text);
        var attachments = row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var metadata) == true &&
            metadata.Attachments is { Count: > 0 } structuredAttachments
                ? structuredAttachments
                : legacyAttachments;
        var accessibleText = BuildAccessibleUserText(messageText, attachments);
        var content = attachments.Select(BuildAttachment).ToList();
        if (messageText.Length > 0)
        {
            content.Add(Text(
                    messageText,
                    14,
                    FontWeights.Normal,
                    "ChatUserTextBrush")
                .IsTextSelectionEnabled(true));
        }

        var bubble = Border(VStack(8, content.ToArray()))
            .Background(Theme.Ref("ChatUserBrush"))
            .CornerRadius(ChatVisuals.SurfaceRadius)
            .Padding(16, 12)
            .MaxWidth(720)
            .HAlign(HorizontalAlignment.Right);

        return VStack(
                bubble,
                HStack(
                        8,
                        row.Props.Timeline.ShowToolCalls
                            ? UserMetadata(row, entry, isHovered)
                            : Empty(),
                        CopyAction(accessibleText, setEntryHovered, entry.Id, row.Key, row.Props.TryCopyText))
                    .Margin(16, 2, 4, 0)
                    .HAlign(HorizontalAlignment.Right))
            .Margin(32, 8, 0, row.Props.ViewportWidth < ChatVisuals.FooterBreakpoint ? 4 : 8)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(accessibleText);
    }

    private static Element BuildAssistant(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isSpeaking,
        bool isHovered,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered)
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
                    row.Props.Timeline.ResolveAssistantMediaAsync)));
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

        var bubble = Border(VStack(ChatVisuals.BlockGap, content.ToArray()))
            .Padding(0, 4)
            .HAlign(HorizontalAlignment.Stretch);

        var turnInset = row.Props.ViewportWidth < ChatVisuals.FooterBreakpoint ? 8 : 12;
        return Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                BuildAssistantAvatarSlot(row)
                    .Grid(column: 0)
                    .VAlign(VerticalAlignment.Top),
                VStack(
                        4,
                        bubble,
                        BuildAssistantFooter(
                            row,
                            entry,
                            isSpeaking,
                            isHovered,
                            toggleSpeechAsync,
                            setEntryHovered,
                            includeMetadata: row.IsAssistantRunEnd && row.Props.Timeline.ShowToolCalls))
                    .HAlign(HorizontalAlignment.Stretch)
                    .Grid(column: 1),
                Border(Empty()).Width(AvatarGutter(row))
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
                .Size(36, 36)
                .Margin(0, 0, 8, 0)
                .VAlign(VerticalAlignment.Top);

        var identity = row.Props.AssistantIdentity;
        var glyph = !string.IsNullOrWhiteSpace(identity?.Avatar)
            ? identity.Avatar
            : identity?.Emoji;
        Element content = !string.IsNullOrWhiteSpace(glyph)
            ? Text(glyph, 16, FontWeights.SemiBold, "TextFillColorSecondaryBrush").Center()
            : Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                .Size(36, 36)
                .AutomationName(identity?.DisplayName ?? "Assistant");

        return Border(content)
            .Size(36, 36)
            .CornerRadius(18)
            .Background(BrushFor("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor("ControlStrokeColorDefaultBrush", Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .Margin(0, 0, 8, 0)
            .VAlign(VerticalAlignment.Top)
            .AutomationName(identity?.DisplayName ?? "Assistant");
    }

    internal static Element BuildSafeMarkdown(string? text, Func<string, bool>? tryCopy = null)
    {
        var options = new MarkdownOptions
        {
            Heading = ChatMarkdownPresentation.Heading,
            Paragraph = ChatMarkdownPresentation.Paragraph,
            CodeBlock = (code, language) => ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy),
            ParserFlags = MarkdownParserFlags.Tables | MarkdownParserFlags.NoHtml,
            ListItem = BuildWrappingMarkdownListItem,
            Image = (alt, _) => Text(
                    string.IsNullOrWhiteSpace(alt) ? "[Image]" : $"[Image: {alt}]",
                    14,
                    FontWeights.Normal,
                    "TextFillColorPrimaryBrush")
                .IsTextSelectionEnabled(true),
            LinkBuilder = (children, _) => HStack(children),
            HtmlBlock = raw => Text(
                    ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(raw),
                    14,
                    FontWeights.Normal,
                    "TextFillColorPrimaryBrush")
                .IsTextSelectionEnabled(true),
        };

        return ChatMarkdownPresentation.MessageBlocks(
            Microsoft.UI.Reactor.Factories.Markdown(ChatMarkdownSanitizer.Sanitize(text), options));
    }

    private static Element BuildWrappingMarkdownListItem(Element defaultElement)
    {
        // preview.12 measures list content at infinite width in an HStack. Use the
        // Auto/Star layout from microsoft/microsoft-ui-reactor#1197 until #1424 retires the pin.
        if (defaultElement is not StackElement { Orientation: Orientation.Horizontal, Children.Length: 2 } row)
            throw new InvalidOperationException("Unexpected Reactor Markdown list item shape. Review the preview.12 workaround.");

        return Grid([GridSize.Auto, GridSize.Star()], [],
            row.Children[0],
            row.Children[1].Grid(column: 1)) with { ColumnSpacing = row.Spacing };
    }

    private static Element BuildAssistantFooter(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isSpeaking,
        bool isHovered,
        Func<ChatTimelineItem, Task> toggleSpeechAsync,
        Action<string, bool> setEntryHovered,
        bool includeMetadata)
    {
        var children = new List<Element>();
        var sender = row.Props.AssistantIdentity?.DisplayName ?? row.Props.Timeline.AssistantSenderLabel;
        var metadataText = includeMetadata ? FooterText(row, entry) : string.Empty;
        var identity = string.IsNullOrWhiteSpace(metadataText) ? sender : $"{sender} · {metadataText}";
        children.Add(CopyAction(entry.Text, setEntryHovered, entry.Id, row.Key, row.Props.TryCopyText));

        if (row.Props.Timeline.OnReadAloud is not null || row.Props.Timeline.OnStopSpeaking is not null)
        {
            var label = isSpeaking
                ? LocalizedOrDefault("Chat_Assistant_Action_Stop", "Stop")
                : LocalizedOrDefault("Chat_Assistant_Action_ReadAloud", "Read aloud");
            children.Add(CompactIconAction(
                isSpeaking ? "\uE71A" : "\uE767",
                label,
                () => _ = toggleSpeechAsync(entry),
                isHovered,
                () => setEntryHovered(entry.Id, true),
                () => setEntryHovered(entry.Id, false)));
        }

        return Grid(
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
                    .HAlign(HorizontalAlignment.Left).Grid(column: 1))
            .Margin(0, 4, 0, 0)
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static Element CopyAction(
        string? text,
        Action<string, bool> setEntryHovered,
        string entryId,
        string identity,
        Func<string, bool>? tryCopy)
    {
        var label = LocalizedOrDefault("Chat_Assistant_Action_Copy", "Copy");
        return Component<ChatCopyButton, ChatCopyButtonProps>(new(
            identity, text ?? string.Empty, label, tryCopy,
            focused => setEntryHovered(entryId, focused)));
    }

    private static Element CompactIconAction(
        string glyph,
        string label,
        Action onClick,
        bool isVisible,
        Action onGotFocus,
        Action onLostFocus)
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
            .OnGotFocus((_, _) => onGotFocus())
            .OnLostFocus((_, _) => onLostFocus())
            .Opacity(isVisible ? 1 : 0.7)
            .IsTabStop(true)
            .Set(button => button.IsHitTestVisible = true);
    }

    private static (string Message, IReadOnlyList<ChatAttachmentPresentation> Attachments) ParseAttachments(string? text)
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

    private static string BuildAccessibleUserText(
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

    private static Element BuildAttachment(ChatAttachmentPresentation attachment)
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

    private static Element BuildReasoning(ChatTimelineItem entry)
    {
        var content = Text(
                entry.Text ?? string.Empty,
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush")
            .IsTextSelectionEnabled(true);
        return Expander(
                LocalizedOrDefault("Chat_Reasoning_ThinkingHeader", "Thinking"),
                content)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Margin(0, ChatVisuals.BlockGap / 2);
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
        var presentation = ChatCompactionPresenter.TryCreateForEntry(
            entry,
            row.Props.Timeline.EntryMetadata,
            LocalizedOrDefault("Chat_Compaction_Title", "COMPACTED HISTORY"),
            LocalizedOrDefault(
                "Chat_Compaction_FallbackDetail",
                "The compacted transcript is preserved as a checkpoint. " +
                "Open session checkpoints to branch or restore from that compacted view."),
            LocalizedOrDefault("Chat_Compaction_OpenCheckpoints", "Open checkpoints"));
        return presentation is null
            ? BuildGenericStatus(entry)
            : BuildCompaction(row, presentation);
    }

    private static Element BuildGenericStatus(ChatTimelineItem entry)
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
        ChatCompactionPresentation presentation)
    {
        var sessionKey = row.Props.Timeline.SessionId;
        var canOpenCheckpoints = !string.IsNullOrWhiteSpace(sessionKey)
            && row.Props.OnOpenCheckpoints is not null;
        return Border(
                VStack(
                    8,
                    Text(presentation.Title, 13, FontWeights.SemiBold)
                        .HAlign(HorizontalAlignment.Center),
                    Text(presentation.Detail, 12, FontWeights.Normal, "TextFillColorSecondaryBrush")
                        .TextAlignment(TextAlignment.Center)
                        .HAlign(HorizontalAlignment.Center),
                    Button(
                            presentation.ActionLabel,
                            () =>
                            {
                                if (canOpenCheckpoints)
                                    row.Props.OnOpenCheckpoints!(sessionKey!);
                            })
                        .IsEnabled(canOpenCheckpoints)
                        .HAlign(HorizontalAlignment.Center)
                        .AutomationName(presentation.ActionLabel)))
            .Margin(36, 8, 24, 8)
            .Padding(16, 10)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(8)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("ControlStrokeColorDefaultBrush"))
            .BorderThickness(1)
            .AutomationName(presentation.AutomationName);
    }

    private static string FooterText(
        ReactorTimelineRow row,
        ChatTimelineItem entry)
    {
        ChatEntryMetadata? metadata = null;
        if (row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var resolvedMetadata) == true)
            metadata = resolvedMetadata;

        var time = metadata?.Timestamp?.ToLocalTime().ToString("h:mm tt");
        var model = metadata?.Model ?? row.Props.Timeline.DefaultModel;
        var usageSummary = row.IsLatestAssistant
            && row.Props.Timeline.ShowToolCalls
            ? row.Props.Timeline.DefaultUsageSummary
            : null;
        return string.Join(
                    " · ",
                    new[] { time, model, usageSummary }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static Element UserMetadata(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isHovered)
    {
        ChatEntryMetadata? metadata = null;
        if (row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var resolvedMetadata) == true)
            metadata = resolvedMetadata;

        var timestamp = metadata?.Timestamp?.ToLocalTime().ToString("h:mm tt");
        var model = metadata?.Model ?? row.Props.Timeline.DefaultModel;

        return HoverMetadata(
            Text(
                string.Join(
                    " · ",
                    new[] { timestamp, model }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                11,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"),
            isHovered);
    }

    private static Element HoverMetadata(Element child, bool isHovered)
    {
        return Border(child)
            .Opacity(isHovered ? 1 : 0)
            .Set(border => border.IsHitTestVisible = false);
    }

    private static TextBlockElement Text(
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

    private static string LocalizedOrDefault(string key, string fallback)
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
        ChatToolActivityRow activity) =>
        new(
            activity.Key,
            ReactorTimelineRowKind.Activity,
            props,
            null,
            activity);

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

    public static ReactorTimelineRow Empty(ReactorChatTimelineProps props) =>
        new("timeline:empty", ReactorTimelineRowKind.Empty, props, null);

}

internal enum ReactorTimelineRowKind
{
    Entry,
    Activity,
    Thinking,
    LoadEarlier,
    Loading,
    Empty,
}

internal readonly record struct ChatAssistantRunPosition(bool IsStart, bool IsEnd);

internal static class ChatTimelineAssistantRuns
{
    public static IReadOnlyList<ChatAssistantRunPosition> Describe(
        IReadOnlyList<ChatTimelineItem> entries)
    {
        var positions = new ChatAssistantRunPosition[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Kind != ChatTimelineItemKind.Assistant)
                continue;

            var isStart = index == 0 || entries[index - 1].Kind != ChatTimelineItemKind.Assistant;
            var isEnd = index == entries.Count - 1 || entries[index + 1].Kind != ChatTimelineItemKind.Assistant;
            positions[index] = new ChatAssistantRunPosition(isStart, isEnd);
        }

        return positions;
    }
}
