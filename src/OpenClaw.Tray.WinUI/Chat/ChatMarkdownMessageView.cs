using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Markdown;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Services;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatMarkdownMessageProps(
    string Text, Func<string, bool>? TryCopyCode, bool CompactSecondaryReading);

/// <summary>
/// Mount through ReactorChatTimeline.BuildSafeMarkdown. Owns one message selection scope
/// and localized result feedback; Reactor alone writes selected text to the clipboard.
/// </summary>
internal sealed class ChatMarkdownMessageView : Component<ChatMarkdownMessageProps>
{
    private readonly record struct CopyFeedback(long ContentVersion, bool Success, long Sequence);

    public override Element Render()
    {
        var props = Props;
        var contentVersion = UseRef(0L);
        var mounted = UseRef(true);
        var sequence = UseRef(0L);
        var announced = UseRef(0L);
        var selection = UseRef<(string Text, MarkdownTextSelectionOptions Options)?>(null);
        var (feedback, setFeedback) = UseState<CopyFeedback?>(null);
        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () => mounted.Current = false;
        }), Array.Empty<object>());

        if (selection.Current is not { } existing || existing.Text != props.Text)
        {
            var version = ++contentVersion.Current;
            selection.Current = (props.Text, new MarkdownTextSelectionOptions
            {
                OnCopyResult = (_, success) =>
                {
                    if (!mounted.Current || contentVersion.Current != version)
                        return;
                    if (!success)
                        Logger.Warn("Markdown selection clipboard copy failed.");
                    setFeedback(new(version, success, ++sequence.Current));
                },
            });
        }

        UseEffect((Func<Action>)(() =>
        {
            if (feedback is not { } pending || pending.ContentVersion != contentVersion.Current)
                return () => { };
            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = ChatCopyButton.DefaultResetAfter;
            timer.IsRepeating = false;
            TypedEventHandler<DispatcherQueueTimer, object> tick = (_, _) =>
            {
                if (mounted.Current && sequence.Current == pending.Sequence
                    && contentVersion.Current == pending.ContentVersion)
                    setFeedback(null);
            };
            timer.Tick += tick;
            timer.Start();
            return () => { timer.Stop(); timer.Tick -= tick; };
        }), contentVersion.Current, feedback?.Sequence);

        var result = feedback.GetValueOrDefault();
        var active = feedback.HasValue && result.ContentVersion == contentVersion.Current;
        var label = active
            ? result.Success
                ? ChatMarkdownPresentation.Localized("Chat_Copy_Copied", "Copied")
                : ChatMarkdownPresentation.Localized("Chat_Copy_Failed", "Copy failed")
            : string.Empty;
        var options = selection.Current.Value.Options;
        return VStack(4,
            MarkdownTextSelection.Scope(options,
                ReactorChatTimeline.BuildMessageMarkdown(
                    props.Text, props.TryCopyCode, options, props.CompactSecondaryReading))
                .AutomationName(ChatMarkdownPresentation.Localized("Chat_Markdown_SelectionName", "Message text")),
            TextBlock(label)
                .Foreground(Theme.Ref(active && result.Success
                    ? "ChatCopySuccessBrush" : "SystemFillColorCriticalBrush"))
                .IsVisible(active)
                .AutomationId("ChatMarkdownCopyStatus")
                .AutomationName(label)
                .LiveRegion(AutomationLiveSetting.Polite)
                .Set(text =>
                {
                    if (!active || result.Sequence == announced.Current)
                        return;
                    announced.Current = result.Sequence;
                    text.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (mounted.Current && sequence.Current == result.Sequence
                            && contentVersion.Current == result.ContentVersion && text.IsLoaded)
                            FrameworkElementAutomationPeer.CreatePeerForElement(text)
                                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                    });
                }));
    }
}
