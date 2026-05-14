using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Welcome page placeholder (v2-shell todo). Real content arrives in the
/// page-welcome todo and is gated by vv-welcome's visual-validation loop.
/// </summary>
public sealed class WelcomePage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        return VStack(16,
            TextBlock("Welcome page (placeholder)").FontSize(28).HAlign(HorizontalAlignment.Center),
            TextBlock("page-welcome todo will replace this with the lobster + Set up locally + Advanced setup design.")
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .Opacity(0.6)
                .TextWrapping()
                .MaxWidth(420),
            Button("Set up locally", () => Props.RequestAdvance())
                .HAlign(HorizontalAlignment.Center)
                .Width(220)
                .Height(40)
        )
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center)
        .Padding(24);
    }
}
