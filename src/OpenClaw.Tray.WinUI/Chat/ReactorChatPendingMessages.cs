// Renders the selected conversation's pending queue without adding messages to history.
// Usage: ReactorChatPendingMessages.Render(timelineContext, viewportWidth, viewportHeight).
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>Compact, bounded queue presentation above the composer. Dispatch remains provider-owned.</summary>
internal static class ReactorChatPendingMessages
{
    private const double IconSize = 14;
    private const double ActionSize = 32;
    private const double TextSize = 13;
    private const double TextLineHeight = 18;
    private const double IconStrokeThickness = 1.75;
    // OpenClaw's queue waiting and trash paths. Pinned sources and licenses are in THIRD_PARTY_NOTICES.md.
    private const string WaitingIconData = "M16 5H3 M16 12H3 M9 19H3 M16 16L13 19L16 22 M21 5V17A2 2 0 0 1 19 19H13";
    private const string TrashIconData = "M3 6H21 M19 6V20C19 21 18 22 17 22H7C6 22 5 21 5 20V6 M8 6V4C8 3 9 2 10 2H14C15 2 16 3 16 4V6 M10 11V17 M14 11V17";
    /// <summary>Renders ordered pending rows, or no panel when the selected queue is empty.</summary>
    public static Element Render(ChatTimelinePresentationContext context, double width, double height)
    {
        if (context.QueuedMessages is not { Count: > 0 } messages)
            return Empty();

        return Border(
                ScrollViewer(VStack(0, messages.Select((message, index) => RenderRow(context, message, index, width)
                    .WithKey($"{context.SessionId}|{context.TimelineGeneration}|queued:{message.Id}")).ToArray()))
                    .MaxHeight(Math.Min(ChatVisuals.ActivityDetailsMaxHeight, height / 3))
                    .Set(scroll =>
                    {
                        scroll.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                        scroll.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
                        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    }))
            .Background(Theme.Ref("ChatCardBrush"))
            .BorderBrush(Theme.Ref("ChatStrokeBrush")).BorderThickness(1)
            .CornerRadius(ChatVisuals.ComposerRadius)
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(ChatVisuals.Gutter(width), 0, ChatVisuals.Gutter(width), ChatVisuals.CompactGap)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("ChatPendingMessages");
    }

    private static Element RenderRow(ChatTimelinePresentationContext context, ChatQueuedMessage message, int index,
        double width)
    {
        var compact = width <= ChatVisuals.CompactEffortBreakpoint;
        var gap = compact ? ChatVisuals.QueueCompactGap : ChatVisuals.BlockGap;
        var avatarSize = compact ? ChatVisuals.QueueCompactAvatarSize : ChatVisuals.QueueAvatarSize;
        var removable = message.SendState != ChatQueuedMessageSendState.Sending &&
            context.OnCancelQueuedMessage is not null;
        var (text, attachments) = ReactorChatTimeline.ParseAttachments(message.Text);
        var accessibleText = ReactorChatTimeline.BuildAccessibleUserText(text, attachments);
        var failed = message.SendState == ChatQueuedMessageSendState.Failed;
        var status = failed
            ? ChatMarkdownPresentation.Localized("Chat_Composer_QueuedMessageFailed", "Failed")
            : ChatMarkdownPresentation.Localized("Chat_Timeline_Pending", "Pending");
        var removeLabel = ChatMarkdownPresentation.Localized(
            failed ? "Chat_Composer_QueuedMessageRemoveFailed" : "Chat_Composer_QueuedMessageCancel",
            failed ? "Remove failed queued message" : "Cancel queued message");
        var content = attachments.Select(ReactorChatTimeline.BuildAttachment).ToList();
        if (text.Length > 0)
            content.Insert(0, TextBlock(text).FontSize(TextSize).LineHeight(TextLineHeight)
                .FontWeight(Microsoft.UI.Text.FontWeights.Normal)
                .Foreground(Theme.Ref("ChatTextBrush")).TextWrapping(TextWrapping.Wrap)
                .IsTextSelectionEnabled(true));
        if (failed)
            content.Add(TextBlock(string.IsNullOrWhiteSpace(message.ErrorText) ? status : message.ErrorText)
                .FontSize(12).TextWrapping(TextWrapping.Wrap)
                .Foreground(Theme.Ref("SystemFillColorCriticalBrush")));

        return Border(
                Grid([GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    OutlineIcon(WaitingIconData, "ChatQueueIconStyle", $"ChatQueueWaitingIcon_{message.Id}")
                        .VAlign(VerticalAlignment.Center).Margin(0, 0, gap, 0).Grid(column: 0),
                    PersonPicture().DisplayName(context.UserDisplayName)
                        .Set(picture => picture.ProfilePicture = context.UserProfilePicture)
                        .Size(avatarSize, avatarSize).IsHitTestVisible(false)
                        .AccessibilityView(AccessibilityView.Raw)
                        .AutomationId($"ChatQueueAvatar_{message.Id}")
                        .VAlign(VerticalAlignment.Center).Margin(0, 0, gap, 0).Grid(column: 1),
                    VStack(ChatVisuals.CompactGap, content.ToArray())
                        .VAlign(VerticalAlignment.Center).Grid(column: 2),
                    Button(OutlineIcon(TrashIconData,
                                removable ? "ChatQueueIconStyle" : "ChatQueueDisabledIconStyle",
                                $"ChatQueueRemoveIcon_{message.Id}"),
                            () => context.OnCancelQueuedMessage?.Invoke(message.Id))
                        .SubtleButton().MinWidth(ActionSize).MinHeight(ActionSize).Padding(ChatVisuals.CompactGap)
                        .IsEnabled(removable)
                        .ToolTip(removeLabel).AutomationName($"{removeLabel}: {accessibleText}")
                        .AutomationId($"{(failed ? "ChatQueuedMessageRemoveFailed" : "ChatQueuedMessageCancel")}_{message.Id}")
                        .VAlign(VerticalAlignment.Center).Margin(gap, 0, 0, 0).Grid(column: 3)))
            .Padding(ChatVisuals.QueueLeadingInset, ChatVisuals.QueueVerticalInset,
                ChatVisuals.QueueTrailingInset, ChatVisuals.QueueVerticalInset)
            .BorderBrush(Theme.Ref("ChatStrokeBrush")).BorderThickness(0, index == 0 ? 0 : 1, 0, 0)
            .AutomationId($"ChatQueuedMessage_{message.Id}")
            .AutomationName(accessibleText).ItemStatus(status).LiveRegion(AutomationLiveSetting.Polite);
    }

    private static Element OutlineIcon(string data, string style, string automationId) =>
        Viewbox(Path2D()
                .StrokeThickness(IconStrokeThickness)
                .StrokeStartLineCap(PenLineCap.Round).StrokeEndLineCap(PenLineCap.Round)
                .StrokeLineJoin(PenLineJoin.Round)
                .Width(24).Height(24).Set(path =>
                {
                    var parsed = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                        $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{data}\" />");
                    // Geometry cannot belong to both the parsed and reconciled native Paths.
                    var geometry = parsed.Data;
                    parsed.Data = null;
                    path.Data = geometry;
                    path.Stretch = Stretch.None;
                    path.Style = (Style)Application.Current.Resources[style];
                    path.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
                }))
            .Stretch(Stretch.Uniform).Width(IconSize).Height(IconSize)
            .AutomationId(automationId).AccessibilityView(AccessibilityView.Raw);
}
