using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 4: Meet your Agent — embedded gateway chat via WebView2 overlay.
/// The actual chat UI is managed by OnboardingWindow's WebView2 overlay
    /// which shows/hides based on the current route. This functional UI component
/// serves as a transparent placeholder that lets the overlay show through.
/// </summary>
public sealed class ChatPage : Component<OnboardingState>
{
    public override Element Render()
    {
        // This page is intentionally minimal — the WebView2 overlay
        // in OnboardingWindow renders the real chat UI on top.
        // We show a brief loading message that's visible only until
        // the WebView2 finishes initializing.
        return VStack(16,
            TextBlock(LocalizationHelper.GetString("Onboarding_Chat_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock(LocalizationHelper.GetString("Onboarding_Chat_Loading"))
                .FontSize(14)
                .Opacity(0.5)
                .HAlign(HorizontalAlignment.Center)
        )
        .MaxWidth(460)
        .VAlign(VerticalAlignment.Center)
        .Padding(0, 16, 0, 0);
    }
}
