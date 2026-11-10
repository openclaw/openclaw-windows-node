using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorRunProgressProps(ChatRunProgressPresentation Progress);

internal sealed class ReactorRunProgress : Component<ReactorRunProgressProps>
{
    public override Element Render()
    {
        var progress = Props.Progress;
        var entrance = UseRef<Storyboard?>(null);
        var (now, setNow) = UseState(DateTimeOffset.UtcNow);
        UseEffect((Func<Action>)(() =>
        {
            if (!progress.Active)
                return static () => { };
            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            TypedEventHandler<DispatcherQueueTimer, object> tick = (_, _) => setNow(DateTimeOffset.UtcNow);
            timer.Tick += tick;
            timer.Start();
            return () => { timer.Stop(); timer.Tick -= tick; };
        }), progress.Active, progress.StartedAt);
        var elapsed = ChatRunProgressProjection.Elapsed(progress.StartedAt,
            progress.EndedAt ?? (progress.Active ? now : progress.StartedAt ?? now));
        var label = progress.Label == ChatRunProgressProjection.RetryLabel
            ? ChatRunProgressProjection.RetryLabelText(progress, now, ReactorQuestionPanel.L)
            : LocalizedLabel(progress.Label);
        var text = elapsed.Length > 0 ? $"{label} ({elapsed})" : label;
        var visibleText = elapsed;
        if (progress.OutputTokens is { } tokens)
        {
            text += ", " + string.Format(ReactorQuestionPanel.L("Chat_Run_OutputTokens", "{0:N0} output tokens"), tokens);
            visibleText = string.Format(ReactorQuestionPanel.L("Chat_Run_OutputTokens", "{0:N0} output tokens"), tokens)
                + (elapsed.Length > 0 ? " \u00b7 " + elapsed : "");
        }
        if (visibleText.Length == 0)
            visibleText = label;
        if (progress.Waiting || progress.Label is "Compacting context" or
            "Retrying after context compaction" or "Retrying the model request")
            visibleText = text;
        return Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Grid([GridSize.Star()], [GridSize.Star()],
                NativeWorkingClawElement.NativeWorkingClaw()
                    .Set(claw => claw.Update(progress.Active, progress.Waiting, progress.StartedAt?.ToString("O") ?? "idle"))
                    .Size(18, 18).Opacity(1))
                .Size(18, 18).Grid(column: 0).VAlign(VerticalAlignment.Center)
                .AccessibilityView(AccessibilityView.Raw).AutomationId("ChatRunProgressClaw"),
            TextBlock(visibleText).FontSize(12)
                .Set(ChatVisuals.StyleSecondaryText)
                .TextWrapping(TextWrapping.Wrap)
                .Grid(column: 1).Margin(8, 0, 0, 0)
                .VAlign(VerticalAlignment.Center).AutomationName(text)
                .AutomationId("ChatRunProgressText"))
            .MinHeight(28).Margin(0, 4, 0, 2).Opacity(1).AutomationId("ChatRunProgress").AutomationName(text)
            .OnMount(row =>
            {
                if (entrance.Current is null && progress.Active &&
                    new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    entrance.Current = AnimateEntrance(row);
            })
            .OnUnmount(_ => entrance.Current?.Stop())
            .LiveRegion(AutomationLiveSetting.Off);
    }

    private static Storyboard AnimateEntrance(FrameworkElement row)
    {
        var transform = new CompositeTransform();
        row.RenderTransform = transform;
        row.RenderTransformOrigin = new Point(.5, .5);
        var storyboard = new Storyboard();
        void Add(DependencyObject target, string property, double initial, double peak, double final)
        {
            var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = initial });
            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(144), Value = peak,
                KeySpline = new KeySpline { ControlPoint1 = new Point(.22, 1), ControlPoint2 = new Point(.36, 1) },
            });
            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(200), Value = final,
                KeySpline = new KeySpline { ControlPoint1 = new Point(.22, 1), ControlPoint2 = new Point(.36, 1) },
            });
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        Add(transform, "TranslateY", 4, -1, 0);
        Add(transform, "ScaleX", .96, 1.01, 1);
        Add(transform, "ScaleY", .96, 1.01, 1);
        storyboard.Begin();
        return storyboard;
    }

    private static string LocalizedLabel(string label)
    {
        var key = label switch
        {
            "Finished" => "Finished",
            "Waiting for approval" => "Approval",
            "Waiting for your answer" => "Answer",
            "Waiting for subagents" => "Subagents",
            "Compacting context" => "Compacting",
            "Retrying after context compaction" => "CompactionRetry",
            "Retrying the model request" => "Retry",
            "Thinking" => "Thinking",
            "Responding" => "Responding",
            "Working" => "Working",
            _ => null,
        };
        if (key is not null)
            return ReactorQuestionPanel.L("Chat_Run_" + key, label);
        return label.StartsWith("Running ", StringComparison.Ordinal)
            ? string.Format(ReactorQuestionPanel.L("Chat_Run_Tool", "Running {0}"), label[8..]) : label;
    }

}
