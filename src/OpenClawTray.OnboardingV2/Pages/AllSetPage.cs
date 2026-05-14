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
/// </summary>
public sealed class AllSetPage : Component<OnboardingV2State>
{
    public override Element Render()
    {
        var (launchAtStartup, setLaunchAtStartup) = UseState(true);

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
                .Margin(0, 16, 0, 0),

            TextBlock(V2Strings.Get("V2_AllSet_Subtitle"))
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 0)
                .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xA0, 0xA0, 0xA0))),

            new BorderElement(null).Height(40),
        };

        if (Props.NodeModeActive)
        {
            children.Add(BuildNodeModeWarning().Margin(48, 0, 48, 0));
            children.Add(new BorderElement(null).Height(28));
        }

        children.Add(BuildStartupRow(launchAtStartup, setLaunchAtStartup).Margin(48, 0, 48, 0));

        return VStack(0, children.ToArray())
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Top);
    }

    private static Element BuildNodeModeWarning()
    {
        var warningBadge = new BorderElement(
            TextBlock("!")
                .FontSize(13)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x33, 0x28, 0x10)))
        )
        .Background("#E0A422")
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
                    .SemiBold(),
                TextBlock(V2Strings.Get("V2_AllSet_NodeMode_Body"))
                    .FontSize(13)
                    .TextWrapping()
                    .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xE8, 0xE0, 0xCC)))
            )
            .Grid(row: 0, column: 1)
        );

        return new BorderElement(inner)
            .Background("#5C4413")
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8));
    }

    private static Element BuildStartupRow(bool isOn, Action<bool> onToggle)
    {
        return Grid(
            new[] { "*", "auto", "auto" },
            new[] { "auto" },
            TextBlock(V2Strings.Get("V2_AllSet_StartupQuestion"))
                .FontSize(15)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),

            TextBlock(isOn ? V2Strings.Get("V2_AllSet_On") : V2Strings.Get("V2_AllSet_Off"))
                .FontSize(14)
                .VAlign(VerticalAlignment.Center)
                .Margin(0, 0, 12, 0)
                .Set(t => t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xD8, 0xD8, 0xD8)))
                .Grid(row: 0, column: 1),

            ToggleSwitch(isOn, v => onToggle(v), onContent: "", offContent: "")
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.MinWidth = 0)
                .Grid(row: 0, column: 2)
        );
    }
}

