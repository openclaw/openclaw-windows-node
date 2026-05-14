using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// LocalSetupProgress page placeholder. Real content arrives in the
/// page-progress todo (Dialog-1 + Dialog-6 frames).
/// </summary>
public sealed class LocalSetupProgressPage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        return VStack(12,
            TextBlock("Setting up locally").FontSize(28).HAlign(HorizontalAlignment.Center),
            TextBlock("page-progress placeholder \u2014 seven-row checklist + inline error card lands later.")
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
