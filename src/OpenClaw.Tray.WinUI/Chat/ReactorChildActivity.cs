// Native read-only child activity, composed from the authenticated session snapshot.
// The timeline passes only direct children of its selected conversation.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using OpenClaw.Chat;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChildActivityProps(IReadOnlyList<ChatThread> Children);

internal sealed class ReactorChildActivity : Component<ReactorChildActivityProps>
{
    public override Element Render()
    {
        var children = Props.Children;
        if (children.Count == 0)
            return Empty();
        var working = children.Count(child => child.Activity == ChatActivity.Working);
        var attention = children.Count(child => child.Activity is ChatActivity.AwaitingInput
            or ChatActivity.AwaitingPermission or ChatActivity.Error);
        var heading = string.Format(ReactorQuestionPanel.L("Chat_Children_Summary",
            "{0} subagents, {1} working, {2} need attention"), children.Count, working, attention);
        return Expander(heading, VStack(ChatVisuals.CompactGap, children.Select(child =>
        {
            var status = child.Activity switch
            {
                ChatActivity.Working => ReactorQuestionPanel.L("Chat_Run_Working", "Working"),
                ChatActivity.AwaitingInput => ReactorQuestionPanel.L("Chat_Run_Answer", "Waiting for your answer"),
                ChatActivity.AwaitingPermission => ReactorQuestionPanel.L("Chat_Run_Approval", "Waiting for approval"),
                ChatActivity.Error => ReactorQuestionPanel.L("Chat_Tool_Error", "Error"),
                _ => child.Status == ChatThreadStatus.Ended
                    ? ReactorQuestionPanel.L("Chat_Run_Finished", "Finished")
                    : ReactorQuestionPanel.L("Chat_Children_Idle", "Idle"),
            };
            return VStack(4,
                TextBlock($"{child.Title}: {status}").TextWrapping(TextWrapping.Wrap),
                string.IsNullOrEmpty(child.CurrentActivity) ? Empty()
                    : TextBlock(child.CurrentActivity).FontSize(12).TextWrapping(TextWrapping.Wrap)
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                .WithKey(child.Id);
        }).ToArray()).Padding(ChatVisuals.DetailHorizontalInset, ChatVisuals.DetailVerticalInset))
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId("ChatChildActivity").AutomationName(heading);
    }
}
