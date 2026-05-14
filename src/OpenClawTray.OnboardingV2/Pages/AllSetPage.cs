using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// All Set page (Dialog-4) — terminal page of the V2 setup flow.
///
/// Layout:
///   * Centered party-popper hero (~120px).
///   * "All set!" title (28pt SemiBold, centered).
///   * Subtitle "OpenClaw is ready to go" (14pt 60% white, centered).
///   * Optional Node-Mode warning bar (only when state.NodeModeActive):
///     dark amber bg (#5C4413), yellow ⚠ badge, bold heading
///     "Node Mode Active" + body explaining the implications.
///   * Row with "Launch OpenClaw at startup?" label + accent
///     ToggleSwitch.
///
/// The Finish button at the bottom of the nav bar is rendered by the
/// shell (OnboardingV2App swaps Next → Finish on the last page).
///
/// Colours come from <see cref="V2Theme"/> keyed on <see cref="OnboardingV2State.EffectiveTheme"/>.
/// </summary>
public sealed class AllSetPage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        var theme = Props.EffectiveTheme;
        var (launchAtStartup, setLaunchAtStartup) = UseState(Props.LaunchAtStartup);

        // Two-way bridge: when the page-local state changes, push to the shared
        // OnboardingV2State so the host can persist Settings.AutoStart at finish.
        if (Props.LaunchAtStartup != launchAtStartup)
        {
            Props.LaunchAtStartup = launchAtStartup;
        }

        var children = new List<Element?>
        {
            new BorderElement(null).Height(36),

            Image("ms-appx:///Assets/Setup/PartyPopper.png")
                .Width(120)
                .Height(120)
                .HAlign(HorizontalAlignment.Center)
                .WithEntrancePopIn(durationMs: 520),

            TextBlock(V2Strings.Get("V2_AllSet_Title"))
                .FontSize(32)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 16, 0, 0)
                .Set(t => t.Foreground = V2Theme.TextStrong(theme)),

            TextBlock(V2Strings.Get("V2_AllSet_Subtitle"))
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 0)
                .Set(t => t.Foreground = V2Theme.TextSubtle(theme)),

            new BorderElement(null).Height(40),
        };

        if (Props.NodeModeActive)
        {
            children.Add(BuildNodeModeWarning(theme).Margin(48, 0, 48, 0));
            children.Add(new BorderElement(null).Height(28));
        }

        children.Add(BuildStartupRow(theme, launchAtStartup, setLaunchAtStartup).Margin(48, 0, 48, 0));

        return VStack(0, children.ToArray())
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Top);
    }

    private static Element BuildNodeModeWarning(ElementTheme theme)
    {
        var warningBadge = new BorderElement(
            TextBlock("!")
                .FontSize(13)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.WarningCardHover(theme))
        )
        .Background(V2Theme.BadgeWarningAmber())
        .Width(20)
        .Height(20)
        .VAlign(VerticalAlignment.Top)
        .Margin(0, 2, 12, 0)
        .Set(b => b.CornerRadius = new CornerRadius(10));

        var inner = Grid(
            new[] { "auto", "*" },
            new[] { "auto" },
            warningBadge.Grid(row: 0, column: 0),
            VStack(10,
                TextBlock(V2Strings.Get("V2_AllSet_NodeMode_Title"))
                    .FontSize(15)
                    .SemiBold()
                    .Set(t => t.Foreground = V2Theme.WarningCardForeground(theme)),
                TextBlock(V2Strings.Get("V2_AllSet_NodeMode_Body"))
                    .FontSize(13)
                    .TextWrapping()
                    .Set(t => t.Foreground = V2Theme.WarningCardForeground(theme))
            )
            .Grid(row: 0, column: 1)
        );

        return new BorderElement(inner)
            .Background(V2Theme.WarningCardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8));
    }

    private static Element BuildStartupRow(ElementTheme theme, bool isOn, Action<bool> onToggle)
    {
        return Grid(
            new[] { "*", "auto", "auto" },
            new[] { "auto" },
            TextBlock(V2Strings.Get("V2_AllSet_StartupQuestion"))
                .FontSize(15)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.TextPrimary(theme))
                .Grid(row: 0, column: 0),

            TextBlock(isOn ? V2Strings.Get("V2_AllSet_On") : V2Strings.Get("V2_AllSet_Off"))
                .FontSize(14)
                .VAlign(VerticalAlignment.Center)
                .Margin(0, 0, 12, 0)
                .Set(t => t.Foreground = V2Theme.TextSecondary(theme))
                .Grid(row: 0, column: 1),

            ToggleSwitch(isOn, v => onToggle(v), onContent: "", offContent: "")
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Set(t =>
                {
                    t.MinWidth = 0;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(t, "V2_AllSet_LaunchAtStartup");
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(t, V2Strings.Get("V2_AllSet_StartupQuestion"));
                })
                .Grid(row: 0, column: 2)
        );
    }
}
