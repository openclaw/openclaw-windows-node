using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatCopyButtonProps(
    string Identity,
    string Text,
    string Label,
    Func<string, bool>? TryCopy = null,
    Action<bool>? FocusChanged = null,
    TimeSpan? ResetAfter = null,
    bool UseInstanceAutomationId = false);

/// <summary>Per-button, content-fenced feedback. No clipboard contents are retained beyond the view props.</summary>
internal sealed class ChatCopyButton : Component<ChatCopyButtonProps>
{
    private enum Status { Idle, Copied, Failed }
    private readonly record struct Feedback(string Identity, string Text, Status Status, long Sequence);

    public override Element Render()
    {
        var props = Props;
        var instanceId = UseRef<string?>(null);
        if (props.UseInstanceAutomationId)
            instanceId.Current ??= Guid.NewGuid().ToString("N");
        var current = UseRef(props);
        current.Current = props;
        var mounted = UseRef(true);
        var generation = UseRef(0L);
        var cancel = UseRef<Action?>(null);
        var announced = UseRef(0L);
        var (feedback, setFeedback) = UseState(default(Feedback));
        var status = feedback.Identity == props.Identity && feedback.Text == props.Text
            ? feedback.Status : Status.Idle;

        void Cancel()
        {
            generation.Current++;
            cancel.Current?.Invoke();
            cancel.Current = null;
        }

        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () => { mounted.Current = false; Cancel(); };
        }), Array.Empty<object>());
        UseEffect((Func<Action>)(() =>
        {
            setFeedback(default);
            return Cancel;
        }), props.Identity, props.Text);

        void Copy()
        {
            Cancel();
            var sequence = generation.Current;
            var success = props.TryCopy?.Invoke(props.Text)
                ?? ClipboardHelper.TryCopyText(props.Text, flush: true);
            if (!mounted.Current || sequence != generation.Current
                || current.Current.Identity != props.Identity || current.Current.Text != props.Text)
                return;

            setFeedback(new(props.Identity, props.Text, success ? Status.Copied : Status.Failed, sequence));
            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = props.ResetAfter ?? TimeSpan.FromSeconds(2);
            timer.IsRepeating = false;
            TypedEventHandler<DispatcherQueueTimer, object> tick = (_, _) =>
            {
                if (!mounted.Current || sequence != generation.Current)
                    return;
                cancel.Current?.Invoke();
                cancel.Current = null;
                setFeedback(default);
            };
            timer.Tick += tick;
            cancel.Current = () => { timer.Stop(); timer.Tick -= tick; };
            timer.Start();
        }

        var label = status switch
        {
            Status.Copied => ChatMarkdownPresentation.Localized("Chat_Copy_Copied", "Copied"),
            Status.Failed => ChatMarkdownPresentation.Localized("Chat_Copy_Failed", "Copy failed"),
            _ => props.Label,
        };
        return Button(
                TextBlock(status switch
                {
                    Status.Copied => FluentIconCatalog.Check,
                    Status.Failed => FluentIconCatalog.StatusWarn,
                    _ => FluentIconCatalog.Copy,
                })
                    .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                    .FontSize(14)
                    .AccessibilityView(AccessibilityView.Raw)
                    .Set(text => text.IsTextScaleFactorEnabled = false),
                Copy)
            .SubtleButton()
            .Width(32).Height(32).MinWidth(32).MinHeight(32).Padding(4)
            .CornerRadius(4).BorderThickness(0)
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Foreground(Theme.Ref(status switch
            {
                Status.Copied => "ChatCopySuccessBrush",
                Status.Failed => "SystemFillColorCriticalBrush",
                _ => "ChatSecondaryTextBrush",
            }))
            .AutomationId("ChatCopy_" + props.Identity
                + (props.UseInstanceAutomationId ? "_" + instanceId.Current : string.Empty))
            .AutomationName(label)
            .ToolTip(label)
            .LiveRegion(AutomationLiveSetting.Polite)
            .OnGotFocus((_, _) => props.FocusChanged?.Invoke(true))
            .OnLostFocus((_, _) => props.FocusChanged?.Invoke(false))
            .Set(button =>
            {
                if (status == Status.Idle || feedback.Sequence == announced.Current)
                    return;
                announced.Current = feedback.Sequence;
                var sequence = feedback.Sequence;
                button.DispatcherQueue.TryEnqueue(() =>
                {
                    if (mounted.Current && sequence == generation.Current && button.IsLoaded)
                        FrameworkElementAutomationPeer.CreatePeerForElement(button)
                            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                });
            });
    }
}
