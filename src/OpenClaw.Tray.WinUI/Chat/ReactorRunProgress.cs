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
        var label = LocalizedLabel(progress.Label);
        var text = elapsed.Length > 0 ? $"{label} ({elapsed})" : label;
        if (progress.OutputTokens is { } tokens)
            text += ", " + string.Format(ReactorQuestionPanel.L("Chat_Run_OutputTokens", "{0:N0} output tokens"), tokens);
        var animation = UseRef<Storyboard?>(null);
        return Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Grid([GridSize.Star()], [GridSize.Star()],
                ClawPath(false),
                ClawPath(true)
                .Set(path =>
                {
                    if (path.RenderTransform is not RotateTransform)
                        path.RenderTransform = new RotateTransform { CenterX = 8.2, CenterY = 15.2 };
                })
                .OnMount(path =>
                {
                    if (!progress.Active || progress.Waiting || !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                        return;
                    var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
                    var motion = new DoubleAnimation
                    {
                        From = -10, To = 10, Duration = new Duration(TimeSpan.FromMilliseconds(600)),
                        EnableDependentAnimation = true,
                    };
                    Storyboard.SetTarget(motion, path.RenderTransform);
                    Storyboard.SetTargetProperty(motion, "Angle");
                    storyboard.Children.Add(motion);
                    animation.Current = storyboard;
                    storyboard.Begin();
                })
                .OnUnmount(_ => { animation.Current?.Stop(); animation.Current = null; })
                .WithKey($"{progress.Active}:{progress.Waiting}"))
                .Size(24, 24).Grid(column: 0).VAlign(VerticalAlignment.Top)
                .AccessibilityView(AccessibilityView.Raw).AutomationId("ChatRunProgressClaw"),
            TextBlock(text).FontSize(12).Set(ChatVisuals.StyleSecondaryText)
                .Grid(column: 1).Margin(8, 0, 0, 0)
                .TextWrapping(TextWrapping.Wrap).AutomationId("ChatRunProgressText"))
            .Margin(0, 12).AutomationId("ChatRunProgress").AutomationName(text)
            .LiveRegion(AutomationLiveSetting.Off);
    }

    private static string LocalizedLabel(string label)
    {
        var key = label switch
        {
            "Finished" => "Finished",
            "Waiting for approval" => "Approval",
            "Waiting for your answer" => "Answer",
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

    // Native geometry adapted from OpenClaw's MIT-licensed claw. See THIRD_PARTY_NOTICES.md.
    private static PathElement ClawPath(bool jaw) =>
        Path2D().StrokeThickness(1.6).StrokeLineJoin(PenLineJoin.Round)
            .Set(path =>
            {
                path.Data ??= CreateClaw(jaw);
                path.Style = (Style)Application.Current.Resources["ChatEffortStrokeStyle"];
                path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
            });

    private static Geometry CreateClaw(bool jaw)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = jaw ? new(5.6, 12.2) : new(10.2, 20), IsClosed = true };
        if (jaw)
        {
            figure.Segments.Add(new BezierSegment { Point1 = new(5.2, 5.6), Point2 = new(10.4, 1.4), Point3 = new(15.6, 2) });
            figure.Segments.Add(new BezierSegment { Point1 = new(19.4, 2.6), Point2 = new(21.8, 5.2), Point3 = new(22.6, 8.2) });
            figure.Segments.Add(new BezierSegment { Point1 = new(20.9, 7.7), Point2 = new(19.2, 7.6), Point3 = new(17.6, 7.9) });
            figure.Segments.Add(new LineSegment { Point = new(16.9, 6.3) });
            figure.Segments.Add(new LineSegment { Point = new(15.2, 8.5) });
            figure.Segments.Add(new BezierSegment { Point1 = new(13.6, 9.4), Point2 = new(12.2, 10.9), Point3 = new(11.6, 12.4) });
            figure.Segments.Add(new LineSegment { Point = new(6.8, 13) });
        }
        else
        {
            var hub = new PathFigure { StartPoint = new(8.2, 10), IsClosed = true };
            hub.Segments.Add(new ArcSegment { Point = new(8.2, 20.4), Size = new(5.2, 5.2), IsLargeArc = true });
            hub.Segments.Add(new ArcSegment { Point = new(8.2, 10), Size = new(5.2, 5.2), IsLargeArc = true });
            geometry.Figures.Add(hub);
            figure.Segments.Add(new BezierSegment { Point1 = new(14.5, 20.8), Point2 = new(19, 18.6), Point3 = new(22.3, 13.2) });
            figure.Segments.Add(new BezierSegment { Point1 = new(21, 12.9), Point2 = new(19.7, 12.7), Point3 = new(18.4, 12.8) });
            foreach (var point in new Point[] { new(17.5, 14.6), new(16, 12.9), new(14.3, 14.5), new(13.5, 13), new(11.5, 14.2) })
                figure.Segments.Add(new LineSegment { Point = point });
        }
        geometry.Figures.Add(figure);
        return geometry;
    }
}
