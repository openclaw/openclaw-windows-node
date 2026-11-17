// List content for the shared conversation panel's Subagents tab.
// The chat root supplies the current direct-child snapshot; rows are status-only for now.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatSubagentsProps(IReadOnlyList<ChatThread> Children, ChatConversationPanelState Panels);

/// <summary>Running and finished subagents with status, current activity and elapsed time.</summary>
internal sealed class ReactorChatSubagents : Component<ReactorChatSubagentsProps>
{
    // Elapsed labels show seconds, matching the run-progress row beside the composer.
    private static readonly TimeSpan ElapsedRefreshInterval = TimeSpan.FromSeconds(1);

    public override Element Render()
    {
        // The tick only re-renders; elapsed always uses the current time so newly started runs count up.
        var (tick, setTick) = UseState(0L);
        var panels = Props.Panels;
        var (_, setPanelRevision) = UseState(0L, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => setPanelRevision(++revision);
            panels.Changed += Changed;
            return () => panels.Changed -= Changed;
        }), panels);
        var summary = ChatSubagentPresentation.Project(Props.Children, DateTimeOffset.UtcNow);
        // The tab body stays mounted behind other tabs; only refresh while it is the selected tab.
        var ticking = summary.Running.Count > 0 && panels.Selected == ChatConversationPanelState.SubagentsSlot;
        UseEffect((Func<Action>)(() =>
        {
            if (!ticking) return static () => { };
            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = ElapsedRefreshInterval;
            var count = tick;
            TypedEventHandler<DispatcherQueueTimer, object> onTick = (_, _) => setTick(++count);
            timer.Tick += onTick;
            timer.Start();
            return () => { timer.Stop(); timer.Tick -= onTick; };
        }), ticking);

        var title = L("Chat_Subagents_Title", "Subagents");
        Element content = summary.Total == 0
            ? TextBlock(L("Chat_Subagents_Empty", "No subagents in the current session list."))
                .TextWrapping(TextWrapping.Wrap).Set(ChatVisuals.StyleSecondaryText)
                .Margin(ChatVisuals.DetailHorizontalInset)
            : VStack(ChatVisuals.BlockGap,
                Group("Running", L("Chat_Subagents_Running", "Running"), summary.Running),
                Group("Finished", L("Chat_Run_Finished", "Finished"), summary.Finished));
        return Border(ScrollViewer(content.Margin(ChatVisuals.BlockGap))
                .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
                .AutomationId("ChatSubagentsList"))
            .AutomationId("ChatSubagentsPane")
            .AutomationName(title);
    }

    private static Element Group(string id, string heading, IReadOnlyList<ChatSubagentRow> rows)
    {
        if (rows.Count == 0) return Empty();
        return VStack(ChatVisuals.CompactGap,
                HStack(ChatVisuals.BlockGap,
                    TextBlock(heading).FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Set(ChatVisuals.StyleSecondaryText).VAlign(VerticalAlignment.Center),
                    Border(TextBlock(rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))
                            .FontSize(12).Set(ChatVisuals.StyleSecondaryText))
                        .Padding(7, 0).CornerRadius(9)
                        .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .VAlign(VerticalAlignment.Center))
                    .Margin(ChatVisuals.BlockGap, ChatVisuals.CompactGap)
                    .HeadingLevel(AutomationHeadingLevel.Level3)
                    .AutomationName($"{heading}: {rows.Count}")
                    .AutomationId($"ChatSubagentsGroup_{id}"),
                VStack(0, rows.Select(Row).ToArray()))
            .WithKey(id);
    }

    private static Element Row(ChatSubagentRow row)
    {
        var child = row.Thread;
        var status = StatusText(row.Status);
        var detail = row.IsRunning && !string.IsNullOrWhiteSpace(child.CurrentActivity)
            ? $"{status} · {child.CurrentActivity}" : status;
        var elapsed = row.Elapsed is { } value
            ? ChatRunProgressProjection.ElapsedMilliseconds(value.TotalMilliseconds)
            : "";
        var failed = row.Status == ChatSubagentStatus.Failed;
        var showDetail = row.IsRunning || row.Status != ChatSubagentStatus.Done;
        var accessible = string.Join(", ", new[] { child.Title, detail, elapsed }.Where(part => part.Length > 0));
        return Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                Avatar(row).Grid(column: 0),
                VStack(2,
                    TextBlock(child.Title).FontSize(ChatVisuals.BodySize)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.Ref("ChatTextBrush"))
                        .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(child.Title),
                    showDetail
                        ? TextBlock(detail).FontSize(12)
                            .Foreground(Theme.Ref(failed ? "SystemFillColorCriticalBrush" : "ChatSecondaryTextBrush"))
                            .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(detail)
                        : Empty())
                    .VAlign(VerticalAlignment.Center).Margin(ChatVisuals.DetailHorizontalInset, 0).Grid(column: 1),
                TextBlock(elapsed).FontSize(12).Set(ChatVisuals.StyleSecondaryText)
                    .VAlign(VerticalAlignment.Center).Grid(column: 2))
            .MinHeight(row.IsRunning ? ChatVisuals.SubagentRowMinHeight : ChatVisuals.SubagentFinishedRowMinHeight)
            .Padding(ChatVisuals.BlockGap, ChatVisuals.CompactGap)
            .AutomationName(accessible)
            .ItemStatus(status)
            .AutomationId($"ChatSubagent_{child.Id}")
            .WithKey(child.Id);
    }

    private static Element Avatar(ChatSubagentRow row)
    {
        var size = row.IsRunning ? ChatVisuals.SubagentAvatarSize : ChatVisuals.SubagentAvatarSize - 8;
        var fill = row.Status switch
        {
            ChatSubagentStatus.Failed => "SystemFillColorCriticalBackgroundBrush",
            ChatSubagentStatus.Done => "SystemFillColorSuccessBackgroundBrush",
            _ => "SubtleFillColorSecondaryBrush",
        };
        Element? badge = row.Status switch
        {
            ChatSubagentStatus.Working or ChatSubagentStatus.AwaitingSubagents =>
                ChatVisuals.SubagentSpinner(ChatVisuals.SubagentBadgeSpinnerSize),
            ChatSubagentStatus.Done => Glyph(FluentIconCatalog.Check, "SystemFillColorSuccessBrush"),
            ChatSubagentStatus.Failed => Glyph(FluentIconCatalog.StatusErr, "SystemFillColorCriticalBrush"),
            _ => null,
        };
        // The badge overhangs the avatar inside the slot; a negative margin would clip it.
        var slot = size + ChatVisuals.SubagentBadgeOverhang;
        return Grid([GridSize.Star()], [GridSize.Star()],
                Border(TextBlock(FluentIconCatalog.Subagent).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                        .FontSize(row.IsRunning ? ChatVisuals.SubagentAvatarGlyphSize : ChatVisuals.SubagentAvatarGlyphSize - 4)
                        .Foreground(Theme.Ref("ChatTextBrush"))
                        .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                    .Size(size, size).CornerRadius(size / 2).Background(Theme.Ref(fill))
                    .HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Top),
                badge is null ? Empty()
                    : Border(badge.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                        .Size(ChatVisuals.SubagentBadgeSize, ChatVisuals.SubagentBadgeSize)
                        .CornerRadius(ChatVisuals.SubagentBadgeSize / 2)
                        .Background(Theme.Ref("ChatCardBrush"))
                        .BorderBrush(Theme.Ref("ChatStrokeBrush")).BorderThickness(1)
                        .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom))
            .Size(slot, slot).VAlign(VerticalAlignment.Center)
            .AccessibilityView(AccessibilityView.Raw);
    }

    private static Element Glyph(string glyph, string brush) =>
        TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(10)
            .Foreground(Theme.Ref(brush));

    private static string StatusText(ChatSubagentStatus status) => status switch
    {
        ChatSubagentStatus.Queued => L("Chat_Subagents_Queued", "Queued"),
        ChatSubagentStatus.Working => L("Chat_Run_Working", "Working"),
        ChatSubagentStatus.AwaitingAnswer => L("Chat_Run_Answer", "Waiting for your answer"),
        ChatSubagentStatus.AwaitingApproval => L("Chat_Run_Approval", "Waiting for approval"),
        ChatSubagentStatus.AwaitingSubagents => L("Chat_Run_Subagents", "Waiting for subagents"),
        ChatSubagentStatus.Stopped => L("Chat_Subagents_Stopped", "Stopped"),
        ChatSubagentStatus.Failed => L("Chat_Subagents_Failed", "Failed"),
        ChatSubagentStatus.Ended => L("Chat_Run_Finished", "Finished"),
        _ => L("Chat_Subagents_Done", "Done"),
    };

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
