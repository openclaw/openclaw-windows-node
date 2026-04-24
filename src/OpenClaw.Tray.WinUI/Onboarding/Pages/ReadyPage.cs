using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 9: Ready / "All set" summary.
/// Feature links, Settings links, Launch at Login checkbox.
/// </summary>
public sealed class ReadyPage : Component<OnboardingState>
{
    public override Element Render()
    {
        return VStack(16,
            TextBlock(LocalizationHelper.GetString("Onboarding_Ready_Title"))
                .FontSize(28)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock(LocalizationHelper.GetString("Onboarding_Ready_Subtitle"))
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            VStack(8,
                FeatureRow("🎯", "Onboarding_Ready_Feature_TrayMenu", "Access from system tray"),
                FeatureRow("💬", "Onboarding_Ready_Feature_Channels", "Connect WhatsApp, Telegram"),
                FeatureRow("🎤", "Onboarding_Ready_Feature_Voice", "Try Voice Wake"),
                FeatureRow("🎨", "Onboarding_Ready_Feature_Canvas", "Use Canvas"),
                FeatureRow("⚡", "Onboarding_Ready_Feature_Skills", "Enable Skills")
            ).Margin(0, 24, 0, 0)
        )
        .MaxWidth(460)
        .Padding(0, 32, 0, 0);
    }

    private static Element FeatureRow(string icon, string labelKey, string fallback)
    {
        var label = LocalizationHelper.GetString(labelKey);
        if (label == labelKey) label = fallback;

        return HStack(12,
            TextBlock(icon).FontSize(20),
            TextBlock(label).FontSize(14)
        ).Padding(8, 4, 8, 4);
    }
}
