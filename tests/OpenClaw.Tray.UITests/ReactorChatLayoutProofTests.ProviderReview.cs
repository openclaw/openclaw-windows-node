using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Fact]
    public async Task ProviderReview_PausedWithoutFindingsStaysContinuableAndNeverReopensAfterReconnect()
    {
        await WithChatAsync(1100, async (surface, _, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            var reviewed = snapshot with
            {
                Threads = [snapshot.Threads[0] with
                {
                    ProviderReview = new SessionProviderReviewInfo("review-paused", "run-paused", null,
                        "Continue with the approved plan.", true),
                }],
            };
            await ui.RunOnUIAsync(() => provider.Publish(reviewed));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var card = FindControl<Border>(surface, "ChatProviderReview");
                Assert.Equal("Session paused as a precaution. " +
                    "The provider paused this session. Review this request in web chat before continuing.",
                    AutomationProperties.GetName(card));
                Invoke(FindControl<Button>(card, "ChatProviderReviewOpen"));
            });
            ContentDialog? dialog = null;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => (dialog = OpenDialogs(surface).SingleOrDefault()) is { IsLoaded: true }, "continuation dialog"));
            await ui.RunOnUIAsync(() =>
            {
                var text = FindDescendants<TextBlock>(dialog!).Select(block => block.Text).ToArray();
                Assert.Contains("Continue with the approved plan.", text);
                Assert.Contains("Review this request in web chat before continuing.", text);
                Assert.DoesNotContain("This session can't continue. It remains stopped.", text);
                Assert.DoesNotContain("Provider findings", text);
                // Disconnect tears the open dialog down without raising its Closed event.
                provider.Publish(reviewed with { ConnectionStatus = "Disconnected" });
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => provider.Publish(reviewed));
            await SettleAsync();
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.NotNull(FindControl<Border>(surface, "ChatProviderReview"));
                Assert.Empty(OpenDialogs(surface));
            });
        });
    }

    private static ContentDialog[] OpenDialogs(FrameworkElement surface) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
            .SelectMany(popup => popup.Child is ContentDialog direct
                ? new[] { direct }
                : popup.Child is null ? [] : FindDescendants<ContentDialog>(popup.Child))
            .Where(dialog => dialog.IsLoaded)
            .ToArray();
}
