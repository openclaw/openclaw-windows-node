using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using System.Globalization;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ChatContextUsageIndicatorProps(ChatContextUsage? Usage, double ViewportWidth);

/// <summary>
/// Composer context-window indicator, ported from the web composer's <c>ContextNotice</c>:
/// a small progress ring in the composer toolbar that opens a popover with the context
/// window total and the latest run's input/output tokens.
/// </summary>
/// <example>
/// <code>
/// var ring = Component&lt;ChatContextUsageIndicator, ChatContextUsageIndicatorProps&gt;(new(contextUsage, viewportWidth));
/// </code>
/// </example>
/// <remarks>
/// The web popover's cost and plan-quota sections need Gateway data the native client does
/// not receive, so they are omitted rather than approximated.
/// </remarks>
internal sealed class ChatContextUsageIndicator : Component<ChatContextUsageIndicatorProps>
{
    // Web CONTEXT_NOTICE_RATIO: usage at or above this share switches to the warning tone.
    internal const int WarningPercent = 85;
    internal const int CriticalPercent = 95;
    private const double RingSize = 16;
    private const double RingRadius = 6.5;
    private const double PopoverWidth = 300;
    private const double PopoverPadding = 14;

    /// <summary>Returns the toolbar ring, or an empty element when the window size is unknown.</summary>
    public override Element Render()
    {
        var arc = UseRef((Percent: -1, Geometry: (Geometry?)null));
        var track = UseRef<Geometry?>(null);
        if (Props.Usage is not { ContextTokens: > 0 } context)
            return Empty();

        var percent = Math.Clamp(context.Percent ?? 0, 0, 100);
        if (arc.Current.Percent != percent)
            arc.Current = (percent, CreateArc(percent));
        track.Current ??= new EllipseGeometry { Center = new(8, 8), RadiusX = RingRadius, RadiusY = RingRadius };
        var used = ChatUsageFormatter.FormatCompact(context.UsedTokens);
        var limit = ChatUsageFormatter.FormatCompact(context.ContextTokens);
        var summary = string.Format(CultureInfo.CurrentCulture,
            Localized("Chat_ContextUsage_SummaryFormat", "Session context usage: {0} of {1} ({2}%)"),
            used, limit, percent);
        var open = Localized("Chat_ContextUsage_Open", "Open context usage details");

        var trigger = Button(Ring(percent, track.Current, arc.Current.Geometry), () => { })
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Width(32).Height(32).MinWidth(32).MinHeight(32)
            .Padding(0)
            .CornerRadius(4)
            .BorderThickness(0)
            .AutomationId("ChatContextUsage")
            .AutomationName(summary)
            .ToolTip(open);

        return Flyout(trigger, Popover(percent, used, limit, context.InputTokens, context.OutputTokens, Props.ViewportWidth))
            .Set(ChatVisuals.StylePicker)
            .Margin(0, 0, 4, 0)
            .VAlign(VerticalAlignment.Center);
    }

    private static Element Ring(int percent, Geometry track, Geometry? arc)
    {
        var tone = percent >= CriticalPercent ? "ChatContextRingCriticalStyle"
            : percent >= WarningPercent ? "ChatContextRingWarningStyle"
            : "ChatContextRingStyle";
        return Grid([GridSize.Star()], [GridSize.Star()],
                Path2D().Width(RingSize).Height(RingSize).StrokeThickness(2.5).Opacity(0.22)
                    .Set(path =>
                    {
                        path.Data = track;
                        path.Stretch = Stretch.None;
                        path.Style = (Style)Application.Current.Resources["ChatContextRingStyle"];
                        path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
                    }),
                Path2D().Width(RingSize).Height(RingSize)
                    .StrokeThickness(2.5).StrokeStartLineCap(PenLineCap.Round).StrokeEndLineCap(PenLineCap.Round)
                    .Set(path =>
                    {
                        path.Data = arc;
                        path.Stretch = Stretch.None;
                        path.Style = (Style)Application.Current.Resources[tone];
                        path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
                    }))
            .Width(RingSize).Height(RingSize)
            .AccessibilityView(AccessibilityView.Raw)
            .Set(grid => grid.IsHitTestVisible = false);
    }

