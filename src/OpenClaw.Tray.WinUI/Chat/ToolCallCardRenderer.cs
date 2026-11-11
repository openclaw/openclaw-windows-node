using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// Authoritative production renderer for standalone tool calls and grouped tool activity.
/// </summary>
internal static class ToolCallCardRenderer
{
    private const int ToolDetailMaxChars = 4000;
    private const int WorkSettleMilliseconds = 200;
    private const int LiveHeadlineMilliseconds = 160;

    private static readonly ConditionalWeakTable<Expander, ActivityExpanderBinding>
        s_activityBindings = new();

    public static Element BuildStandalone(
        ChatTimelinePresentationContext props,
        ChatTimelineItem entry,
        bool isNested = false,
        IReadOnlyList<ChatToolCallNode>? children = null) =>
        Component<ToolResultCard, ToolResultCardProps>(new(props, entry, isNested, children));

    internal static Element BuildStandaloneCore(
        ChatTimelinePresentationContext props, ChatTimelineItem entry, bool isNested,
        ChatToolOutputResult? recovered, Element recovery, Action onCollapse,
        bool showChevron = false, Action<bool>? onHeaderHover = null, Action<bool>? onHeaderFocus = null,
        Action? onExpansionChanged = null, IReadOnlyList<ChatToolCallNode>? children = null)
    {
        var details = new List<Element>();
        var fullInput = entry.ToolName == "sessions_yield" ? null : entry.ToolInputDetails;
        var displayArgs = fullInput is not null
            ? fullInput.Unavailable
                ? LocalizedOrDefault("Chat_Tool_InputTooLarge", "Tool input exceeds the safe display limit.")
                : string.Join("\n\n", new[] { fullInput.Command, fullInput.Text }.Where(text => !string.IsNullOrEmpty(text)))
            : entry.ToolName != "sessions_yield" && entry.ToolArgs is { Count: > 0 }
                ? FormatToolDisplayArgs(entry.ToolArgs)
                : string.Empty;
        if (!string.IsNullOrWhiteSpace(displayArgs))
        {
            details.Add(BuildDetailSection(
                LocalizedOrDefault("Chat_Tool_InputSection", "Tool input"),
                displayArgs, entry).WithKey("input"));
        }
        else if (entry.ToolName != "sessions_yield" && !string.IsNullOrWhiteSpace(entry.Text) &&
            !string.Equals(entry.Text, entry.ToolName, StringComparison.Ordinal))
        {
            details.Add(BuildDetailSection(
                LocalizedOrDefault("Chat_Tool_InputSection", "Tool input"),
                entry.Text, entry).WithKey("input"));
        }

        var output = recovered?.Output ?? entry.ToolOutput;
        if (!string.IsNullOrWhiteSpace(output))
        {
            if (details.Count > 0)
                details.Add(Border(Empty()).Height(1).Background(Theme.Ref("ChatStrokeBrush"))
                    .AccessibilityView(AccessibilityView.Raw).WithKey("input-output-divider"));
            details.Add(BuildDetailSection(
                LocalizedOrDefault(
                    entry.ToolResult == ChatToolCallStatus.Error
                        ? "Chat_Tool_ErrorLabel"
                        : "Chat_Tool_OutputLabel",
                    entry.ToolResult == ChatToolCallStatus.Error
                        ? "Tool error"
                        : "Tool output"),
                output).WithKey("output"));
        }
        var hasDetails = details.Count > 0 || entry.ToolResultReference?.IsTruncated == true;
        // Web: one thin-bordered, transparent card holding the sections, with the
        // settled outcome right-aligned under a divider ("Completed" / "failed").
        var outcome = entry.ToolResult switch
        {
            ChatToolCallStatus.Success => LocalizedOrDefault("Chat_Tool_Completed", "Completed"),
            ChatToolCallStatus.Error => LocalizedOrDefault("Chat_Tool_FailedInline", "failed"),
            ChatToolCallStatus.Interrupted => StatusLabel(entry.ToolResult),
            _ => null,
        };
        if (details.Count > 0 && outcome is not null)
        {
            details.Add(Border(Empty()).Height(1).Background(Theme.Ref("ChatStrokeBrush")).WithKey("outcome-divider"));
            details.Add(TextBlock(outcome)
                .FontSize(11)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .HAlign(HorizontalAlignment.Right)
                .Margin(14, 6, 14, 8)
                .AutomationId("ChatToolOutcome")
                .WithKey("outcome"));
        }
        var card = details.Count == 0
            ? Empty()
            : Border(VStack(0, details.ToArray()))
                .CornerRadius(ChatVisuals.SurfaceRadius)
                .BorderThickness(1)
                .BorderBrush(Theme.Ref("ChatStrokeBrush"))
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .WithKey("details-card");

        var toolName = string.IsNullOrWhiteSpace(entry.ToolName)
            ? LocalizedOrDefault("Chat_Tool_FooterLabel", "Tool")
            : entry.ToolName;
        var statusLabel = entry.ToolResult == ChatToolCallStatus.InProgress && entry.ToolInputPreparing
            ? LocalizedOrDefault("Chat_Tool_Preparing", "Preparing input") : StatusLabel(entry.ToolResult);
        if (entry.ToolResult == ChatToolCallStatus.InProgress && entry.ToolLiveDiff is { } diff)
            statusLabel += $" (+{diff.Added} / -{diff.Removed})";
        var preview = ToolPreview(entry);
        var title = string.IsNullOrWhiteSpace(preview) ? toolName : preview;
        // Web step rows: muted title, a red inline "failed", no trailing "Done".
        var inlineStatus = entry.ToolResult switch
        {
            ChatToolCallStatus.Success => null,
            ChatToolCallStatus.Error => TextBlock(LocalizedOrDefault("Chat_Tool_FailedInline", "failed"))
                .FontSize(ChatVisuals.ToolRowFontSize)
                .Foreground(Theme.Ref("SystemFillColorCriticalBrush")),
            _ => TextBlock(statusLabel).FontSize(ChatVisuals.ToolRowFontSize).Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
        };
        var expansionKey = ChatToolActivityPresentation.ToolRowKey(props.SessionId, props.TimelineGeneration, entry.Id);
        var isExpanded = props.ToolExpansionState?.IsExpanded(expansionKey, props.ToolCallsCollapseVersion) ?? false;
        var header = BuildRowHeader(
            ToolGlyph(entry),
            showChevron,
            isExpanded,
            TextBlock(title)
                .FontSize(ChatVisuals.ToolRowFontSize)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .WithTextShimmer(entry.ToolResult is null or ChatToolCallStatus.InProgress)
                .WithKey("preview"),
            inlineStatus,
            onHeaderHover);
        Element body = VStack(4, card, recovery.WithKey("recovery"))
            .Margin(0, 2, 0, 6);
        // Web wrapper rows list their nested calls first, then their own input behind "Tool input".
        // Nested rows exist only while the wrapper is expanded.
        if (children is { Count: > 0 })
        {
            body = isExpanded || props.ToolExpansionState is null
                ? BuildNestedToolBody(props, expansionKey, children, hasDetails ? body : null, onExpansionChanged)
                : Empty();
        }
        var expander = Expander(
                $"{toolName} · {statusLabel}",
                body,
                props.ToolExpansionState is { } expansion
                    ? (Optional<bool>)expansion.IsExpanded(expansionKey, props.ToolCallsCollapseVersion) : default,
                expanded =>
                {
                    props.ToolExpansionState?.SetExplicit(expansionKey, expanded, props.ToolCallsCollapseVersion);
                    if (!expanded) onCollapse();
                    onExpansionChanged?.Invoke();
                })
            .HeaderTemplate(header)
            .OnUnmount(ChatVisuals.StopToolRowShimmers)
            .MinHeight(ChatVisuals.ToolRowMinHeight)
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderBrush(Theme.Ref("SubtleFillColorTransparentBrush"))
            .Resources(ChatVisuals.ToolRowExpanderResources)
            .Set(ChatVisuals.FlushToolRowExpander)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .OnGotFocus((sender, args) => { if (IsOwnHeader(sender, args)) onHeaderFocus?.Invoke(true); })
            .OnLostFocus((sender, args) => { if (IsOwnHeader(sender, args)) onHeaderFocus?.Invoke(false); })
            .AutomationId($"ChatToolCall_{SanitizeAutomationId(entry.Id)}")
            .AutomationName(
                $"{LocalizedOrDefault("Chat_Tool_CallLabel", "Tool call")} {toolName}. {statusLabel}.")
            .HelpText(preview)
            .WithKey($"tool-expander:{entry.Id}:collapse:{props.ToolCallsCollapseVersion}");

        if (isNested)
        {
            expander = expander
                .FontSize(ChatVisuals.ToolRowFontSize)
                .MinHeight(ChatVisuals.ToolRowMinHeight)
                .Padding(0);
        }

        var reviews = (entry.ToolReviews ?? []).Select(review =>
        {
            var status = review.Status switch
            {
                "in_progress" => LocalizedOrDefault("Chat_Review_Working", "Provider review in progress"),
                "approved" => LocalizedOrDefault("Chat_Review_Approved", "Provider review approved"),
                "timed_out" => LocalizedOrDefault("Chat_Review_TimedOut", "Provider review timed out"),
                "aborted" => LocalizedOrDefault("Chat_Review_Aborted", "Provider review canceled"),
                _ => LocalizedOrDefault("Chat_Review_Denied", "Provider review did not approve the request"),
            };
            return VStack(4,
                TextBlock($"{review.Label}: {status}").TextWrapping(TextWrapping.Wrap).FontSize(12),
                string.IsNullOrEmpty(review.Rationale) ? Empty()
                    : TextBlock(review.Rationale).TextWrapping(TextWrapping.Wrap).FontSize(12)
                        .IsTextSelectionEnabled(true),
                TextBlock(string.Join(" · ", new[] { review.RiskLevel, review.UserAuthorization }
                    .Where(value => !string.IsNullOrEmpty(value))))
                    .FontSize(12).TextWrapping(TextWrapping.Wrap))
                .AutomationId("ChatToolReview").WithKey(review.Id);
        }).ToArray();
        return Border(VStack(4, [expander, .. reviews]))
            .Padding(0)
            .BorderThickness(0)
            .CornerRadius(ChatVisuals.SurfaceRadius)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"));
    }

