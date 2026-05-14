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
///
/// Colours come from <see cref="V2Theme"/> keyed on <see cref="OnboardingV2State.EffectiveTheme"/>
/// so light + dark + system modes all render correctly.
/// </summary>
public sealed class WelcomePage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        var theme = Props.EffectiveTheme;
        var hasExisting = Props.ExistingConfig is { HasAny: true };
        var (confirmingReplace, setConfirmingReplace) = UseState(hasExisting && !Props.ReplaceExistingConfigurationConfirmed);

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
                    .Set(t => t.Foreground = V2Theme.White())
            )
            .Background(V2Theme.AccentCyan())
            .Width(20)
            .Height(20)
            .VAlign(VerticalAlignment.Top)
            .Margin(0, 2, 12, 0)
            .Set(b => b.CornerRadius = new CornerRadius(10))
            .Grid(row: 0, column: 0),

            TextBlock(V2Strings.Get("V2_Welcome_InfoCard"))
                .FontSize(13)
                .TextWrapping()
                .Set(t => t.Foreground = V2Theme.TextSecondary(theme))
                .Grid(row: 0, column: 1)
        );

        var infoCardWrap = new BorderElement(infoCard)
            .Background(V2Theme.CardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8))
            .WithEntranceFadeIn(durationMs: 360, delayMs: 200);

        Element BuildAccentButton(string label, Action onClick, string automationId)
        {
            return Button(label, onClick)
                .HAlign(HorizontalAlignment.Stretch)
                .Height(44)
                .Set(b =>
                {
                    b.Foreground = V2Theme.OnAccentText();
                    b.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    b.FontSize = 14;
                    b.HorizontalContentAlignment = HorizontalAlignment.Center;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, automationId);
                    b.BorderThickness = new Thickness(0);
                    b.Resources["ButtonBackground"] = V2Theme.AccentCyan();
                    b.Resources["ButtonBackgroundPointerOver"] = V2Theme.AccentCyanHover();
                    b.Resources["ButtonBackgroundPressed"] = V2Theme.AccentCyanPressed();
                    b.Resources["ButtonForeground"] = V2Theme.OnAccentText();
                    b.Resources["ButtonForegroundPointerOver"] = V2Theme.OnAccentText();
                    b.Resources["ButtonForegroundPressed"] = V2Theme.OnAccentText();
                    b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
                    b.Resources["ButtonBorderBrushPointerOver"] = V2Theme.Transparent();
                    b.Resources["ButtonBorderBrushPressed"] = V2Theme.Transparent();
                    b.Resources["ButtonBorderThemeThickness"] = new Thickness(0);
                });
        }

        Element BuildLinkButton(string label, Action onClick, string automationId)
        {
            return Button(label, onClick)
                .HAlign(HorizontalAlignment.Center)
                .Set(b =>
                {
                    b.Background = V2Theme.Transparent();
                    b.BorderBrush = V2Theme.Transparent();
                    b.Foreground = V2Theme.AccentCyan();
                    b.Padding = new Thickness(8, 4, 8, 4);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, automationId);
                    b.Resources["ButtonBackground"] = V2Theme.Transparent();
                    b.Resources["ButtonBackgroundPointerOver"] = V2Theme.AccentCyanGlowHover();
                    b.Resources["ButtonBackgroundPressed"] = V2Theme.AccentCyanGlowPressed();
                    b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
                    b.Resources["ButtonBorderBrushPointerOver"] = V2Theme.Transparent();
                    b.Resources["ButtonBorderBrushPressed"] = V2Theme.Transparent();
                    b.Resources["ButtonForeground"] = V2Theme.AccentCyan();
                    b.Resources["ButtonForegroundPointerOver"] = V2Theme.AccentCyanHover();
                    b.Resources["ButtonForegroundPressed"] = V2Theme.AccentCyanPressed();
                });
        }

        Element bottomCluster;
        if (confirmingReplace)
        {
            // Existing-config warn-and-confirm: matches the legacy
            // SetupWarningPage UX but rendered with V2 theme + spacing.
            var lost = new List<string>();
            if (Props.ExistingConfig is { } ec)
            {
                if (ec.HasToken) lost.Add("gateway token");
                if (ec.HasOperatorDeviceToken || ec.HasNodeDeviceToken) lost.Add("device pairing");
                if (ec.HasNonDefaultGatewayUrl) lost.Add("current gateway URL");
                if (ec.HasBootstrapToken) lost.Add("bootstrap token");
            }
            var body = V2Strings.Get("V2_Welcome_Replace_Body");
            if (lost.Count > 0)
            {
                body += $" This will overwrite: {string.Join(", ", lost)}.";
            }

            var warnCard = new BorderElement(
                VStack(8,
                    TextBlock(V2Strings.Get("V2_Welcome_Replace_Heading"))
                        .FontSize(16)
                        .SemiBold()
                        .HAlign(HorizontalAlignment.Center)
                        .TextWrapping()
                        .Set(t => t.Foreground = V2Theme.WarningCardForeground(theme)),
                    TextBlock(body)
                        .FontSize(13)
                        .HAlign(HorizontalAlignment.Center)
                        .TextWrapping()
                        .Set(t =>
                        {
                            t.Foreground = V2Theme.WarningCardForeground(theme);
                            t.TextAlignment = TextAlignment.Center;
                        })
                )
            )
            .Background(V2Theme.WarningCardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8));

            void ConfirmReplace()
            {
                Props.ReplaceExistingConfigurationConfirmed = true;
                setConfirmingReplace(false);
                Props.RequestAdvance();
            }

            void KeepSetup()
            {
                // "Keep my setup" — the user has existing configuration and wants
                // to keep it. Dismiss the V2 wizard entirely (which closes the
                // onboarding window without firing Finished or running the
                // completion pipeline) so existing settings and gateway
                // connection are preserved untouched. Mirrors the legacy
                // SetupWarningPage CancelReplace handler post-PR-#340.
                // Deliberately do NOT setConfirmingReplace(false) first —
                // the window is about to close and the redundant state change
                // would briefly re-render the "Set up locally" button.
                Props.Dismiss();
            }

            bottomCluster = VStack(12,
                warnCard,
                BuildAccentButton(V2Strings.Get("V2_Welcome_Replace_Confirm"), ConfirmReplace, "V2_Welcome_ReplaceConfirm"),
                BuildLinkButton(V2Strings.Get("V2_Welcome_Replace_Keep"), KeepSetup, "V2_Welcome_ReplaceKeep"),
                BuildLinkButton(V2Strings.Get("V2_Welcome_AdvancedLink"), () => Props.RequestAdvancedSetup(), "V2_Welcome_AdvancedSetup")
            );
        }
        else
        {
            bottomCluster = VStack(16,
                infoCardWrap,
                BuildAccentButton(V2Strings.Get("V2_Welcome_PrimaryButton"), () => Props.RequestAdvance(), "V2_Welcome_SetUpLocally"),
                BuildLinkButton(V2Strings.Get("V2_Welcome_AdvancedLink"), () => Props.RequestAdvancedSetup(), "V2_Welcome_AdvancedSetup")
            );
        }

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
                    .Margin(0, 12, 0, 0)
                    .Set(t => t.Foreground = V2Theme.TextStrong(theme)),
                TextBlock(V2Strings.Get("V2_Welcome_Body"))
                    .FontSize(14)
                    .HAlign(HorizontalAlignment.Center)
                    .TextWrapping()
                    .MaxWidth(440)
                    .Set(t =>
                    {
                        t.Foreground = V2Theme.TextSecondary(theme);
                        t.TextAlignment = TextAlignment.Center;
                    })
            )
            .HAlign(HorizontalAlignment.Center)
            .Grid(row: 1, column: 0),

            // Flex spacer fills the middle so the bottom cluster pins to the bottom.
            new BorderElement(null).Grid(row: 2, column: 0),

            bottomCluster
                .Margin(40, 0, 40, 40)
                .Grid(row: 3, column: 0)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch);
    }
}
