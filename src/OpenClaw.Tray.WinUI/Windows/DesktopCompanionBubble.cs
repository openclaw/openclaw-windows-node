// Reactor content of the desktop lobster's notification and quick-chat bubble.
// DesktopCompanionWindow hosts it in a windowed Popup so a DesktopAcrylicBackdrop
// can blur the desktop behind it; the mascot stays in the transparent overlay window.
// The window chooses the bubble's PopoverSurface (backdrop, fill and stroke) from the
// Companion appearance; the bubble only paints it.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Windows;

internal sealed record DesktopCompanionBubbleProps(
    DesktopCompanionPresentation Presentation,
    Action Dismiss,
    Action OpenNotifications,
    Action Reply,
    FrameworkElement ChatContent,
    Action<FrameworkElement> BubbleMounted);

internal sealed class DesktopCompanionBubble : Component<DesktopCompanionBubbleProps>
{
    public override Element Render()
    {
        var props = Props;
        var presentation = props.Presentation;
        var (_, invalidate) = UseState((presentation.Revision, presentation.BubbleSurface));
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => invalidate((presentation.Revision, presentation.BubbleSurface));
            presentation.Changed += changed;
            invalidate((presentation.Revision, presentation.BubbleSurface));
            return () => presentation.Changed -= changed;
        }), presentation);

        var notification = presentation.Notification;
        var chatOpen = presentation.IsChatOpen;
        var surface = presentation.BubbleSurface;
        var canReply = AppNotificationActionRoutes.TryGetChatSessionKey(notification?.ActionRoute, out _);
        var revision = presentation.Revision;
        var announced = UseRef(0L);
        var dismissLabel = LocalizationHelper.GetString("DesktopCompanion_Dismiss");
        var actionBrush = surface.ActionForeground is { } foreground ? new SolidColorBrush(ToColor(foreground)) : null;
        HyperlinkButtonElement ActionLink(string label, Action onClick)
        {
            if (actionBrush is null) return HyperlinkButton(label, onClick: onClick);
            // Hover and press animate the template's presenter. Put collision ink on the text
            // itself so it cannot inherit that animation; keep the same native link and focus.
            return HyperlinkButton(TextBlock(label).Foreground(actionBrush).AccessibilityView(AccessibilityView.Raw),
                onClick: onClick).AutomationName(label);
        }
        var frame = Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(notification?.Title ?? "").SemiBold()
                        .TextWrapping(TextWrapping.Wrap).MaxLines(2).VAlign(VerticalAlignment.Center)
                        .Margin(8, 4, 0, 0)
                        .AutomationId("DesktopCompanionTitle").IsVisible(!chatOpen),
                    Button(TextBlock(FluentIconCatalog.Exit)
                            .FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(12)
                            .AccessibilityView(AccessibilityView.Raw), props.Dismiss)
                        .SubtleButton().Padding(chatOpen ? 6 : 4).MinWidth(28).MinHeight(28)
                        .Margin(0, chatOpen ? 0 : 4, 0, 0)
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
                    .MaxHeight(118).Margin(8, 0, 8, 0)
                    .Set(scroll =>
                    {
                        scroll.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                        scroll.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
                    }).Grid(row: 1).IsVisible(!chatOpen),
                HStack(8,
                    ActionLink(LocalizationHelper.GetString("DesktopCompanion_QuickReply"), props.Reply)
                        .Padding(0).AutomationId("DesktopCompanionReply").IsVisible(canReply),
                    ActionLink(LocalizationHelper.GetString("DesktopCompanion_Notifications"), props.OpenNotifications)
                    .Padding(0).HAlign(HorizontalAlignment.Left)
                    .AutomationId("DesktopCompanionNotifications"))
                    .Margin(8, 0, 0, 8).Grid(row: 2).IsVisible(!chatOpen),
                new XamlHostElement(() => props.ChatContent)
                    .MaxHeight(360).Grid(row: 3).IsVisible(chatOpen))
                .Set(grid => grid.RowSpacing = chatOpen ? 0 : 8))
            .BorderThickness(1).CornerRadius(12).Padding(4);
        // A Gateway theme paints the dashboard's toast colors; the Windows look and High Contrast keep Fluent's.
        // The window recomputes the surface on every appearance and light/dark change, so these are plain brushes.
        var fillBrush = surface.Fill is { } fill ? new SolidColorBrush(ToColor(fill)) : null;
        var strokeBrush = surface.Stroke is { } stroke ? new SolidColorBrush(ToColor(stroke)) : null;
        frame = fillBrush is not null ? frame.Background(fillBrush) : frame.Background(Theme.Ref(surface.FillResource));
        frame = strokeBrush is not null
            ? frame.BorderBrush(strokeBrush)
            : frame.BorderBrush(Theme.Ref(PopoverSurfacePolicy.StrokeResource));
        return VStack(0, frame)
            .AutomationId("DesktopCompanionBubble")
            .IsVisible(chatOpen || notification is not null)
            .OnMount(props.BubbleMounted);
    }

    private static global::Windows.UI.Color ToColor(OpenClaw.Shared.Themes.ThemeColor color) =>
        global::Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
}
