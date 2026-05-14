using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Gateway welcome page (Dialog-2).
///
/// Layout:
///   * Title "Configuring gateway" centered (28pt SemiBold).
///   * Big spacer.
///   * Welcome card (#2C2C2C bg, rounded 8px) with bold heading,
///     body paragraph, blank line, second body paragraph.
///   * "Open http://localhost:18789 in browser" hyperlink with
///     external-link glyph, centered below the card.
///
/// The local URL is hard-coded to http://localhost:18789 — the real
/// gateway service binds there in dev. Real wiring (Launcher.LaunchUriAsync)
/// gets attached at cutover; in the preview the link is a no-op.
/// </summary>
public sealed class GatewayWelcomePage : Component<OnboardingV2State>
{
    private const string GatewayUrl = "http://localhost:18789";

    public override Element Render()
    {
        var card = VStack(16,
            TextBlock("Welcome to OpenClaw gateway")
                .FontSize(20)
                .SemiBold()
                .HAlign(HorizontalAlignment.Left),

            TextBlock($"Your local OpenClaw gateway is running at {GatewayUrl} \u2014 visit it to add your first AI provider and configure your agent.")
                .FontSize(14)
                .TextWrapping()
                .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xD8, 0xD8, 0xD8))),

            TextBlock("All requests are processed on this PC. Your data stays local.")
                .FontSize(14)
                .TextWrapping()
                .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xD8, 0xD8, 0xD8)))
        );

        var cardWrap = new BorderElement(card)
            .Background("#2C2C2C")
            .Padding(28, 28, 28, 28)
            .Set(b => b.CornerRadius = new CornerRadius(8));

        var openLink = Button(
            $"Open {GatewayUrl} in browser  \u2197",
            () => { /* page-gateway wiring later */ })
            .HAlign(HorizontalAlignment.Center)
            .Set(b =>
            {
                b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x60, 0xC8, 0xF8));
                b.FontSize = 14;
                b.UseSystemFocusVisuals = false;
                b.Padding = new Thickness(8, 4, 8, 4);
                b.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(60, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x52, 0xB0, 0xDA));
                b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x46, 0x99, 0xBC));
                b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            });

        return VStack(0,
            new BorderElement(null).Height(40),
            TextBlock("Configuring gateway")
                .FontSize(28)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center),
            new BorderElement(null).Height(48),
            cardWrap.Margin(48, 0, 48, 0),
            new BorderElement(null).Height(28),
            openLink
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Top);
    }
}

