using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 1: Connection / Gateway Selection.
/// Extends current SetupWizardWindow Step 0 with local/remote/later radio choices.
/// </summary>
public sealed class ConnectionPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var (mode, setMode) = UseState(Props.Mode);

        void SelectMode(ConnectionMode m)
        {
            setMode(m);
            Props.Mode = m;
        }

        return VStack(16,
            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_Title"))
                .FontSize(24)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock(LocalizationHelper.GetString("Onboarding_Connection_Subtitle"))
                .FontSize(14)
                .Opacity(0.7)
                .TextWrapping(),

            // Connection mode choices
            VStack(8,
                ConnectionChoice("Onboarding_Connection_Local", "This PC",
                    mode == ConnectionMode.Local, () => SelectMode(ConnectionMode.Local)),
                ConnectionChoice("Onboarding_Connection_Remote", "Remote gateway",
                    mode == ConnectionMode.Remote, () => SelectMode(ConnectionMode.Remote)),
                ConnectionChoice("Onboarding_Connection_Later", "Configure later",
                    mode == ConnectionMode.Later, () => SelectMode(ConnectionMode.Later))
            ).Margin(0, 16, 0, 0)
        )
        .MaxWidth(460)
        .Padding(0, 32, 0, 0);
    }

    private static Element ConnectionChoice(string labelKey, string fallback, bool selected, Action onSelect)
    {
        var label = LocalizationHelper.GetString(labelKey);
        if (label == labelKey) label = fallback;

        return Button(
            HStack(8,
                Border(null!)
                    .Width(16).Height(16)
                    .CornerRadius(8)
                    .Background(selected ? "#0078D4" : "Transparent"),
                TextBlock(label).FontSize(14)
            ), onSelect)
            .HAlign(HorizontalAlignment.Stretch);
    }
}