    private static Element BuildNestedToolBody(
        ChatTimelinePresentationContext props,
        string expansionKey,
        IReadOnlyList<ChatToolCallNode> children,
        Element? ownDetails,
        Action? onExpansionChanged)
    {
        var rows = children
            .Select(child => BuildStandalone(props, child.Tool, isNested: true, child.Children)
                .WithKey($"child:{child.Tool.Id}"))
            .ToList();
        if (ownDetails is not null)
        {
            var inputKey = expansionKey + "|input";
            var label = LocalizedOrDefault("Chat_Tool_InputSection", "Tool input");
            var inputExpanded = props.ToolExpansionState?.IsExpanded(inputKey, props.ToolCallsCollapseVersion) ?? false;
            rows.Add(Expander(
                    label,
                    ownDetails,
                    props.ToolExpansionState is null ? default : (Optional<bool>)inputExpanded,
                    expanded =>
                    {
                        props.ToolExpansionState?.SetExplicit(inputKey, expanded, props.ToolCallsCollapseVersion);
                        onExpansionChanged?.Invoke();
                    })
                .HeaderTemplate(BuildRowHeader(
                    FluentIconCatalog.ChevronR,
                    showChevron: true,
                    inputExpanded,
                    TextBlock(label).FontSize(ChatVisuals.ToolRowFontSize).Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                    null,
                    null))
                .FontSize(ChatVisuals.ToolRowFontSize)
                .MinHeight(ChatVisuals.ToolRowMinHeight)
                .Padding(0)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderBrush(Theme.Ref("SubtleFillColorTransparentBrush"))
                .Resources(ChatVisuals.ToolRowExpanderResources)
                .Set(ChatVisuals.FlushToolRowExpander)
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .AutomationName(label)
                .WithKey("own-input"));
        }
        // A thin guide line under the wrapper's icon center marks its nested calls,
        // 16 pixels before their rows, as on the web (.chat-tool-children).
        return Grid([GridSize.Auto, GridSize.Star()], [GridSize.Star()],
                Border(Empty()).Width(1).Margin(7, 0, 16, 0)
                    .Background(Theme.Ref("DividerStrokeColorDefaultBrush"))
                    .VAlign(VerticalAlignment.Stretch).Grid(column: 0)
                    .AccessibilityView(AccessibilityView.Raw).WithKey("nested-guide"),
                VStack(0, rows.ToArray()).Grid(column: 1));
    }

