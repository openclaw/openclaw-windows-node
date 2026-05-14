using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// AllSet page placeholder. Real content arrives in the page-allset todo
/// (Dialog-4: party popper + optional Node Mode warning + Launch toggle +
/// Finish button).
/// </summary>
public sealed class AllSetPage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        return VStack(12,
            TextBlock("All set!").FontSize(28).HAlign(HorizontalAlignment.Center),
            TextBlock(Props.NodeModeActive
                ? "page-allset placeholder \u2014 (Node Mode active)"
                : "page-allset placeholder")
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
