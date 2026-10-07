using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Windows;

internal sealed class DesktopCompanionPresentation
{
    public event EventHandler? Changed;
    public AppNotification? Notification { get; private set; }
    public long Revision { get; private set; }

    public void Present(AppNotification? notification)
    {
        Notification = notification;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record DesktopCompanionSurfaceProps(
    DesktopCompanionPresentation Presentation,
    Action Dismiss,
    Action OpenNotifications,
    Action<FrameworkElement> BubbleMounted,
    Action<FrameworkElement> MascotMounted);

internal sealed class DesktopCompanionSurface : Component<DesktopCompanionSurfaceProps>
{
    public override Element Render()
    {
        var props = Props;
        var presentation = props.Presentation;
        var mascot = UseRef<OnboardingMascot?>(null);
        var moodRevision = UseRef(-1L);
        var (_, invalidate) = UseState(presentation.Revision);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => invalidate(presentation.Revision);
            presentation.Changed += changed;
            invalidate(presentation.Revision);
            return () =>
            {
                presentation.Changed -= changed;
                if (mascot.Current is { } control) control.IsAnimationEnabled = false;
                mascot.Current = null;
            };
        }), presentation);

        var notification = presentation.Notification;
        var revision = presentation.Revision;
        var announced = UseRef(0L);
        // Preview.12 does not map Shape.Fill in its theme-binding helper.
        var tailStyle = UseMemo(() => (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Path">
                <Setter Property="Fill" Value="{ThemeResource SolidBackgroundFillColorBaseBrush}"/>
            </Style>
            """), Array.Empty<object>());
        var dismissLabel = LocalizationHelper.GetString("DesktopCompanion_Dismiss");
        var mood = notification?.Severity switch
        {
            null => OnboardingMascotMood.Idle,
            AppNotificationSeverity.Error => OnboardingMascotMood.Sad,
            AppNotificationSeverity.Warning => OnboardingMascotMood.Attentive,
            _ => OnboardingMascotMood.Happy,
        };
        return Grid([GridSize.Star()], [GridSize.Star()],
            VStack(0,
                Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        TextBlock(notification?.Title ?? "").SemiBold()
                            .TextWrapping(TextWrapping.Wrap).MaxLines(2).VAlign(VerticalAlignment.Center)
                            .AutomationId("DesktopCompanionTitle"),
                        Button(TextBlock(FluentIconCatalog.Exit)
                                .FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(12)
                                .AccessibilityView(AccessibilityView.Raw), props.Dismiss)
                            .Padding(6).MinWidth(28).MinHeight(28)
                            .AutomationId("DesktopCompanionDismiss").AutomationName(dismissLabel)
                            .ToolTip(dismissLabel).Grid(column: 1))
                        .Set(grid => grid.ColumnSpacing = 8),
                    ScrollViewer(TextBlock(notification?.Message ?? "")
                            .TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)
                            .LiveRegion(AutomationLiveSetting.Polite)
                            .AutomationId("DesktopCompanionMessage")
                            .Set(text =>
                            {
                                if (notification is null || announced.Current == presentation.Revision) return;
                                var announcedRevision = announced.Current = revision;
                                if (!text.DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (text.IsLoaded && announcedRevision == presentation.Revision)
                                        FrameworkElementAutomationPeer.CreatePeerForElement(text)?
                                            .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                                }))
                                    Logger.Warn("Desktop lobster could not announce its notification.");
                            }))
                        .MaxHeight(118)
                        .Set(scroll =>
                        {
                            scroll.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                            scroll.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
                        }).Grid(row: 1),
                    HyperlinkButton(LocalizationHelper.GetString("DesktopCompanion_Notifications"),
                            onClick: props.OpenNotifications)
                        .Padding(0).HAlign(HorizontalAlignment.Left)
                        .AutomationId("DesktopCompanionNotifications").Grid(row: 2))
                    .Set(grid => grid.RowSpacing = 8))
                    .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
                    .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                    .BorderThickness(1).CornerRadius(12).Padding(14),
                Path2D().Width(20).Height(12).Margin(0, -1, 74, 0).HAlign(HorizontalAlignment.Right)
                    .Set(path =>
                    {
                        path.Style = tailStyle;
                        path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.FillProperty);
                        path.Data ??= new PathGeometry
                        {
                            Figures =
                            {
                                new PathFigure
                                {
                                    StartPoint = new(0, 0), IsClosed = true,
                                    Segments =
                                    {
                                        new LineSegment { Point = new(20, 0) },
                                        new LineSegment { Point = new(10, 12) },
                                    },
                                },
                            },
                        };
                    }))
                .VAlign(VerticalAlignment.Bottom).Margin(4, 4, 4, 174)
                .AutomationId("DesktopCompanionBubble")
                .IsVisible(notification is not null)
                .OnMount(props.BubbleMounted),
            Button(new XamlHostElement(
                    () => mascot.Current = new OnboardingMascot(),
                    element =>
                    {
                        if (moodRevision.Current == revision) return;
                        moodRevision.Current = revision;
                        var control = (OnboardingMascot)element;
                        // Only arrivals restart the entrance, not theme/layout renders.
                        control.Mood = OnboardingMascotMood.Idle;
                        control.Mood = mood;
                    }).AccessibilityView(AccessibilityView.Raw))
                .Width(180).Height(180).Padding(0).BorderThickness(0)
                .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .VerticalContentAlignment(VerticalAlignment.Stretch)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .Resources(resources => resources
                    .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
                .AutomationId("DesktopCompanionMascot")
                .AutomationName(LocalizationHelper.GetString("DesktopCompanion_Name"))
                .OnMount(props.MascotMounted))
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"));
    }
}
