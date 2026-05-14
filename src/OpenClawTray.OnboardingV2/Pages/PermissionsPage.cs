using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Permissions page placeholder. Real content arrives in the
/// page-permissions todo (Dialog-5, Open Settings variant).
/// </summary>
public sealed class PermissionsPage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        return VStack(12,
            TextBlock("Grant permissions").FontSize(28).HAlign(HorizontalAlignment.Center),
            TextBlock("page-permissions placeholder \u2014 five row cards with Open Settings + Refresh status lands later.")
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
