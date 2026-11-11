// Hosts the current conversation beside an explicitly opened, read-only child pane.
// The chat root supplies its existing conversation element and current Gateway child snapshot.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Elements;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatSubagentsProps(
    string? ParentId, bool Connected, Element Conversation,
    IReadOnlyList<ChatThread> Children, Func<ChatThread, Element> RenderTranscript);

/// <summary>Owns transient list/detail selection without changing the primary chat selection.</summary>
internal sealed class ReactorChatSubagents : Component<ReactorChatSubagentsProps>
{
    public override Element Render()
    {
        var props = Props;
        var (open, setOpen) = UseState(false);
        var (selectedId, setSelectedId) = UseState<string?>(null);
        var (width, setWidth) = UseState(800d);
        var scope = UseRef<string?>(props.ParentId);
        var toggle = UseRef<Button?>(null);
        var close = UseRef<Button?>(null);
        var rows = UseRef(new Dictionary<string, Button>(StringComparer.Ordinal));
        var focusTarget = UseRef<string?>(null);
        var (focusRevision, setFocusRevision) = UseState(0);
        if (scope.Current != props.ParentId || !props.Connected)
        {
            scope.Current = props.ParentId;
            focusTarget.Current = null;
            if (open) setOpen(false);
            if (selectedId is not null) setSelectedId(null);
            open = false;
            selectedId = null;
        }
        var selected = props.Children.FirstOrDefault(child => child.Id == selectedId);
        if (selectedId is not null && selected is null)
        {
            setSelectedId(null);
            selectedId = null;
            focusTarget.Current = "close";
            setFocusRevision(focusRevision + 1);
        }
        var narrow = width < ChatVisuals.SubagentsNarrowWidth;
        void FocusAfterRender(string target)
        {
            focusTarget.Current = target;
            setFocusRevision(focusRevision + 1);
        }
        void Close()
        {
            setOpen(false);
            setSelectedId(null);
            FocusAfterRender("toggle");
        }
        UseEffect((Func<Action>)(() =>
        {
            var target = focusTarget.Current;
            focusTarget.Current = null;
            if (target is null) return static () => { };
            var retired = false;
            // Visibility is reconciled after render; focus only after the new layout is applied.
            DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (retired) return;
                var control = target == "toggle" ? toggle.Current
                    : target == "close" ? close.Current
                    : rows.Current.GetValueOrDefault(target) ?? close.Current;
                if (control?.IsLoaded == true)
                {
                    control.UpdateLayout();
                    control.Focus(FocusState.Keyboard);
                }
            });
            return () => retired = true;
        }), focusRevision, props.ParentId ?? "", props.Connected);

        var title = L("Chat_Subagents_Title", "Subagents");
        var list = ScrollViewer(VStack(ChatVisuals.CompactGap,
            props.Children.Count == 0
                ? [TextBlock(L("Chat_Subagents_Empty", "No subagents in the current session list."))
                    .TextWrapping(TextWrapping.Wrap).Padding(ChatVisuals.DetailHorizontalInset)]
                : props.Children.Select(child =>
                {
                    var status = Status(child);
                    return (Element)Button(VStack(ChatVisuals.CompactGap,
                        TextBlock(child.Title).TextWrapping(TextWrapping.Wrap),
                        TextBlock(status).FontSize(ChatVisuals.ToolRowFontSize)
                            .Set(ChatVisuals.StyleSecondaryText),
                        string.IsNullOrEmpty(child.CurrentActivity) ? Empty()
                            : TextBlock(child.CurrentActivity).FontSize(ChatVisuals.ToolRowFontSize)
                                .TextWrapping(TextWrapping.Wrap).Set(ChatVisuals.StyleSecondaryText)),
                        () =>
                        {
                            setSelectedId(child.Id);
                            FocusAfterRender("close");
                        })
                        .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                        .HAlign(HorizontalAlignment.Stretch)
                        .Padding(ChatVisuals.DetailHorizontalInset, ChatVisuals.DetailVerticalInset)
                        .AutomationId($"ChatSubagent_{child.Id}")
                        .AutomationName($"{child.Title}: {status}")
                        .OnMount(control => rows.Current[child.Id] = (Button)control)
                        .OnUnmount(_ => rows.Current.Remove(child.Id))
                        .WithKey(child.Id);
                }).ToArray()))
            .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
            .IsVisible(selected is null)
            .AutomationId("ChatSubagentsList");
        var header = Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            ActionButton(FluentIconCatalog.Back, L("Chat_Subagents_Back", "Back to subagents"), () =>
                {
                    setSelectedId(null);
                    FocusAfterRender(selectedId ?? "close");
                })
                .AutomationId("ChatSubagentsBack").IsVisible(selected is not null).Grid(column: 0),
            TextBlock(selected?.Title ?? title).FontSize(ChatVisuals.BodySize)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(selected?.Title ?? title).VAlign(VerticalAlignment.Center)
                .Margin(ChatVisuals.BlockGap, 0).Grid(column: 1),
            ActionButton(FluentIconCatalog.Exit, L("Chat_Subagents_Close", "Close subagents"), Close)
                .OnMount(control => close.Current = (Button)control)
                .OnUnmount(_ => close.Current = null)
                .AutomationId("ChatSubagentsClose").Grid(column: 2));
        var pane = Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                header.Margin(ChatVisuals.BlockGap).Grid(row: 0),
                Grid([GridSize.Star()], [GridSize.Star()],
                    list,
                    Border(open && selected is not null ? props.RenderTranscript(selected) : Empty())
                        .IsVisible(selected is not null).AutomationId("ChatSubagentTranscript"))
                    .Grid(row: 1)))
            .Background(Theme.Ref("LayerFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("ChatStrokeBrush")).BorderThickness(narrow ? 0 : 1, 0, 0, 0)
            .IsVisible(open).AutomationId("ChatSubagentsPane")
            .AutomationName(title)
            .Grid(column: narrow ? 0 : 1, row: 0, rowSpan: 2);
        var paneWidth = open && !narrow
            ? Math.Clamp(width * ChatVisuals.SubagentsPaneWidthRatio,
                ChatVisuals.SubagentsPaneMinWidth, ChatVisuals.SubagentsPaneWidth)
            : 0;
        return Grid([GridSize.Star(), GridSize.Px(paneWidth)], [GridSize.Auto, GridSize.Star()],
            Button(title, () =>
                {
                    if (open) Close();
                    else
                    {
                        setOpen(true);
                        FocusAfterRender("close");
                    }
                })
                .OnMount(control => toggle.Current = (Button)control)
                .OnUnmount(_ => toggle.Current = null)
                .AutomationId("ChatSubagentsToggle").AutomationName(title)
                .IsVisible(props.Connected && props.ParentId is not null && !(open && narrow))
                .HAlign(HorizontalAlignment.Right)
                .Margin(ChatVisuals.DetailHorizontalInset, ChatVisuals.CompactGap).Grid(row: 0),
            Border(props.Conversation).IsVisible(!(open && narrow))
                .AutomationId("ChatMainConversation").Grid(row: 1),
            pane)
            .OnMount(control => ChatVisuals.Observe(control, setWidth))
            .OnUnmount(ChatVisuals.StopObserving);
    }

    private static ButtonElement ActionButton(string glyph, string name, Action action) =>
        Button(TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily), action)
            .MinWidth(ChatVisuals.ToolRowMinHeight).MinHeight(ChatVisuals.ToolRowMinHeight)
            .AutomationName(name).ToolTip(name);

    private static string Status(ChatThread child) => child.Activity switch
    {
        ChatActivity.Working => L("Chat_Run_Working", "Working"),
        ChatActivity.AwaitingInput => L("Chat_Run_Answer", "Waiting for your answer"),
        ChatActivity.AwaitingPermission => L("Chat_Run_Approval", "Waiting for approval"),
        ChatActivity.Error => L("Chat_Tool_Error", "Error"),
        _ => child.WaitingForSubagents ? L("Chat_Run_Subagents", "Waiting for subagents")
            : child.Status == ChatThreadStatus.Ended ? L("Chat_Run_Finished", "Finished")
            : L("Chat_Children_Idle", "Idle"),
    };

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
