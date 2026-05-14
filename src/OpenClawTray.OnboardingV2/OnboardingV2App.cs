using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Placeholder root component for the V2 onboarding wizard.
///
/// At this stage (preview-project todo) it just renders a centered
/// "OnboardingV2 placeholder" string so we can prove the preview exe
/// can host the FunctionalUI tree end-to-end and capture a screenshot.
///
/// The real shell (custom title bar, navigation host, dot indicator,
/// Back/Next nav bar, page cross-fade) lands in the v2-shell todo.
/// </summary>
public sealed class OnboardingV2App : Component<OnboardingV2State>
{
    public override Element Render()
    {
        return VStack(
            TextBlock($"OnboardingV2 placeholder — route={Props.CurrentRoute}")
                .FontSize(20)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch)
        .Padding(24);
    }
}