    private static Element BuildDetailSection(string label, string content, ChatTimelineItem? input = null)
    {
        var body = RichTextBlock(content)
            .Set(text =>
            {
                // Reactor pools RichTextBlocks without clearing Markdown highlighters.
                text.TextHighlighters.Clear();
                text.Style = (Style)Application.Current.Resources[
                    input is null ? "ChatSecondaryRichTextStyle" : "ChatRichTextStyle"];
                text.TextWrapping = TextWrapping.Wrap;
                text.FontSize = 12;
                text.FontWeight = FontWeights.Normal;
                text.FontFamily = new FontFamily(ChatVisuals.CodeFontFamily);
                text.IsTextSelectionEnabled = true;
                ChatToolInputSyntax.Apply(text, content, input);
            });

        return Border(ScrollViewer(body)
                .AutomationName(label)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                })
                .MaxHeight(240))
            .Padding(ChatVisuals.DetailHorizontalInset, ChatVisuals.DetailVerticalInset);
    }

    private static string FormatToolDisplayArgs(System.Text.Json.Nodes.JsonObject args)
    {
        var lines = new List<string>();
        foreach (var key in NativeToolProjector.DisplayArgumentKeys)
        {
            if (args[key] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                lines.Add($"{key}: {text}");
            }
        }

        var display = string.Join('\n', lines);
        return display.Length <= ToolDetailMaxChars
            ? display
            : display[..ToolDetailMaxChars] + "\n\u2026(truncated)";
    }

    public static Element BuildActivity(
        ChatTimelinePresentationContext props,
        ChatToolActivityRow activity,
        ChatToolActivityExpansionState expansionState,
        Func<ChatTimelineItem, Element>? buildWorkEntry = null,
        bool animateEntrance = false) =>
        Component<ToolActivityCard, ToolActivityCardProps>(
            new ToolActivityCardProps(props, activity, expansionState, buildWorkEntry, animateEntrance));

    internal static Element BuildActivityCore(
        ChatTimelinePresentationContext props,
        ChatToolActivityRow activity,
        bool isExpanded,
        Action<bool> onUserExpansionChanged,
        Func<ChatTimelineItem, Element>? buildWorkEntry = null,
        bool animateEntrance = false,
        bool reducedMotion = true,
        bool showChevron = false,
        Action<bool>? onHeaderHover = null,
        Action<bool>? onHeaderFocus = null)
    {
        var summary = activity.Summary
            ?? throw new ArgumentException("An activity row requires a summary.", nameof(activity));
        var formattedSummary = FormatSummary(summary);
        var runningTool = summary.NewestRunningTool;
        var runningPreview = runningTool is null ? string.Empty : ToolPreview(runningTool);
        var hasRunningTitle = summary.IsRunning && !string.IsNullOrWhiteSpace(runningPreview);
        var summaryText = activity.IsCompletedWork
            ? activity.DurationMs is { } duration
                ? string.Format(LocalizedOrDefault("Chat_Activity_WorkedFor", "Worked for {0}"),
                    ChatRunProgressProjection.ElapsedMilliseconds(duration))
                : LocalizedOrDefault("Chat_Activity_Worked", "Worked")
            : hasRunningTitle ? runningPreview : formattedSummary;
        var runningStatus = hasRunningTitle ? StatusLabel(runningTool?.ToolResult) : null;
        string AutomationName(bool expanded) => string.Format(
            summary.ToolCount == 1
                ? LocalizedOrDefault("Chat_Activity_AutomationFormatOne", "Activity: {0}. {1} tool. {2}.")
                : LocalizedOrDefault("Chat_Activity_AutomationFormat", "Activity: {0}. {1} tools. {2}."),
            summaryText,
            summary.ToolCount,
            expanded
                ? LocalizedOrDefault("Chat_Activity_Expanded", "Expanded")
                : LocalizedOrDefault("Chat_Activity_Collapsed", "Collapsed"));
        var collapsedAutomationName = AutomationName(expanded: false);
        var expandedAutomationName = AutomationName(expanded: true);
        Element details = !isExpanded
            ? Empty()
            : activity.Work is { } work
                ? VStack(
                    0,
                    [
                        Border(Empty()).Height(1).Margin(0, 2, 0, 4)
                            .Background(Theme.Ref("DividerStrokeColorDefaultBrush")).WithKey("work-separator"),
                        .. ChatToolActivityPresentation.ProjectWork(work, props.SessionId, props.TimelineGeneration)
                            .Select(row => (row.IsActivityGroup
                                    ? BuildActivity(props, row, props.ToolExpansionState ?? new(), buildWorkEntry)
                                    : row.Entry!.Kind == ChatTimelineItemKind.ToolCall
                                        ? BuildStandalone(props, row.Entry, isNested: true)
                                        : buildWorkEntry?.Invoke(row.Entry) ?? Empty())
                                .WithKey($"work-row:{row.Key}")),
                    ])
                : VStack(
                    0,
                    ChatToolActivityPresentation.GroupNested(activity.Tools)
                        .Select(root => BuildStandalone(props, root.Tool, isNested: true, root.Children)
                            .WithKey($"activity-entry:{root.Tool.Id}"))
                        .ToArray());

        var headlineText = activity.IsCompletedWork && isExpanded && summary.ToolCount > 0
            ? summaryText + " · " + string.Format(
                summary.ToolCount == 1
                    ? LocalizedOrDefault("Chat_Activity_ToolCallsOne", "{0} tool call")
                    : LocalizedOrDefault("Chat_Activity_ToolCallsMany", "{0} tool calls"),
                summary.ToolCount)
            : summaryText;
        var headline = TextBlock(headlineText)
            .FontSize(ChatVisuals.ToolRowFontSize)
            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
            .TextWrapping(TextWrapping.Wrap)
            .WithTextShimmer(summary.IsRunning)
            .WithKey($"activity-headline:{summary.NewestRunningTool?.Id ?? "settled"}");
        if (!reducedMotion && summary.IsRunning)
            headline = headline.Transition(
                Transition.Enter(Transition.Fade), Curve.Ease(LiveHeadlineMilliseconds, Easing.EaseOut));

        var header = BuildRowHeader(
            FluentIconCatalog.BulletedList,
            showChevron,
            isExpanded,
            headline,
            runningStatus is null
                ? null
                : TextBlock(runningStatus).FontSize(ChatVisuals.ToolRowFontSize).Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
            onHeaderHover);
        var expander = Expander(
                summaryText,
                details)
            .HeaderTemplate(header)
            .OnUnmount(ChatVisuals.StopToolRowShimmers)
            .MinHeight(ChatVisuals.ToolRowMinHeight)
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderBrush(Theme.Ref("SubtleFillColorTransparentBrush"))
            .Resources(ChatVisuals.ToolRowExpanderResources)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .OnGotFocus((sender, args) => { if (IsOwnHeader(sender, args)) onHeaderFocus?.Invoke(true); })
            .OnLostFocus((sender, args) => { if (IsOwnHeader(sender, args)) onHeaderFocus?.Invoke(false); })
            .AutomationId($"ChatToolActivity_{SanitizeAutomationId(activity.IsCompletedWork ? activity.Key : activity.Tools[0].Id)}")
            .Set(control =>
            {
                ChatVisuals.FlushToolRowExpander(control);
                ApplyActivityExpander(
                    control,
                    onUserExpansionChanged,
                    isExpanded,
                    collapsedAutomationName,
                    expandedAutomationName);
            })
            .AutomationName(isExpanded ? expandedAutomationName : collapsedAutomationName)
            .WithKey(activity.Key);

        var card = Border(expander)
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderThickness(0)
            .CornerRadius(ChatVisuals.SurfaceRadius);
        return activity.IsCompletedWork && animateEntrance && !reducedMotion
            ? card.Transition(Transition.Enter(Transition.Fade), Curve.Ease(WorkSettleMilliseconds, Easing.EaseOut))
            : card;
    }

    internal static string FormatSummary(ChatToolActivitySummary summary)
        => ChatToolActivityFormatter.Format(summary, CreateFormatTemplates());

    private static string ToolPreview(ChatTimelineItem entry)
    {
        if (entry.ToolName == "sessions_yield")
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(entry.ToolInputDetails?.Preview))
            return entry.ToolInputDetails.Preview;

        return NativeToolProjector.SanitizeToolDisplayValue(
            NativeToolProjector.FirstToolDisplayValue(entry.ToolArgs));
    }

    // A fixed width keeps the title column the same for the 14pt glyph and the 12pt chevron. No
    // fixed height: a taller box draws the glyph at its top, above the title's centre line.
    private static Element ToolIcon(string glyph, double fontSize = 14) =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(fontSize)
            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
            .AccessibilityView(AccessibilityView.Raw)
            .Set(text => text.IsTextScaleFactorEnabled = false)
            .Width(16);

    /// <summary>
    /// Tool row header: leading icon (a chevron while hovered or focused), the title, then an
    /// optional inline status. The left-aligned grid keeps the status right after the title
    /// while still trimming long titles.
    /// </summary>
    private static Element BuildRowHeader(
        string glyph,
        bool showChevron,
        bool isExpanded,
        Element title,
        Element? status,
        Action<bool>? onHover)
    {
        var icon = showChevron
            ? ToolIcon(isExpanded ? FluentIconCatalog.ChevronDown : FluentIconCatalog.ChevronR, 12)
            : ToolIcon(glyph);
        return Border(
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    icon.Grid(column: 0).Margin(0, 0, 8, 0).VAlign(VerticalAlignment.Center),
                    title.Grid(column: 1).VAlign(VerticalAlignment.Center),
                    status is null ? Empty() : status.Grid(column: 2).Margin(6, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center))
                .HAlign(HorizontalAlignment.Left))
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .OnPointerEntered((_, _) => onHover?.Invoke(true))
            .OnPointerExited((_, _) => onHover?.Invoke(false));
    }

    // GotFocus bubbles from nested rows and detail controls; only this Expander's own header counts.
    private static bool IsOwnHeader(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton header)
            return false;
        DependencyObject? node = header;
        while (node is not null and not Microsoft.UI.Xaml.Controls.Expander)
            node = VisualTreeHelper.GetParent(node);
        return ReferenceEquals(node, sender);
    }

    private static string ToolGlyph(ChatTimelineItem entry) =>
        ToolGlyph(ChatToolActivityPresentation.Classify(entry.ToolName, entry.ToolArgs));

    private static string ToolGlyph(ChatToolActivityCategory category) => category switch
    {
        ChatToolActivityCategory.Command => FluentIconCatalog.Terminal,
        ChatToolActivityCategory.Read => FluentIconCatalog.Document,
        ChatToolActivityCategory.Edit => FluentIconCatalog.Develop,
        ChatToolActivityCategory.Write => FluentIconCatalog.Add,
        ChatToolActivityCategory.Search or ChatToolActivityCategory.Fetch => FluentIconCatalog.Browser,
        _ => FluentIconCatalog.Plugins,
    };

    private static ChatToolActivityFormatTemplates CreateFormatTemplates() => new(
        CommandOne: LocalizedOrDefault("Chat_Activity_CommandOne", "{0} command"),
        CommandMany: LocalizedOrDefault("Chat_Activity_CommandMany", "{0} commands"),
        ReadOne: LocalizedOrDefault("Chat_Activity_ReadOne", "{0} read"),
        ReadMany: LocalizedOrDefault("Chat_Activity_ReadMany", "{0} reads"),
        EditOne: LocalizedOrDefault("Chat_Activity_EditOne", "{0} edit"),
        EditMany: LocalizedOrDefault("Chat_Activity_EditMany", "{0} edits"),
        WriteOne: LocalizedOrDefault("Chat_Activity_WriteOne", "{0} write"),
        WriteMany: LocalizedOrDefault("Chat_Activity_WriteMany", "{0} writes"),
        SearchOne: LocalizedOrDefault("Chat_Activity_SearchOne", "{0} search"),
        SearchMany: LocalizedOrDefault("Chat_Activity_SearchMany", "{0} searches"),
        FetchOne: LocalizedOrDefault("Chat_Activity_FetchOne", "{0} fetch"),
        FetchMany: LocalizedOrDefault("Chat_Activity_FetchMany", "{0} fetches"),
        GenericOne: LocalizedOrDefault("Chat_Activity_GenericOne", "{0} other operation"),
        GenericMany: LocalizedOrDefault("Chat_Activity_GenericMany", "{0} other operations"),
        Running: LocalizedOrDefault("Chat_Activity_RunningFormat", "Running {0}"),
        ToolFallback: LocalizedOrDefault("Chat_Tool_FooterLabel", "Tool"));

    private static void ApplyActivityExpander(
        Expander control,
        Action<bool> onUserExpansionChanged,
        bool isExpanded,
        string collapsedAutomationName,
        string expandedAutomationName)
    {
        if (!s_activityBindings.TryGetValue(control, out var binding))
        {
            binding = new ActivityExpanderBinding();
            control.Expanding += (_, _) =>
            {
                if (!binding.IsApplying)
                {
                    binding.OnUserExpansionChanged?.Invoke(true);
                    AutomationProperties.SetName(control, binding.ExpandedAutomationName);
                }
            };
            control.Collapsed += (_, _) =>
            {
                if (!binding.IsApplying)
                {
                    binding.OnUserExpansionChanged?.Invoke(false);
                    AutomationProperties.SetName(control, binding.CollapsedAutomationName);
                }
            };
            s_activityBindings.Add(control, binding);
        }

        binding.OnUserExpansionChanged = onUserExpansionChanged;
        binding.CollapsedAutomationName = collapsedAutomationName;
        binding.ExpandedAutomationName = expandedAutomationName;
        binding.IsApplying = true;
        try
        {
            control.IsExpanded = isExpanded;
        }
        finally
        {
            binding.IsApplying = false;
        }
    }

    private static string StatusLabel(ChatToolCallStatus? status) => status switch
    {
        null or ChatToolCallStatus.InProgress => LocalizedOrDefault("Chat_Status_Running", "Running"),
        ChatToolCallStatus.Error => LocalizedOrDefault("Chat_Status_Error", "Error"),
        ChatToolCallStatus.Interrupted => LocalizedOrDefault("Chat_Status_Interrupted", "Interrupted"),
        _ => LocalizedOrDefault("Chat_Status_Done", "Done"),
    };

    private static string SanitizeAutomationId(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static TextBlockElement Text(
        string text,
        double fontSize,
        global::Windows.UI.Text.FontWeight weight,
        string foregroundResource) =>
        TextBlock(text)
            .TextWrapping(TextWrapping.Wrap)
            .FontSize(fontSize)
            .FontWeight(weight)
            .Foreground(BrushFor(foregroundResource, Microsoft.UI.Colors.Black));

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
            return brush;
        return new SolidColorBrush(fallback);
    }

    private sealed class ActivityExpanderBinding
    {
        public Action<bool>? OnUserExpansionChanged { get; set; }
        public bool IsApplying { get; set; }
        public string CollapsedAutomationName { get; set; } = string.Empty;
        public string ExpandedAutomationName { get; set; } = string.Empty;
    }

}

