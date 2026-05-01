using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using static OpenClawTray.FunctionalUI.Factories;
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
        return VStack(10,
            TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_Subtitle"))
                .FontSize(14)
                .Opacity(0.6)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_GetConnected"))
                .FontSize(13)
                .Opacity(0.5)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .Margin(0, 4, 0, 0),

            // Combined security notice + trust card
            Border(
                VStack(8,
                    HStack(6,
                        TextBlock("⚠️").FontSize(14),
                        TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_SecurityTitle"))
                            .FontSize(13)
                            .FontWeight(new global::Windows.UI.Text.FontWeight(600))
                    ),
                    TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_SecurityBody"))
                        .FontSize(12)
                        .Opacity(0.85)
                        .TextWrapping(),
                    TextBlock(LocalizationHelper.GetString("Onboarding_Welcome_TrustTitle"))
                        .FontSize(13)
                        .FontWeight(new global::Windows.UI.Text.FontWeight(600))
                        .Margin(0, 4, 0, 0),
                    BulletItem("Onboarding_Welcome_Trust_Commands", "Run commands on your computer"),
                    BulletItem("Onboarding_Welcome_Trust_Files", "Read and write files"),
                    BulletItem("Onboarding_Welcome_Trust_Screen", "Capture screenshots")
                ).Padding(14)
            )
            .CornerRadius(8)
            .Background("#FFF4E0")
            .Margin(0, 12, 0, 0)
        )
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center)
        .MaxWidth(460)
        .Padding(0, 8, 0, 0);
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
