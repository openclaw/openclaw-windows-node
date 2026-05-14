using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Welcome page (Dialog.png) — first impression of the V2 setup flow.
///
/// Layout (top → bottom, all centered horizontally):
///
///   * spacer ~80px
///   * Lobster hero  (224×224 image)
///   * "Get started with OpenClaw"  (28pt SemiBold)
///   * Body copy (14pt 70% white, max-width ~480px, centered, wraps)
///   * spacer that grows to push the bottom cluster down
///   * Info card (rounded, slightly lighter bg, blue (i) + text)
///   * "Set up locally" primary button (accent #60C8F8, full-width-ish)
///   * "Advanced setup" hyperlink (accent text-only button)
///
/// The page has no nav bar (OnboardingV2App hides chrome on the Welcome
/// route — the design's first impression keeps focus on the two CTAs).
/// </summary>
public sealed class WelcomePage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        var infoCard = Grid(
            new[] { "auto", "*" },
            new[] { "auto" },
            // Blue info circle with white "i" — built from a Border + TextBlock
            // so we don't depend on a separate icon font for this badge.
            new BorderElement(
                TextBlock("i")
                    .FontSize(13)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White))
            )
            .Background("#60C8F8")
            .Width(20)
            .Height(20)
            .VAlign(VerticalAlignment.Top)
            .Margin(0, 2, 12, 0)
            .Set(b => b.CornerRadius = new CornerRadius(10))
            .Grid(row: 0, column: 0),

            TextBlock(V2Strings.Get("V2_Welcome_InfoCard"))
                .FontSize(13)
                .TextWrapping()
                .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xD0, 0xD0, 0xD0)))
                .Grid(row: 0, column: 1)
        );

        var infoCardWrap = new BorderElement(infoCard)
            .Background("#2C2C2C")
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8))
            .WithEntranceFadeIn(durationMs: 360, delayMs: 200);

        var accentButton = Button(V2Strings.Get("V2_Welcome_PrimaryButton"), () => Props.RequestAdvance())
            .HAlign(HorizontalAlignment.Stretch)
            .Height(44)
            .Set(b =>
            {
                b.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                b.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                b.FontSize = 14;
                b.HorizontalContentAlignment = HorizontalAlignment.Center;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Welcome_SetUpLocally");
                b.BorderThickness = new Thickness(0);
                b.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x52, 0xB0, 0xDA));
                b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x46, 0x99, 0xBC));
                b.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 0));
                b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderThemeThickness"] = new Thickness(0);
            });

        var advancedLink = Button(V2Strings.Get("V2_Welcome_AdvancedLink"), () => { /* page-welcome wiring lands later */ })
            .HAlign(HorizontalAlignment.Center)
            .Set(b =>
            {
                b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x60, 0xC8, 0xF8));
                b.Padding = new Thickness(8, 4, 8, 4);
                b.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(60, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x60, 0xC8, 0xF8));
                b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x52, 0xB0, 0xDA));
                b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x46, 0x99, 0xBC));
            });

        // Outer Grid: rows are [hero spacer | hero/title/body | flex spacer | bottom cluster]
        // pushes the hero into the upper third and the CTAs into the lower third,
        // matching the designer composition.
        return Grid(
            new[] { "*" },
            new[] { "auto", "auto", "*", "auto" },
            // Top spacer (Border with min height) pushes hero down from the title bar.
            new BorderElement(null).Height(28).Grid(row: 0, column: 0),

            VStack(16,
                Image("ms-appx:///Assets/Setup/Lobster.png")
                    .Width(170)
                    .Height(170)
                    .HAlign(HorizontalAlignment.Center)
                    .WithBreathe(maxScale: 1.025, durationMs: 4200),
                TextBlock(V2Strings.Get("V2_Welcome_Title"))
                    .FontSize(28)
                    .SemiBold()
                    .HAlign(HorizontalAlignment.Center)
                    .Margin(0, 12, 0, 0),
                TextBlock(V2Strings.Get("V2_Welcome_Body"))
                    .FontSize(14)
                    .HAlign(HorizontalAlignment.Center)
                    .TextWrapping()
                    .MaxWidth(440)
                    .Set(t =>
                    {
                        t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xC0, 0xC0, 0xC0));
                        t.TextAlignment = TextAlignment.Center;
                    })
            )
            .HAlign(HorizontalAlignment.Center)
            .Grid(row: 1, column: 0),

            // Flex spacer fills the middle so the bottom cluster pins to the bottom.
            new BorderElement(null).Grid(row: 2, column: 0),

            VStack(16,
                infoCardWrap,
                accentButton,
                advancedLink
            )
            .Margin(40, 0, 40, 40)
            .Grid(row: 3, column: 0)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch);
    }
}
