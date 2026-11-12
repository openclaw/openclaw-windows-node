// List/detail content for the shared conversation panel's Subagents tab.
// The chat root supplies the current direct-child snapshot and a read-only transcript renderer.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatSubagentsProps(
    string? ParentId,
    IReadOnlyList<ChatThread> Children, Func<ChatThread, Element> RenderTranscript);

/// <summary>Owns transient list/detail selection without changing the primary chat selection.</summary>
internal sealed class ReactorChatSubagents : Component<ReactorChatSubagentsProps>
{
    public override Element Render()
    {
        var props = Props;
        var (selectedId, setSelectedId) = UseState<string?>(null);
        var scope = UseRef<string?>(props.ParentId);
        var back = UseRef<Button?>(null);
        var listControl = UseRef<ScrollViewer?>(null);
        var rows = UseRef(new Dictionary<string, Button>(StringComparer.Ordinal));
        var focusTarget = UseRef<string?>(null);
        var (focusRevision, setFocusRevision) = UseState(0);
        if (scope.Current != props.ParentId)
        {
            scope.Current = props.ParentId;
            focusTarget.Current = null;
            if (selectedId is not null) setSelectedId(null);
            selectedId = null;
        }
        var selected = props.Children.FirstOrDefault(child => child.Id == selectedId);
        if (selectedId is not null && selected is null)
        {
            setSelectedId(null);
            selectedId = null;
            focusTarget.Current = "list";
            setFocusRevision(focusRevision + 1);
        }
        void FocusAfterRender(string target)
        {
            focusTarget.Current = target;
            setFocusRevision(focusRevision + 1);
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
                Control? control = target == "back" ? back.Current
                    : rows.Current.GetValueOrDefault(target) ?? (Control?)listControl.Current;
                if (control?.IsLoaded == true)
                {
                    control.UpdateLayout();
                    control.Focus(FocusState.Keyboard);
                }
            });
            return () => retired = true;
        }), focusRevision, props.ParentId ?? "");

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
                            FocusAfterRender("back");
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
            .OnMount(control => listControl.Current = (ScrollViewer)control)
            .OnUnmount(_ => listControl.Current = null)
            .IsVisible(selected is null)
            .AutomationId("ChatSubagentsList");
        var header = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Button(TextBlock(FluentIconCatalog.Back).FontFamily(FluentIconCatalog.SymbolThemeFontFamily), () =>
                {
                    setSelectedId(null);
                    FocusAfterRender(selectedId ?? "list");
                })
                .MinWidth(ChatVisuals.ToolRowMinHeight).MinHeight(ChatVisuals.ToolRowMinHeight)
                .AutomationName(L("Chat_Subagents_Back", "Back to subagents"))
                .ToolTip(L("Chat_Subagents_Back", "Back to subagents"))
                .OnMount(control => back.Current = (Button)control)
                .OnUnmount(_ => back.Current = null)
                .AutomationId("ChatSubagentsBack").Grid(column: 0),
            TextBlock(selected?.Title ?? title).FontSize(ChatVisuals.BodySize)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(selected?.Title ?? title).VAlign(VerticalAlignment.Center)
                .Margin(ChatVisuals.BlockGap, 0).Grid(column: 1))
            .IsVisible(selected is not null);
        return Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                header.Margin(ChatVisuals.BlockGap).Grid(row: 0),
                Grid([GridSize.Star()], [GridSize.Star()],
                    list,
                    Border(selected is not null ? props.RenderTranscript(selected) : Empty())
                        .IsVisible(selected is not null).AutomationId("ChatSubagentTranscript"))
                    .Grid(row: 1)))
            .AutomationId("ChatSubagentsPane")
            .AutomationName(title);
    }

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