internal sealed record ToolResultCardProps(
    ChatTimelinePresentationContext Timeline,
    ChatTimelineItem Entry,
    bool IsNested,
    IReadOnlyList<ChatToolCallNode>? Children = null);

internal sealed class ToolResultCard : Component<ToolResultCardProps>
{
    public override Element Render()
    {
        var operation = UseRef(new ChatToolOutputOperation()).Current;
        var (_, setVersion) = UseState(0, threadSafe: true);
        var version = UseRef(0);
        UseEffect((Func<Action>)(() =>
        {
            void Changed() => setVersion(++version.Current);
            operation.Changed += Changed;
            return () => { operation.Changed -= Changed; operation.Dispose(); };
        }), Array.Empty<object>());
        var timeline = Props.Timeline;
        var entry = Props.Entry;
        var reference = entry.ToolResultReference;
        var currentProfile = timeline.ReadToolDetailsProfileState?.Invoke() ?? default;
        ChatToolOutputRequest? request = reference is { MessageId: { Length: > 0 }, SessionId: { Length: > 0 }, IsTruncated: true } &&
            currentProfile.IsAvailable && currentProfile == timeline.ToolDetailsProfileState &&
            timeline.SessionId is { } sessionKey && timeline.ToolOutputConnectionEpoch is { } epoch &&
            reference.CallId is { Length: > 0 } callId
                ? new(sessionKey, timeline.TimelineGeneration, timeline.HistoryRevision, epoch,
                    entry.Id, entry.ToolRunId, callId, reference.SessionId, reference.MessageId, timeline.ToolDetailsProfileState)
                : null;
        var loader = timeline.LoadToolOutputAsync;
        UseEffect(() => operation.SetTarget(request, loader), request, loader, timeline.ToolCallsCollapseVersion);
        UseEffect(operation.Close, timeline.ToolCallsCollapseVersion);
        var (loading, result) = operation.Snapshot(request, loader, currentProfile);
        Element recovery = Empty();
        if (reference?.IsTruncated == true)
        {
            var status = loading ? "Loading" : result?.Status.ToString() ??
                (request is null || loader is null ? "Missing" : "Preview");
            var children = new List<Element>
            {
                TextBlock(LocalizationHelper.GetString("Chat_ToolRecovery_" + status))
                    .FontSize(12).TextWrapping(TextWrapping.Wrap).AutomationId("ChatToolOutputRecoveryStatus"),
            };
            if (loading)
            {
                var label = LocalizationHelper.GetString("Chat_ToolRecovery_Cancel");
                children.Add(Button(label, operation.Close).AutomationName(label).AutomationId("ChatToolOutputCancel"));
            }
            else if (request is not null && loader is not null &&
                (result is null || result.Status == ChatToolOutputStatus.Error))
            {
                var label = LocalizationHelper.GetString(result is null ? "Chat_ToolRecovery_Load" : "Chat_ToolRecovery_Retry");
                children.Add(Button(label, async () => await operation.LoadAsync(request, loader))
                    .AutomationName(label).AutomationId("ChatToolOutputLoad"));
            }
            recovery = VStack(4, children.ToArray()).WithKey("full-output-action");
        }
        var (hovered, setHovered) = UseState(false, threadSafe: true);
        var (focused, setFocused) = UseState(false, threadSafe: true);
        var (_, setExpansionVersion) = UseState(0, threadSafe: true);
        var expansionVersion = UseRef(0);
        return ToolCallCardRenderer.BuildStandaloneCore(
            timeline, entry, Props.IsNested, result, recovery, operation.Close,
            hovered || focused, value => setHovered(value), value => setFocused(value),
            () => setExpansionVersion(++expansionVersion.Current), Props.Children);
    }
}

