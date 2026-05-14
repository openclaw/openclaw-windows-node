using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// GatewayWelcome page placeholder. Real content arrives in the
/// page-gateway todo (Dialog-2).
/// </summary>
public sealed class GatewayWelcomePage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        return VStack(12,
            TextBlock("Configuring gateway").FontSize(28).HAlign(HorizontalAlignment.Center),
            TextBlock("page-gateway placeholder \u2014 welcome card + Open in browser link lands later.")
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .Opacity(0.6)
                .TextWrapping()
                .MaxWidth(420)
        )
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Top)
        .Padding(24, 80, 24, 24);
    }
}
