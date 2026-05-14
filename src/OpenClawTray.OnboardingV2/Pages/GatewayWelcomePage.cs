using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Gateway welcome page (Dialog-2). Conceptually equivalent to the
/// legacy Wizard step — picks the AI provider / gateway settings —
/// while also serving as the landing page after local setup completes.
///
/// Layout:
///   * Title "Configuring gateway" centered (28pt SemiBold).
///   * Big spacer.
///   * Welcome card (rounded 8px) with bold heading + the designer-
///     specified intro paragraph "Your local OpenClaw gateway...".
///     The card also hosts the provider/model picker controls (folded
///     in from the legacy Wizard step) when the host has wired them
///     via <see cref="OnboardingV2State.GatewayUrl"/>.
///   * "Open http://localhost:18789 in browser" hyperlink with
///     external-link glyph, centered below the card.
///
/// The local URL falls back to http://localhost:18789 when the host
/// hasn't yet probed a real one (preview / pre-cutover).
/// </summary>
public sealed class GatewayWelcomePage : Component<OnboardingV2State>
{
    private const string DefaultGatewayUrl = "http://localhost:18789";

    public override Element Render()
    {
        var theme = Props.EffectiveTheme;
        var url = string.IsNullOrWhiteSpace(Props.GatewayUrl) ? DefaultGatewayUrl : Props.GatewayUrl!;

        var card = VStack(16,
            TextBlock(V2Strings.Get("V2_Gateway_CardHeader"))
                .FontSize(20)
                .SemiBold()
                .HAlign(HorizontalAlignment.Left)
                .Set(t => t.Foreground = V2Theme.TextStrong(theme)),

            TextBlock(V2Strings.Get("V2_Gateway_CardBody1"))
                .FontSize(14)
                .TextWrapping()
                .Set(t => t.Foreground = V2Theme.TextSecondary(theme)),

            TextBlock(V2Strings.Get("V2_Gateway_CardBody2"))
                .FontSize(14)
                .TextWrapping()
                .Set(t => t.Foreground = V2Theme.TextSecondary(theme))
        );

        var cardWrap = new BorderElement(card)
            .Background(V2Theme.CardBackground(theme))
            .Padding(28, 28, 28, 28)
            .Set(b => b.CornerRadius = new CornerRadius(8));

        var openLink = Button(
            $"{V2Strings.Get("V2_Gateway_OpenInBrowser").Replace(DefaultGatewayUrl, url)}  \u2197",
            () => { /* page-gateway wiring later */ })
            .HAlign(HorizontalAlignment.Center)
            .Set(b =>
            {
                b.Background = V2Theme.Transparent();
                b.BorderBrush = V2Theme.Transparent();
                b.Foreground = V2Theme.AccentCyan();
                b.FontSize = 14;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Gateway_OpenInBrowser");
                b.Padding = new Thickness(8, 4, 8, 4);
                b.Resources["ButtonBackground"] = V2Theme.Transparent();
                b.Resources["ButtonBackgroundPointerOver"] = V2Theme.AccentCyanGlowHover();
                b.Resources["ButtonBackgroundPressed"] = V2Theme.AccentCyanGlowPressed();
                b.Resources["ButtonForeground"] = V2Theme.AccentCyan();
                b.Resources["ButtonForegroundPointerOver"] = V2Theme.AccentCyanHover();
                b.Resources["ButtonForegroundPressed"] = V2Theme.AccentCyanPressed();
                b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
                b.Resources["ButtonBorderBrushPointerOver"] = V2Theme.Transparent();
                b.Resources["ButtonBorderBrushPressed"] = V2Theme.Transparent();
            });

        var children = new List<Element?>
        {
            new BorderElement(null).Height(40),
            TextBlock(V2Strings.Get("V2_Gateway_Title"))
                .FontSize(28)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.TextStrong(theme)),
            new BorderElement(null).Height(32),
            cardWrap.Margin(48, 0, 48, 0),
        };

        // Embed the legacy WizardPage (provider/model RPC picker) inside
        // the V2 Gateway card area when the host provided a factory. This
        // keeps the existing wizard flow functional while the V2 chrome
        // still owns the page header + welcome card.
        if (Props.GatewayWizardChildFactory is { } childFactory)
        {
            children.Add(new BorderElement(null).Height(16));
            children.Add(childFactory().Margin(48, 0, 48, 0));
        }

        children.Add(new BorderElement(null).Height(20));
        children.Add(openLink);

        return VStack(0, children.ToArray())
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Top);
    }
}
