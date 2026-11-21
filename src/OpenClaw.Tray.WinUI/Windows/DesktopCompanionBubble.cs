// Reactor content of the desktop lobster's notification and quick-chat bubble.
// DesktopCompanionWindow hosts it in a windowed Popup so a DesktopAcrylicBackdrop
// can blur the desktop behind it; the mascot stays in the transparent overlay window.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.SetupEngine;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
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
        var (_, invalidate) = UseState(presentation.Revision);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => invalidate(presentation.Revision);
            presentation.Changed += changed;
            invalidate(presentation.Revision);
            return () => presentation.Changed -= changed;
        }), presentation);

        var notification = presentation.Notification;
        var chatOpen = presentation.IsChatOpen;
        var canReply = AppNotificationActionRoutes.TryGetChatSessionKey(notification?.ActionRoute, out _);
        var revision = presentation.Revision;
        var announced = UseRef(0L);
        var dismissLabel = LocalizationHelper.GetString("DesktopCompanion_Dismiss");
        return VStack(0, Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
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
                    HyperlinkButton(LocalizationHelper.GetString("DesktopCompanion_QuickReply"),
                            onClick: props.Reply)
                        .Padding(0).AutomationId("DesktopCompanionReply").IsVisible(canReply),
                    HyperlinkButton(LocalizationHelper.GetString("DesktopCompanion_Notifications"),
                        onClick: props.OpenNotifications)
                    .Padding(0).HAlign(HorizontalAlignment.Left)
                    .AutomationId("DesktopCompanionNotifications"))
                    .Margin(8, 0, 0, 8).Grid(row: 2).IsVisible(!chatOpen),
                new XamlHostElement(() => props.ChatContent)
                    .MaxHeight(360).Grid(row: 3).IsVisible(chatOpen))
                .Set(grid => grid.RowSpacing = chatOpen ? 0 : 8))
            .Background(Theme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("SurfaceStrokeColorFlyoutBrush"))
            .BorderThickness(1).CornerRadius(12).Padding(4))
            .AutomationId("DesktopCompanionBubble")
            .IsVisible(chatOpen || notification is not null)
            .OnMount(props.BubbleMounted);
    }
}