internal sealed record ToolActivityCardProps(
    ChatTimelinePresentationContext Timeline,
    ChatToolActivityRow Activity,
    ChatToolActivityExpansionState ExpansionState,
    Func<ChatTimelineItem, Element>? BuildWorkEntry = null,
    bool AnimateEntrance = false);

/// <summary>
/// Owns activity disclosure rerenders so nested tool controls exist only while expanded.
/// </summary>
internal sealed class ToolActivityCard : Component<ToolActivityCardProps>
{
    public override Element Render()
    {
        var reducedMotion = UseReducedMotion();
        var (hovered, setHovered) = UseState(false, threadSafe: true);
        var (focused, setFocused) = UseState(false, threadSafe: true);
        var (_, setRenderVersion) = UseState(0, threadSafe: true);
        var renderVersion = UseRef(0);
        var summary = Props.Activity.Summary
            ?? throw new ArgumentException("An activity row requires a summary.", nameof(Props));
        var isExpanded = Props.ExpansionState.IsExpanded(
            Props.Activity.Key,
            summary,
            Props.Timeline.ToolCallsCollapseVersion);

        void OnUserExpansionChanged(bool expanded)
        {
            Props.ExpansionState.SetExplicit(
                Props.Activity.Key,
                expanded,
                Props.Timeline.ToolCallsCollapseVersion);
            var nextRenderVersion = renderVersion.Current + 1;
            renderVersion.Current = nextRenderVersion;
            setRenderVersion(nextRenderVersion);
        }

        return ToolCallCardRenderer.BuildActivityCore(
            Props.Timeline,
            Props.Activity,
            isExpanded,
            OnUserExpansionChanged,
            Props.BuildWorkEntry,
            Props.AnimateEntrance,
            reducedMotion,
            hovered || focused,
            value => setHovered(value),
            value => setFocused(value));
    }
}
