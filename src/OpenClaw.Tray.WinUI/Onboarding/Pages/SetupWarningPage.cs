using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 0 of the forked Phase-5 onboarding flow.
///
/// Layout contract (Mattingly Phase 5, decisions/inbox/mattingly-warning-page-layout.md):
///
///   Grid
///     Rows: Auto (title), 1* (body+spacer), Auto (primary), Auto (hyperlink)
///     Columns: 1*
///     HAlign Center / VAlign Center / MaxWidth 460
///     Row 0: TextBlock title — bold 22pt, centered
///     Row 1: TextBlock body — 14pt, 0.65 opacity, wrapping; security notice folded in
///     Row 2: Button "Set up locally" — accent fill, MinWidth 200, Height 44, centered
///     Row 3: Button "Advanced setup" styled as TextBlockButton (hyperlink), 8px top margin
///
/// Picking either path sets <see cref="OnboardingState.SetupPath"/> and fires
/// <see cref="OnboardingState.AdvanceRequested"/>; <see cref="OnboardingApp"/>
/// catches the event and navigates to the next page in the (re-derived) order.
/// The nav-bar Next button is disabled on this page until a path is chosen.
/// </summary>
public sealed class SetupWarningPage : Component<OnboardingState>
{
    public override Element Render()
    {
        const string TitleText = "Set up OpenClaw";
        // Body folds in the ⚠️ security notice (Mike's decision — WelcomePage removed).
        const string BodyText =
            "OpenClaw lets agents run commands, read and write files, and capture screenshots " +
            "on this PC. Only set it up on a computer you trust.\n\n" +
            "⚠️ The local setup installs a small WSL Linux instance dedicated to OpenClaw. " +
            "If you'd rather connect to an existing or remote gateway, choose Advanced setup.";

        void ChooseLocal()
        {
            Props.SetupPath = Onboarding.Services.SetupPath.Local;
            Props.Mode = ConnectionMode.Local;
            Props.RequestAdvance();
        }

        void ChooseAdvanced()
        {
            Props.SetupPath = Onboarding.Services.SetupPath.Advanced;
            Props.RequestAdvance();
        }

        return Grid(
            columns: ["1*"],
            rows: ["Auto", "1*", "Auto", "Auto"],

            TextBlock(TitleText)
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .Grid(row: 0, column: 0),

            TextBlock(BodyText)
                .FontSize(14)
                .Opacity(0.65)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Top)
                .TextWrapping()
                .Margin(0, 12, 0, 12)
                .Grid(row: 1, column: 0),

            Button("Set up locally", ChooseLocal)
                .MinWidth(200)
                .Height(44)
                .HAlign(HorizontalAlignment.Center)
                .Set(b =>
                {
                    b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingSetupLocal");
                })
                .Grid(row: 2, column: 0),

            Button("Advanced setup", ChooseAdvanced)
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 0)
                .Set(b =>
                {
                    if (Application.Current.Resources.TryGetValue("TextBlockButtonStyle", out var hyperStyle) &&
                        hyperStyle is Style s)
                    {
                        b.Style = s;
                    }
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingSetupAdvanced");
                })
                .Grid(row: 3, column: 0)
        )
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center)
        .MaxWidth(460)
        .Padding(0, 8, 0, 0);
    }
}
