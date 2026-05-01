using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.FunctionalUI.Factories;
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

        return ScrollView(
            VStack(12,
                TextBlock("🎉").FontSize(40)
                    .HAlign(HorizontalAlignment.Center)
                    .Margin(0, 4, 0, 2),

                TextBlock(LocalizationHelper.GetString("Onboarding_Ready_Title"))
                    .FontSize(22)
                    .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                    .HAlign(HorizontalAlignment.Center),

                TextBlock(LocalizationHelper.GetString("Onboarding_Ready_Subtitle"))
                    .FontSize(14)
                    .Opacity(0.7)
                    .HAlign(HorizontalAlignment.Center)
                    .TextWrapping(),

                // Mode-specific info card
                ModeInfoCard(),

                // Feature rows — different content for Node Mode vs Operator Mode
                Border(
                    VStack(4,
                        Props.Settings.EnableNodeMode ? NodeModeFeatureRows() : OperatorModeFeatureRows()
                    ).Padding(12)
                )
                .CornerRadius(8)
                .Background("#FFFFFF"),

                // Launch at Login toggle
                HStack(8,
                    ToggleSwitch(launchAtLogin, v => setLaunchAtLogin(v)),
                    TextBlock(LocalizationHelper.GetString("Onboarding_Ready_LaunchAtLogin"))
                        .FontSize(13)
                        .VAlign(VerticalAlignment.Center)
                )
            )
            .HAlign(HorizontalAlignment.Center)
            .MaxWidth(460)
            .Padding(0, 8, 0, 0)
        ).HorizontalScrollMode(Microsoft.UI.Xaml.Controls.ScrollMode.Disabled);
    }

    private Element ModeInfoCard()
    {
        if (Props.Settings.EnableNodeMode)
        {
            return Border(
                VStack(8,
                    TextBlock("🔌 Node Mode Active")
                        .FontSize(14)
                        .FontWeight(new global::Windows.UI.Text.FontWeight(600)),
                    TextBlock("This PC will operate as a remote compute node. " +
                        "The gateway can invoke screen capture, camera, and system " +
                        "commands on this machine.")
                        .FontSize(12)
                        .Opacity(0.8)
                        .TextWrapping()
                ).Padding(12)
            )
            .CornerRadius(8)
            .Background("#FFF3E0")
            .Margin(0, 8, 0, 0);
        }

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

    private static Element FeatureActionRow(string icon, string labelKey, string fallback, string subtitleKey, string subtitleFallback)
    {
        var label = LocalizationHelper.GetString(labelKey);
        if (label == labelKey) label = fallback;

        var subtitle = LocalizationHelper.GetString(subtitleKey);
        if (subtitle == subtitleKey) subtitle = subtitleFallback;

        return HStack(12,
            TextBlock(icon).FontSize(18).Width(24),
            VStack(2,
                TextBlock(label).FontSize(13).FontWeight(new global::Windows.UI.Text.FontWeight(500)),
                TextBlock(subtitle).FontSize(11).Opacity(0.6)
            ),
            TextBlock("›")
                .FontSize(16)
                .Opacity(0.3)
                .VAlign(VerticalAlignment.Center)
                .HAlign(HorizontalAlignment.Right)
                .Margin(0, 0, 4, 0)
        ).Padding(6, 8, 6, 8);
    }

    private static Element[] NodeModeFeatureRows() =>
    [
        FeatureActionRow("🖥️", "Onboarding_Ready_Node_ScreenCapture", "Screen Capture",
            "Onboarding_Ready_Node_ScreenCapture_Sub", "Remote screen access"),
        FeatureActionRow("📷", "Onboarding_Ready_Node_Camera", "Camera",
            "Onboarding_Ready_Node_Camera_Sub", "Remote camera access"),
        FeatureActionRow("⚙️", "Onboarding_Ready_Node_SystemCmd", "System Commands",
            "Onboarding_Ready_Node_SystemCmd_Sub", "Remote command execution"),
        FeatureActionRow("🎨", "Onboarding_Ready_Node_Canvas", "Canvas Rendering",
            "Onboarding_Ready_Node_Canvas_Sub", "Visual workspace output"),
        FeatureActionRow("🔔", "Onboarding_Ready_Node_Notify", "Notifications",
            "Onboarding_Ready_Node_Notify_Sub", "System notifications"),
    ];

    private static Element[] OperatorModeFeatureRows() =>
    [
        FeatureActionRow("📋", "Onboarding_Ready_Feature_TrayMenu", "Open menu bar panel",
            "Onboarding_Ready_Feature_TrayMenu_Subtitle", "Access from system tray"),
        FeatureActionRow("💬", "Onboarding_Ready_Feature_Channels", "Connect WhatsApp, Telegram",
            "Onboarding_Ready_Feature_Channels_Subtitle", "Settings → Channels"),
        FeatureActionRow("🎤", "Onboarding_Ready_Feature_Voice", "Try Voice Wake",
            "Onboarding_Ready_Feature_Voice_Subtitle", "Wake with your voice"),
        FeatureActionRow("🎨", "Onboarding_Ready_Feature_Canvas", "Use Canvas",
            "Onboarding_Ready_Feature_Canvas_Subtitle", "Visual workspace"),
        FeatureActionRow("⚡", "Onboarding_Ready_Feature_Skills", "Enable Skills",
            "Onboarding_Ready_Feature_Skills_Subtitle", "Settings → Skills"),
    ];
}
