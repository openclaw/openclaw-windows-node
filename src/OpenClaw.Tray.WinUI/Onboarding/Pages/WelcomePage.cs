using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 0: Welcome &amp; Security Notice.
/// Matches macOS welcomePage() — title, subtitle, orange security warning card.
/// </summary>
public sealed class WelcomePage : Component
{
    public override Element Render()
    {
        return VStack(16,
            TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_Title"))
                .FontSize(28)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_Subtitle"))
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            // Security notice card
            Border(
                VStack(8,
                    TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_SecurityTitle"))
                        .FontSize(14)
                        .FontWeight(new global::Windows.UI.Text.FontWeight(600)),
                    TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_SecurityBody"))
                        .FontSize(12)
                        .Opacity(0.8)
                        .TextWrapping()
                ).Padding(16)
            )
            .CornerRadius(8)
            .Background("#FFF4E0")
            .Margin(0, 24, 0, 0)
        )
        .HAlign(HorizontalAlignment.Center)
        .MaxWidth(460)
        .Padding(0, 32, 0, 0);
    }
}
