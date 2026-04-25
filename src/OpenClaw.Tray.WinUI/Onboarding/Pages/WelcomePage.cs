using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 0: Welcome &amp; Security Notice.
/// Matches macOS welcomePage() — title, subtitle, security warning card.
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

            // Security notice card (orange warning, matches Mac)
            Border(
                VStack(8,
                    HStack(8,
                        TextBlock("⚠️").FontSize(16),
                        TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_SecurityTitle"))
                            .FontSize(14)
                            .FontWeight(new global::Windows.UI.Text.FontWeight(600))
                    ),
                    TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_SecurityBody"))
                        .FontSize(12)
                        .Opacity(0.85)
                        .TextWrapping()
                ).Padding(16)
            )
            .CornerRadius(8)
            .Background("#FFF4E0")
            .Margin(0, 24, 0, 0),

            // Trust explanation
            Border(
                VStack(6,
                    TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_TrustTitle"))
                        .FontSize(13)
                        .FontWeight(new global::Windows.UI.Text.FontWeight(600)),
                    BulletItem("Onboarding_Welcome_Trust_Commands", "Run commands on your computer"),
                    BulletItem("Onboarding_Welcome_Trust_Files", "Read and write files"),
                    BulletItem("Onboarding_Welcome_Trust_Screen", "Capture screenshots")
                ).Padding(16)
            )
            .CornerRadius(8)
            .Background("#F5F5F5")
            .Margin(0, 8, 0, 0)
        )
        .HAlign(HorizontalAlignment.Center)
        .MaxWidth(460)
        .Padding(0, 16, 0, 0);
    }

    private static Element BulletItem(string key, string fallback)
    {
        var text = LocalizationHelper.GetString(key);
        if (text == key) text = fallback;
        return HStack(6,
            TextBlock("•").FontSize(12).Opacity(0.6),
            TextBlock(text).FontSize(12).Opacity(0.7)
        );
    }
}
