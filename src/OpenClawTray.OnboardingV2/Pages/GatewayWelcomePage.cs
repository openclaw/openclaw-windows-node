using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Gateway welcome page (Dialog-2). Conceptually equivalent to the
/// gateway wizard step — picks the AI provider / gateway settings.
///
/// Status: the designer's "Welcome to OpenClaw gateway" intro card has
/// been temporarily removed so the embedded gateway wizard gets the full
/// vertical space and its own nav buttons are reachable. The intro card will
/// return in a follow-up PR once the wizard itself is redesigned to share the
/// V2 card chrome.
///
/// Until then, this page is just the V2 title bar (from
/// <see cref="OnboardingV2App"/>) plus the host-owned wizard component
/// rendered via <see cref="OnboardingV2State.GatewayWizardChildFactory"/>.
/// </summary>
public sealed class GatewayWelcomePage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        var children = new List<Element?>
        {
            new BorderElement(null).Height(24),
        };

        // Embed the gateway wizard (provider/model RPC picker). The host
        // populates GatewayWizardChildFactory at mount time. When it is
        // present the wizard renders its own dynamic step-driven heading;
        // we deliberately do NOT add the static "Configuring gateway"
        // heading here to avoid showing two titles for the same UI.
        // In the standalone preview (no host) we render only the heading.
        if (Props.GatewayWizardChildFactory is { } childFactory)
        {
            children.Add(childFactory().Margin(0, 0, 0, 0));
        }
        else
        {
            var theme = Props.EffectiveTheme;
            var heading = TextBlock(V2Strings.Get("V2_Gateway_Title"))
                .FontSize(28)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.TextStrong(theme));
            children.Add(heading);
            children.Add(new BorderElement(null).Height(20));
        }

        return VStack(0, children.ToArray())
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Top);
    }
}
