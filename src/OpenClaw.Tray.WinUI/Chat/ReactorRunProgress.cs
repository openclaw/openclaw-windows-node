using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
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
    private const int StatusSwapMilliseconds = 220;

    public override Element Render()
    {
        var progress = Props.Progress;
        var entrance = UseRef<Storyboard?>(null);
        var reducedMotion = UseReducedMotion();
        var (now, setNow) = UseState(DateTimeOffset.UtcNow);
        UseEffect((Func<Action>)(() =>
        {
            if (!progress.IsInProgress)
                return static () => { };
            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            TypedEventHandler<DispatcherQueueTimer, object> tick = (_, _) => setNow(DateTimeOffset.UtcNow);
            timer.Tick += tick;
            timer.Start();
            return () => { timer.Stop(); timer.Tick -= tick; };
        }), progress.IsInProgress, progress.StartedAt);
        var elapsed = ChatRunProgressProjection.Elapsed(progress.StartedAt,
            progress.Waiting ? now : progress.EndedAt ?? (progress.Active ? now : progress.StartedAt ?? now));
        var label = progress.Label == ChatRunProgressProjection.RetryLabel
            ? ChatRunProgressProjection.RetryLabelText(progress, now, ReactorQuestionPanel.L)
            : LocalizedLabel(progress.Label);
        var text = elapsed.Length > 0 ? $"{label} ({elapsed})" : label;
        var details = elapsed;
        if (progress.OutputTokens is { } tokens)
        {
            text += ", " + string.Format(ReactorQuestionPanel.L("Chat_Run_OutputTokens", "{0:N0} output tokens"), tokens);
            details = string.Format(ReactorQuestionPanel.L("Chat_Run_OutputTokens", "{0:N0} output tokens"), tokens)
                + (elapsed.Length > 0 ? " \u00b7 " + elapsed : "");
        }
        // A phase change swaps lines: the old line rises out of the clipped row while the new
        // line rises in from one line below, matching the reference recording frame by frame.
        var line = details.Length > 0 ? $"{label} \u00b7 {details}" : label;
        var incomingRef = this.UseElementRef<Microsoft.UI.Xaml.Controls.Grid>();
        var outgoingRef = this.UseElementRef<Microsoft.UI.Xaml.Controls.TextBlock>();
        var shown = UseRef<(string Phase, string Line)?>(null);
        var outgoingLine = UseRef("");
        var swapPending = UseRef(false);
        if (shown.Current is { } last && last.Phase != progress.Label && !reducedMotion)
        {
            outgoingLine.Current = last.Line;
            swapPending.Current = true;
        }
        shown.Current = (progress.Label, line);
        UseEffect((Func<Action>)(() =>
        {
            if (swapPending.Current && incomingRef.Current is { } incoming && outgoingRef.Current is { } outgoing)
                AnimateStatusSwap(incoming, outgoing);
            swapPending.Current = false;
            return static () => { };
        }), progress.Label);
        return Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Grid([GridSize.Star()], [GridSize.Star()],
                NativeWorkingClawElement.NativeWorkingClaw()
                    .Set(claw => claw.Update(progress.IsInProgress, progress.Waiting, progress.StartedAt?.ToString("O") ?? "idle"))
                    .Size(18, 18).Opacity(1))
                .Size(18, 18).Grid(column: 0).VAlign(VerticalAlignment.Center)
                .AccessibilityView(AccessibilityView.Raw).AutomationId("ChatRunProgressClaw"),
            Grid([GridSize.Star()], [GridSize.Auto],
                    // Star then Auto on a left-aligned grid: the status trims before the elapsed time.
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        ChatVisuals.ActivityText(label, progress.Active && !progress.Waiting)
                            .AutomationId("ChatRunProgressStatus").Grid(column: 0),
                        TextBlock(details.Length > 0 ? " \u00b7 " + details : "").FontSize(12)
                            .Set(ChatVisuals.StyleSecondaryText)
                            .TextWrapping(TextWrapping.NoWrap)
                            .AutomationName(text)
                            .AutomationId("ChatRunProgressText").Grid(column: 1))
                        .HAlign(HorizontalAlignment.Left)
                        .Ref(incomingRef),
                    TextBlock(outgoingLine.Current).FontSize(12)
                        .Set(ChatVisuals.StyleSecondaryText)
                        .TextWrapping(TextWrapping.NoWrap)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .IsHitTestVisible(false)
                        .AccessibilityView(AccessibilityView.Raw)
                        .Ref(outgoingRef))
                .Set(ClipToBounds)
                .OnUnmount(ResetStatusSwap)
                .Grid(column: 1).Margin(8, 0, 0, 0).VAlign(VerticalAlignment.Center))
            .MinHeight(28).Margin(0, 4, 0, 2).Opacity(1).AutomationId("ChatRunProgress").AutomationName(text)
            .OnMount(row =>
            {
                if (entrance.Current is null && progress.IsInProgress &&
                    new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    entrance.Current = AnimateEntrance(row);
            })
            .OnUnmount(_ => entrance.Current?.Stop())
            .LiveRegion(AutomationLiveSetting.Off);
    }

    private static void ClipToBounds(FrameworkElement host)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host);
        visual.Clip ??= visual.Compositor.CreateInsetClip();
    }

    // Reactor can pool these controls; composition state is not part of its cleanup.
    private static void ResetStatusSwap(FrameworkElement host)
    {
        var hostVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host);
        hostVisual.Clip = null;
        if (host is not Microsoft.UI.Xaml.Controls.Panel panel)
            return;
        foreach (var child in panel.Children)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(child);
            // Translation exists only after a swap has enabled it.
            if (visual.Properties.TryGetVector3("Translation", out _) ==
                Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded)
            {
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
            }
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
        }
    }

    /// <summary>
    /// Both lines travel one line height on composition visuals, so layout and focus never move.
    /// Cubic ease-out puts most travel in the first ~100 ms, as in the reference.
    /// </summary>
    private static void AnimateStatusSwap(FrameworkElement incoming, FrameworkElement outgoing)
    {
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(incoming).Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new(.215f, .61f), new(.355f, 1f));
        var distance = (float)Math.Max(incoming.ActualHeight, 16);
        void Animate(FrameworkElement element, float fromY, float toY, float fromOpacity, float toOpacity)
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0, new(0, fromY, 0));
            move.InsertKeyFrame(1, new(0, toY, 0), easing);
            move.Duration = TimeSpan.FromMilliseconds(StatusSwapMilliseconds);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, fromOpacity);
            fade.InsertKeyFrame(1, toOpacity, easing);
            fade.Duration = move.Duration;
            visual.StartAnimation("Translation", move);
            visual.StartAnimation("Opacity", fade);
        }
        Animate(incoming, distance, 0, 0, 1);
        Animate(outgoing, 0, -distance, 1, 0);
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