    // Clockwise arc from twelve o'clock, matching the web SVG dial rotated by -90 degrees.
    private static Geometry? CreateArc(int percent)
    {
        if (percent <= 0)
            return null;
        const double center = RingSize / 2;
        if (percent >= 100)
            return new EllipseGeometry { Center = new(center, center), RadiusX = RingRadius, RadiusY = RingRadius };

        var angle = percent / 100d * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new(center, center - RingRadius), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new(center + RingRadius * Math.Sin(angle), center - RingRadius * Math.Cos(angle)),
            Size = new(RingRadius, RingRadius),
            IsLargeArc = percent > 50,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Element Popover(int percent, string used, string limit, long? inputTokens, long? outputTokens,
        double viewportWidth)
    {
        var barFill = percent >= CriticalPercent ? "SystemFillColorCriticalBrush"
            : percent >= WarningPercent ? "SystemFillColorCautionBrush"
            : "AccentFillColorDefaultBrush";
        var popoverWidth = Math.Clamp(viewportWidth - 24, 80, PopoverWidth);
        var trackWidth = popoverWidth - 2 * PopoverPadding;
        var track = Border(
                Border(Empty())
                    .Width(Math.Round(trackWidth * percent / 100d))
                    .CornerRadius(2.5)
                    .HAlign(HorizontalAlignment.Left)
                    .Background(Theme.Ref(barFill)))
            .Height(5)
            .CornerRadius(2.5)
            .Margin(0, 8, 0, 10)
            .Background(Theme.Ref("ChatStrokeBrush"))
            .AccessibilityView(AccessibilityView.Raw);

        return VStack(0,
                Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                    SectionLabel(Localized("Chat_ContextUsage_ContextWindow", "Context window"))
                        .VAlign(VerticalAlignment.Center).Grid(column: 0),
                    TextBlock($"{used} / {limit} · {percent}%")
                        .FontSize(13).FontWeight(FontWeights.SemiBold)
                        .TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Right)
                        .Foreground(Theme.Ref("ChatTextBrush"))
                        .Margin(8, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center)
                        .AutomationId("ChatContextUsageValue")
                        .Grid(column: 1)),
                track,
                Border(Empty()).Height(1).Background(Theme.Ref("ChatStrokeBrush")),
                SectionLabel(Localized("Chat_ContextUsage_LatestRunTokens", "Latest run tokens"))
                    .Margin(0, 14, 0, 0),
                Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                        Stat(Localized("Chat_ContextUsage_Input", "Input"), inputTokens).Margin(0, 0, 4, 0).Grid(column: 0),
                        Stat(Localized("Chat_ContextUsage_Output", "Output"), outputTokens).Grid(column: 1))
                    .Margin(0, 8, 0, 0)
                    .AutomationId("ChatContextUsageLatestRun"))
            .Padding(PopoverPadding)
            .Width(popoverWidth)
            .AutomationId("ChatContextUsagePopup")
            .AutomationName(Localized("Chat_ContextUsage_Title", "Context usage details"));
    }

    private static TextBlockElement SectionLabel(string text) =>
        TextBlock(text.ToUpper(CultureInfo.CurrentCulture))
            .FontSize(11).FontWeight(FontWeights.Bold)
            .TextWrapping(TextWrapping.Wrap)
            .Foreground(Theme.Ref("ChatSecondaryTextBrush"))
            .Set(label => label.CharacterSpacing = 80);

    private static Element Stat(string label, long? value) =>
        TextBlock(string.Empty).FontSize(12).TextWrapping(TextWrapping.Wrap)
            .Foreground(Theme.Ref("ChatTextBrush"))
            .Set(text =>
            {
                text.Inlines.Clear();
                text.Inlines.Add(new Run { Text = label + " " });
                text.Inlines.Add(new Run
                {
                    Text = value is >= 0 ? ChatUsageFormatter.FormatCompact(value.Value) : "—",
                    FontWeight = FontWeights.SemiBold,
                });
            });

    private static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}
