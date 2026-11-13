// Provider review notice beside the composer, matching the web's chat-provider-review card.
// Usage: Component<ReactorChatProviderReview, ReactorChatProviderReviewProps>(new(review, viewportWidth)).
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatProviderReviewProps(SessionProviderReviewInfo? Review, double ViewportWidth);

/// <summary>
/// Warning card for a session the provider paused or stopped, with read-only findings in a dialog.
/// Continuation stays a separate Gateway action; this card never acknowledges or resumes the run.
/// </summary>
internal sealed class ReactorChatProviderReview : Component<ReactorChatProviderReviewProps>
{
    public override Element Render()
    {
        var (dialogFor, setDialogFor) = UseState<string?>(null);
        // Tearing the dialog down (review cleared, disconnect) does not raise OnClosed; forget the
        // stale request so a returning review cannot reopen the modal without a user action.
        if (dialogFor is not null && Props.Review?.Id != dialogFor)
        {
            setDialogFor(null);
            dialogFor = null;
        }
        if (Props.Review is not { } review)
            return Empty();

        var hasFindings = !string.IsNullOrWhiteSpace(review.Explanation);
        var hasContinuation = review.CanContinue && !string.IsNullOrWhiteSpace(review.ContinuationMessage);
        var title = L(review.CanContinue ? "Chat_Review_PausedTitle" : "Chat_Review_StoppedTitle",
            review.CanContinue ? "Session paused as a precaution" : "Session stopped as a precaution");
        // Permission to continue comes only from CanContinue; missing text never implies a stop.
        var body = review.CanContinue
            ? hasFindings
                ? L("Chat_Review_PausedBody",
                    "The provider couldn't confirm the agent understood your instructions. Review its findings before continuing.")
                : L("Chat_Review_PausedNoFindingsBody",
                    "The provider paused this session. Review this request in web chat before continuing.")
            : hasFindings
                ? L("Chat_Review_StoppedWithFindingsBody",
                    "The provider stopped this session. Review its findings before starting more work.")
                : L("Chat_Review_StoppedBody",
                    "The provider stopped this session without reviewable findings. It can't continue.");
        var canReview = hasFindings || hasContinuation;
        var reviewLabel = L("Chat_Review_Review", "Review findings");
        var gutter = ChatVisuals.Gutter(Props.ViewportWidth);

        var card = Border(
                Grid([GridSize.Px(ChatVisuals.ReviewIconColumn), GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(FluentIconCatalog.StatusWarn).FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                        .FontSize(ChatVisuals.ReviewIconSize)
                        .Foreground(Theme.Ref("SystemFillColorCautionBrush"))
                        .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
                        .AccessibilityView(AccessibilityView.Raw).Grid(column: 0),
                    VStack(2,
                            TextBlock(title).FontSize(ChatVisuals.ToolRowFontSize)
                                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                                .Foreground(Theme.Ref("ChatTextBrush")).TextWrapping(TextWrapping.Wrap),
                            TextBlock(body).FontSize(ChatVisuals.ToolRowFontSize)
                                .Foreground(Theme.Ref("ChatSecondaryTextBrush")).TextWrapping(TextWrapping.Wrap))
                        .VAlign(VerticalAlignment.Center).Margin(ChatVisuals.ReviewGap, 0).Grid(column: 1),
                    canReview
                        ? Button(reviewLabel, () => setDialogFor(review.Id))
                            .VAlign(VerticalAlignment.Center)
                            .AutomationId("ChatProviderReviewOpen").Grid(column: 2)
                        : Empty().Grid(column: 2)))
            .MinHeight(ChatVisuals.ReviewMinHeight)
            .Padding(ChatVisuals.DetailHorizontalInset, ChatVisuals.ReviewVerticalInset)
            .CornerRadius(ChatVisuals.ReviewRadius)
            .Background(Theme.Ref("ChatComposerBrush"))
            .BorderBrush(Theme.Ref("ChatStrokeBrush")).BorderThickness(1)
            .AutomationId("ChatProviderReview")
            .AutomationName($"{title}. {body}")
            .LiveRegion(AutomationLiveSetting.Assertive);

        var dialog = ContentDialog(title, Findings(review), L("Chat_Review_Close", "Close")) with
        {
            IsOpen = canReview && dialogFor == review.Id,
            OnClosed = _ => setDialogFor(null),
        };
        return Grid([GridSize.Star()], [GridSize.Auto], card, dialog)
            .MaxWidth(ChatVisuals.ReadingWidth)
            .Margin(gutter, ChatVisuals.BlockGap, gutter, 0)
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static Element Findings(SessionProviderReviewInfo review)
    {
        var parts = new List<Element>();
        if (review.CanContinue)
        {
            if (!string.IsNullOrWhiteSpace(review.ContinuationMessage))
            {
                parts.Add(Heading(L("Chat_Review_ContinuationTitle", "Continuation message")));
                parts.Add(Border(Body(review.ContinuationMessage!))
                    .BorderBrush(Theme.Ref("ChatStrokeBrush")).BorderThickness(3, 0, 0, 0)
                    .Padding(ChatVisuals.ProseInset, 0, 0, 0));
            }
            parts.Add(Body(L("Chat_Review_Web", "Review this request in web chat before continuing.")));
        }
        else
        {
            parts.Add(Body(L("Chat_Review_NoContinuation", "This session can't continue. It remains stopped.")));
        }
        if (!string.IsNullOrWhiteSpace(review.Explanation))
        {
            parts.Add(Heading(L("Chat_Review_Findings", "Provider findings")));
            parts.Add(Body(review.Explanation));
        }
        return ScrollViewer(VStack(ChatVisuals.BlockGap, parts.ToArray()))
            .MaxHeight(ChatVisuals.ReviewDialogMaxHeight)
            .AutomationId("ChatProviderReviewFindings");
    }

    private static Element Heading(string text) =>
        TextBlock(text).FontSize(ChatVisuals.ToolRowFontSize).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .HeadingLevel(AutomationHeadingLevel.Level3);

    private static Element Body(string text) =>
        TextBlock(text).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true);

    private static string L(string key, string fallback) => ReactorQuestionPanel.L(key, fallback);
}
