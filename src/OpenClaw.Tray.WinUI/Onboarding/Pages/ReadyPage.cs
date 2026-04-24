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
/// Matches macOS readyPage().
/// </summary>
public sealed class ReadyPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var (launchAtLogin, setLaunchAtLogin) = UseState(false);

        return VStack(16,
            TextBlock("🎉").FontSize(48)
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 4),

            TextBlock(LocalizationHelper.GetString("Onboarding_Ready_Title"))
                .FontSize(28)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock(LocalizationHelper.GetString("Onboarding_Ready_Subtitle"))
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            // Mode-specific info card
            ModeInfoCard(),

            // Feature rows
            Border(
                VStack(4,
                    FeatureActionRow("📋", "Onboarding_Ready_Feature_TrayMenu",
                        "Open menu bar panel", "Access from system tray"),
                    FeatureActionRow("💬", "Onboarding_Ready_Feature_Channels",
                        "Connect WhatsApp, Telegram", "Settings → Channels"),
                    FeatureActionRow("🎤", "Onboarding_Ready_Feature_Voice",
                        "Try Voice Wake", "Wake with your voice"),
                    FeatureActionRow("🎨", "Onboarding_Ready_Feature_Canvas",
                        "Use Canvas", "Visual workspace"),
                    FeatureActionRow("⚡", "Onboarding_Ready_Feature_Skills",
                        "Enable Skills", "Settings → Skills")
                ).Padding(12)
            )
            .CornerRadius(8)
            .Background("#F8F8F8")
            .Margin(0, 8, 0, 0),

            // Launch at Login toggle
            HStack(8,
                ToggleButton("", launchAtLogin, v => setLaunchAtLogin(v))
                    .Width(40),
                TextBlock(LocalizationHelper.GetString("Onboarding_Ready_LaunchAtLogin"))
                    .FontSize(13)
            ).Margin(0, 8, 0, 0)
        )
        .MaxWidth(460)
        .Padding(0, 8, 0, 0);
    }

    private Element ModeInfoCard()
    {
        var message = Props.Mode switch
        {
            ConnectionMode.Later => LocalizationHelper.GetString("Onboarding_Ready_ConfigureLater"),
            ConnectionMode.Remote => LocalizationHelper.GetString("Onboarding_Ready_RemoteInfo"),
            _ => null,
        };

        if (message is null || message.StartsWith("Onboarding_"))
        {
            message = Props.Mode switch
            {
                ConnectionMode.Later => "You can configure your gateway connection anytime from the tray menu → Setup Guide.",
                ConnectionMode.Remote => "Make sure your remote gateway is running and accessible.",
                _ => null,
            };
        }

        if (message is null) return VStack(); // Empty for Local mode

        return Border(
            TextBlock(message).FontSize(12).Opacity(0.8).TextWrapping().Padding(12)
        )
        .CornerRadius(8)
        .Background("#E8F4FD")
        .Margin(0, 8, 0, 0);
    }

    private static Element FeatureActionRow(string icon, string labelKey, string fallback, string subtitle)
    {
        var label = LocalizationHelper.GetString(labelKey);
        if (label == labelKey) label = fallback;

        return HStack(12,
            TextBlock(icon).FontSize(18).Width(24),
            VStack(2,
                TextBlock(label).FontSize(13).FontWeight(new global::Windows.UI.Text.FontWeight(500)),
                TextBlock(subtitle).FontSize(11).Opacity(0.6)
            )
        ).Padding(6, 6, 6, 6);
    }
}
