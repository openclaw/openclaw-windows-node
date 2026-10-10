using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatEffortGaugeProps(int SelectedIndex, int StopCount, bool IsOff);

/// <summary>
/// Native rendering of OpenClaw 2026.9.4's inline effort SVG.
/// See THIRD_PARTY_NOTICES.md for the pinned source and license notices.
/// </summary>
internal sealed class ChatEffortGauge : Component<ChatEffortGaugeProps>
{
    internal const string DialData = "M3.34 17a10 10 0 1 1 17.32 0";
    internal const string NeedleData = "M12 12V6";

    public override Element Render()
    {
        var fraction = Props.IsOff || Props.SelectedIndex < 0 ? 0
            : Props.StopCount > 1 ? (double)Props.SelectedIndex / (Props.StopCount - 1) : 1;
        var angle = -120 + Math.Clamp(fraction, 0, 1) * 240;

        return Viewbox(Grid([GridSize.Star()], [GridSize.Star()],
                Path2D().StrokeThickness(2).StrokeStartLineCap(PenLineCap.Round)
                    .StrokeEndLineCap(PenLineCap.Round).StrokeLineJoin(PenLineJoin.Round)
                    .Width(24).Height(24).Opacity(0.55)
                    .Set(path =>
                    {
                        path.Data ??= CreateDial();
                        path.Stretch = Stretch.None;
                        path.Style = (Style)Application.Current.Resources["ChatEffortDialStyle"];
                        path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
                    })
                    .AutomationId("ChatEffortGaugeDial"),
                Path2D().StrokeThickness(2).StrokeStartLineCap(PenLineCap.Round)
                    .StrokeEndLineCap(PenLineCap.Round).StrokeLineJoin(PenLineJoin.Round)
                    .Width(24).Height(24)
                    .Set(path =>
                    {
                        path.Data ??= CreateNeedle();
                        path.Stretch = Stretch.None;
                        path.Style = (Style)Application.Current.Resources["ChatEffortStrokeStyle"];
                        path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
                        path.RenderTransform = new RotateTransform { Angle = angle, CenterX = 12, CenterY = 12 };
                    })
                    .AutomationId("ChatEffortGaugeNeedle"),
                Ellipse().Width(2).Height(2).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
                    .Set(ellipse =>
                    {
                        ellipse.Style = (Style)Application.Current.Resources["ChatEffortHubStyle"];
                        ellipse.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.FillProperty);
                    }))
                .Width(24).Height(24))
            .Stretch(Stretch.Uniform).Width(20).Height(20)
            .VAlign(VerticalAlignment.Center)
            .Opacity(Props.IsOff ? 0.5 : 1)
            .AutomationId("ChatEffortGauge").AccessibilityView(AccessibilityView.Raw)
            .Set(viewbox => viewbox.IsHitTestVisible = false);
    }

    private static Geometry CreateDial()
    {
        // Geometry is owned by its native Path, not shared across reconciled controls.
        // This is the absolute-coordinate equivalent of DialData's relative arc.
        var figure = new PathFigure { StartPoint = new(3.34, 17), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new(3.34 + 17.32, 17),
            Size = new(10, 10),
            IsLargeArc = true,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Geometry CreateNeedle()
    {
        var figure = new PathFigure { StartPoint = new(12, 12), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = new(12, 6) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
