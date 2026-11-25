// Renders typed Gateway transcript notices in the native chat timeline the way the web Control UI
// does: a hairline "system line" divider with an optional description, an inline disclosure for
// collapsed system context, the learned-skill notice with its gated Undo, and the run-failure row
// with copyable error details. ChatNoticePresentation decides the kind and copy; this file only lays
// it out with native theme resources.
//
// Usage (from ReactorChatTimeline.BuildStatus):
//   var kind = ChatNoticePresentation.KindOf(entry, row.Props.Timeline.EntryMetadata);
//   if (kind != ChatNoticeKind.None)
//       return ReactorChatNotices.Build(row, entry, kind);

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal static class ReactorChatNotices
{
    private const double GlyphSize = 12;
    private const double HairlineMinWidth = 24;
    private const double HairlineGap = 8;
    private const double MinLabelWidth = 120;
    private const double SkillNameMaxWidth = 176;
    private const double ErrorDetailsMaxHeight = 160;
    private const string MutedBrush = "TextFillColorSecondaryBrush";

    internal static Element Build(ReactorTimelineRow row, ChatTimelineItem entry, ChatNoticeKind kind)
    {
        var timeline = row.Props.Timeline;
        var presentation = entry.Presentation ?? timeline.EntryMetadata?.GetValueOrDefault(entry.Id)?.Presentation;
        var labelWidth = LabelMaxWidth(row);
        return kind switch
        {
            ChatNoticeKind.RunFailure when presentation?.RunFailure is { } failure =>
                Disclosure(row, entry, (expanded, onExpanded) =>
                    BuildRunFailure(row, entry, failure, expanded, onExpanded)),
            ChatNoticeKind.SkillLearned when presentation?.SkillChanges is { } notice =>
                Component<ReactorSkillLearnedNotice, ReactorSkillLearnedNoticeProps>(
                    new(notice, timeline.SkillUndo, entry.Id, labelWidth)),
            ChatNoticeKind.LegacySkill => Notice(
                SystemLine(FluentIconCatalog.Lightbulb, L("Chat_SkillLearned_Label", "Learned"), labelWidth),
                Body(row, entry.Text)),
            ChatNoticeKind.ModelFallback when presentation?.ModelFallback is { } fallback => Notice(
                SystemLine(null, ChatNoticePresentation.FallbackLabel(fallback, L), labelWidth),
                ChatNoticePresentation.FallbackReason(fallback, L) is { } reason
                    ? Muted(reason).TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center)
                        .MaxWidth(labelWidth)
                    : null),
            ChatNoticeKind.System when ChatNoticePresentation.ResolveSystem(presentation, entry.Text, L) is { } view =>
                BuildSystem(row, entry, view, labelWidth),
            _ => ReactorChatTimeline.BuildGenericStatus(entry),
        };
    }

    private static Element BuildSystem(
        ReactorTimelineRow row, ChatTimelineItem entry, ChatSystemNoticeView view, double labelWidth)
    {
        var glyph = view.Icon == ChatSystemNoticeIcon.Compass ? FluentIconCatalog.Compass : FluentIconCatalog.Cpu;
        var time = view.ShowTimestamp ? ReactorChatTimeline.FooterText(row, entry) : string.Empty;
        if (!view.Collapsed || string.IsNullOrWhiteSpace(view.Text))
            return Notice(
                SystemLine(glyph, view.Label, labelWidth, time),
                Body(row, view.Text)?.MaxWidth(labelWidth).HAlign(HorizontalAlignment.Center));

        return Disclosure(row, entry, (expanded, onExpanded) =>
        {
            var title = new List<Element>
            {
                Glyph(glyph).WithKey("icon"),
                ChatVisuals.ActivityText(view.Label, false).WithKey("label"),
            };
            if (time.Length > 0)
                title.Add(ChatVisuals.ActivityText("· " + time, false).WithKey("time"));
            return DisclosureFrame(
                HStack(6, title.ToArray()),
                ReactorChatTimeline.BuildSafeMarkdown(view.Text, row.Props.TryCopyText, compactSecondaryReading: true)
                    .Margin(ChatVisuals.ToolRowTitleInset, 4, 0, 8),
                time.Length > 0 ? $"{view.Label}, {time}" : view.Label,
                "ChatSystemNotice_" + AutomationSuffix(entry.Id),
                expanded,
                onExpanded);
        });
    }

    private static Element BuildRunFailure(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        ChatRunFailureNotice failure,
        bool expanded,
        Action<bool> onExpanded)
    {
        var title = ChatNoticePresentation.RunFailureTitle(failure, L);
        var titleParts = new List<Element>
        {
            Glyph(FluentIconCatalog.StatusErr, "SystemFillColorCriticalBrush").WithKey("icon"),
            ChatVisuals.ActivityText(title, false).WithKey("title"),
        };
        var automationName = title;
        if (row.NoticeRepeatCount > 1)
        {
            var count = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("Chat_RunFailure_RepeatCount", "{0} times"),
                row.NoticeRepeatCount);
            titleParts.Add(ChatVisuals.ActivityText("· " + count, false).WithKey("count"));
            automationName = $"{title}, {count}";
        }

        var errorDetails = L("Chat_RunFailure_ErrorDetails", "Error details");
        var details = new List<Element>();
        if (ChatNoticePresentation.RunFailureLead(failure.Summary) is { } lead)
            details.Add(ReactorChatTimeline.Text(lead, 13).IsTextSelectionEnabled(true).WithKey("lead"));
        details.Add(Border(
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    ScrollViewer(TextBlock(failure.Summary)
                            .FontFamily(ChatVisuals.CodeFontFamily)
                            .FontSize(12)
                            .TextWrapping(TextWrapping.Wrap)
                            .Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
                            .IsTextSelectionEnabled(true)
                            .Set(text => text.FlowDirection = FlowDirection.LeftToRight))
                        .MaxHeight(ErrorDetailsMaxHeight)
                        .IsTabStop(true)
                        .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
                        .AutomationName(errorDetails)
                        .AutomationId("ChatRunFailureDetails_" + AutomationSuffix(entry.Id))
                        .VAlign(VerticalAlignment.Top)
                        .Grid(column: 0),
                    Component<ChatCopyButton, ChatCopyButtonProps>(new(
                            "RunFailure_" + AutomationSuffix(entry.Id),
                            failure.Summary,
                            L("Chat_RunFailure_CopyError", "Copy error"),
                            row.Props.TryCopyText))
                        .VAlign(VerticalAlignment.Top)
                        .Margin(8, 0, 0, 0)
                        .Grid(column: 1)))
            .Padding(10, 8)
            .CornerRadius(6)
            .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
            .BorderBrush(Theme.Ref("ControlStrokeColorDefaultBrush"))
            .BorderThickness(1)
            .WithKey("details"));

        return DisclosureFrame(
            HStack(6, titleParts.ToArray()),
            VStack(8, details.ToArray()).Margin(0, 4, 0, 8),
            automationName,
            "ChatRunFailure_" + AutomationSuffix(entry.Id),
            expanded,
            onExpanded,
            ruledLabelWidth: LabelMaxWidth(row));
    }

    private static Element Disclosure(
        ReactorTimelineRow row, ChatTimelineItem entry, Func<bool, Action<bool>, Element> build) =>
        Component<ReactorNoticeDisclosure, ReactorNoticeDisclosureProps>(new(row.Props.Timeline, entry.Id, build))
            // Like tool rows, collapse-all remounts the disclosure so its local open state resets too.
            .WithKey($"notice:{entry.Id}:collapse:{row.Props.Timeline.ToolCallsCollapseVersion}");

    internal static Element DisclosureFrame(
        Element title, Element body, string automationName, string automationId, bool expanded, Action<bool> onExpanded,
        double? ruledLabelWidth = null)
    {
        var header = ToolCallCardRenderer.BuildDisclosureHeader(expanded, title);
        return Expander(automationName, body, (Optional<bool>)expanded, onExpanded)
            .HeaderContent(ruledLabelWidth is { } width ? Ruled(header.MaxWidth(width)) : header)
            .OnUnmount(ChatVisuals.StopToolRowShimmers)
            .MinHeight(ChatVisuals.ToolRowMinHeight)
            .Padding(0)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderBrush(Theme.Ref("SubtleFillColorTransparentBrush"))
            .Resources(ChatVisuals.ToolRowExpanderResources)
            .Set(ChatVisuals.FlushToolRowExpander)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationName(automationName)
            .AutomationId(automationId)
            .Margin(0, 4, 0, 0);
    }

    private static Element Notice(Element line, Element? body) =>
        body is null
            ? line.Margin(0, 8)
            : VStack(6, line.WithKey("line"), body.WithKey("body")).Margin(0, 8);

    private static Element? Body(ReactorTimelineRow row, string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : ReactorChatTimeline.BuildSafeMarkdown(text, row.Props.TryCopyText, compactSecondaryReading: true);

    /// <summary>Web SystemLine: hairline, optional icon, label and metric, hairline.</summary>
    internal static Element SystemLine(string? glyph, string label, double labelWidth, string? metric = null)
    {
        var parts = new List<Element>();
        if (glyph is not null)
            parts.Add(Glyph(glyph).WithKey("icon"));
        parts.Add(Muted(label).TextAlignment(TextAlignment.Center).WithKey("label"));
        if (!string.IsNullOrEmpty(metric))
        {
            parts.Add(Muted("·").AccessibilityView(AccessibilityView.Raw).WithKey("separator"));
            parts.Add(Muted(metric).WithKey("metric"));
        }

        return Ruled(
                (FlexRow(parts.ToArray()) with
                {
                    Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap,
                    ColumnGap = 6,
                    RowGap = 2,
                    AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Center,
                }).MaxWidth(labelWidth))
            .AutomationName(string.IsNullOrEmpty(metric) ? label : $"{label}, {metric}");
    }

    /// <summary>Centers <paramref name="content"/> between two hairlines that share the remaining width.</summary>
    internal static Element Ruled(Element content) =>
        Grid([GridSize.Star(), GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Hairline().Grid(column: 0).Margin(0, 0, HairlineGap, 0),
            // Non-Stretch opts Reactor's flex panel into fit-content measurement.
            content.Grid(column: 1).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
            Hairline().Grid(column: 2).Margin(HairlineGap, 0, 0, 0));

    private static Element Hairline() =>
        Border(Empty())
            .Height(1)
            .MinWidth(HairlineMinWidth)
            .VAlign(VerticalAlignment.Center)
            .Background(Theme.Ref("DividerStrokeColorDefaultBrush"));

    internal static TextBlockElement Glyph(string glyph, string brush = MutedBrush) =>
        TextBlock(glyph)
            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
            .FontSize(GlyphSize)
            .Foreground(Theme.Ref(brush))
            .VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw)
            .Set(text => text.IsTextScaleFactorEnabled = false);

    internal static TextBlockElement Muted(string text, string brush = MutedBrush) =>
        ReactorChatTimeline.Text(text, ChatVisuals.ActivityTextFontSize, FontWeights.Normal, brush)
            .VAlign(VerticalAlignment.Center);

    /// <summary>Label width that leaves room for both minimum hairlines inside the prose column.</summary>
    internal static double LabelMaxWidth(ReactorTimelineRow row) =>
        Math.Max(MinLabelWidth, ChatVisuals.ProseWidth(row.Props.ViewportWidth) - 2 * (HairlineMinWidth + HairlineGap));

    internal static string AutomationSuffix(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    internal static string L(string key, string fallback) => ReactorChatTimeline.LocalizedOrDefault(key, fallback);

    internal static double NameMaxWidth => SkillNameMaxWidth;
}

internal sealed record ReactorNoticeDisclosureProps(
    ChatTimelinePresentationContext Timeline, string EntryId, Func<bool, Action<bool>, Element> Build);

/// <summary>
/// Notice disclosure with its open state in the tool-row expansion store, so a recycled row never
/// inherits another notice's state and collapse-all closes notices with the tool rows.
/// </summary>
internal sealed class ReactorNoticeDisclosure : Component<ReactorNoticeDisclosureProps>
{
    public override Element Render()
    {
        var (localExpanded, setLocalExpanded) = UseState(false, threadSafe: true);
        var timeline = Props.Timeline;
        var key = "notice|" + ChatToolActivityPresentation.ToolRowKey(
            timeline.SessionId, timeline.TimelineGeneration, Props.EntryId);
        var expanded = timeline.ToolExpansionState?.IsExpanded(key, timeline.ToolCallsCollapseVersion) ?? localExpanded;
        return Props.Build(expanded, value =>
        {
            timeline.ToolExpansionState?.SetExplicit(key, value, timeline.ToolCallsCollapseVersion);
            setLocalExpanded(value);
        });
    }
}

internal sealed record ReactorSkillLearnedNoticeProps(
    ChatSkillChangeNotice Notice, ChatSkillUndoTracker? Tracker, string EntryId, double LabelWidth);

/// <summary>
/// Web learned-skill notice: hairlines around "Learned · Created name …" and an Undo that is offered
/// only while the Gateway advertises <c>skills.workshop.undo</c> to an admin operator. The tracker
/// owns the request so a remounted row shows the same pending, undone or failed state.
/// </summary>
internal sealed class ReactorSkillLearnedNotice : Component<ReactorSkillLearnedNoticeProps>
{
    public override Element Render()
    {
        var props = Props;
        var tracker = props.Tracker;
        var notice = props.Notice;
        var (_, setVersion) = UseState(0, threadSafe: true);
        var version = UseRef(0);
        var mounted = UseRef(true);

        void Observe(Task<ChatSkillUndoStatus>? task) =>
            task?.ContinueWith(
                _ =>
                {
                    if (mounted.Current)
                        setVersion(++version.Current);
                },
                TaskScheduler.Default);

        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () => mounted.Current = false;
        }), Array.Empty<object>());
        UseEffect(() => Observe(tracker?.PendingTask(notice)), notice, tracker);

        var status = tracker?.StatusOf(notice) ?? ChatSkillUndoStatus.Idle;
        var undone = status == ChatSkillUndoStatus.Undone;
        var pending = status == ChatSkillUndoStatus.Pending;
        var label = ReactorChatNotices.L("Chat_SkillLearned_Label", "Learned");
        var suffix = ReactorChatNotices.AutomationSuffix(props.EntryId);

        var parts = new List<Element>
        {
            ReactorChatNotices.Glyph(FluentIconCatalog.Lightbulb).WithKey("icon"),
            ReactorChatNotices.Muted(label).FontWeight(FontWeights.SemiBold).WithKey("label"),
        };
        foreach (var skill in notice.Skills)
            parts.Add(Skill(skill, undone).WithKey("skill:" + skill.Action + ":" + skill.Name));

        if (undone)
        {
            parts.Add(HStack(4,
                    ReactorChatNotices.Glyph(FluentIconCatalog.Check, "SystemFillColorSuccessBrush"),
                    ReactorChatNotices.Muted(ReactorChatNotices.L("Chat_SkillLearned_Undone", "Undone"),
                        "SystemFillColorSuccessBrush").FontWeight(FontWeights.SemiBold))
                .Margin(6, 0, 0, 0)
                .AutomationId("ChatSkillUndone_" + suffix)
                .LiveRegion(AutomationLiveSetting.Polite)
                .WithKey("undone"));
        }
        else if (tracker?.CanUndo == true || pending)
        {
            var undoLabel = pending
                ? ReactorChatNotices.L("Chat_SkillLearned_Undoing", "Undoing…")
                : ReactorChatNotices.L("Chat_SkillLearned_Undo", "Undo");
            parts.Add(Button(
                    HStack(4,
                        pending
                            ? ChatVisuals.SubagentSpinner(12).WithKey("spinner")
                            : ReactorChatNotices.Glyph(FluentIconCatalog.Undo).WithKey("icon"),
                        ReactorChatNotices.Muted(undoLabel).FontWeight(FontWeights.SemiBold).WithKey("label")),
                    () =>
                    {
                        if (tracker is null || tracker.StatusOf(notice) == ChatSkillUndoStatus.Pending)
                            return;
                        Observe(tracker.UndoAsync(notice));
                        setVersion(++version.Current);
                    })
                .SubtleButton()
                .Padding(6, 2)
                .MinHeight(0)
                .MinWidth(0)
                .Margin(6, 0, 0, 0)
                .IsEnabled(!pending)
                .Opacity(pending ? 0.6 : 1)
                .AutomationName(undoLabel)
                .AutomationId("ChatSkillUndo_" + suffix)
                .WithKey("undo"));
        }

        var line = ReactorChatNotices.Ruled(
            (FlexRow(parts.ToArray()) with
            {
                Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap,
                ColumnGap = 6,
                RowGap = 2,
                AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Center,
            }).MaxWidth(props.LabelWidth));
        Element error = status == ChatSkillUndoStatus.Failed
            ? ReactorChatNotices.Muted(
                    ReactorChatNotices.L("Chat_SkillLearned_UndoError", "Couldn’t undo the skill change. Try again."),
                    "SystemFillColorCriticalBrush")
                .TextAlignment(TextAlignment.Center)
                .HAlign(HorizontalAlignment.Center)
                .MaxWidth(props.LabelWidth)
                .Margin(0, 6, 0, 0)
                .AutomationId("ChatSkillUndoError_" + suffix)
                .LiveRegion(AutomationLiveSetting.Assertive)
            : Empty();
        return VStack(0, line.WithKey("line"), error.WithKey("error"))
            .Margin(0, 8)
            .AutomationName(label)
            .AutomationId("ChatSkillLearned_" + suffix);
    }

    private static Element Skill(ChatSkillChange skill, bool undone) =>
        HStack(4,
                ReactorChatNotices.Muted("·").AccessibilityView(AccessibilityView.Raw),
                ReactorChatNotices.Muted(ChatNoticePresentation.SkillVerb(skill.Action, ReactorChatNotices.L))
                    .AccessibilityView(AccessibilityView.Raw),
                TextBlock(skill.Name)
                    .FontFamily(ChatVisuals.CodeFontFamily)
                    .FontSize(ChatVisuals.ActivityTextFontSize)
                    .Foreground(Theme.Ref(undone ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"))
                    .TextDecorations(undone
                        ? global::Windows.UI.Text.TextDecorations.Strikethrough
                        : global::Windows.UI.Text.TextDecorations.None)
                    .TextWrapping(TextWrapping.NoWrap)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxWidth(ReactorChatNotices.NameMaxWidth)
                    .VAlign(VerticalAlignment.Center)
                    .ToolTip(ChatNoticePresentation.SkillToolTip(skill) ?? skill.Name)
                    .AccessibilityView(AccessibilityView.Raw))
            .AutomationName(ChatNoticePresentation.SkillAccessibleName(skill, ReactorChatNotices.L));
}
