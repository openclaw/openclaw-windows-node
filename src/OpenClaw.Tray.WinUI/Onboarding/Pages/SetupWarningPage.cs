using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 0 of the forked Phase-5 onboarding flow.
///
/// Layout contract (Mattingly Phase 5 + PR #274 must-fix #6):
///
///   Grid
///     Rows: Auto (title), 1* (body+spacer), Auto (primary or warning section), Auto (hyperlink)
///     Columns: 1*
///     HAlign Center / VAlign Center / MaxWidth 460
///     Row 0: TextBlock title — bold 22pt, centered
///     Row 1: TextBlock body — 14pt, 0.65 opacity, wrapping; security notice folded in
///     Row 2: [no existing config] Button "Set up locally" — accent fill, MinWidth 200, Height 44, centered
///             [existing config] VStack: ⚠️ heading + body + "Replace my setup" (accent) + "Keep my setup" (hyperlink)
///     Row 3: Button "Advanced setup" styled as TextBlockButton (hyperlink), 8px top margin (always visible)
///
/// When existing config is detected (<see cref="OnboardingState.ExistingConfigGuard"/>
/// returns HasExistingConfiguration=true), the warn-and-confirm section replaces row 2
/// immediately on page load. The user must explicitly click "Replace my setup" before
/// the local setup path can advance. "Advanced setup" is always available in row 3.
/// </summary>
public sealed class SetupWarningPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var guard = Props.ExistingConfigGuard;
        var hasExisting = guard?.HasExistingConfiguration() == true;

        // Initialize warn-confirm state to true when existing config detected so the
        // warning is visible immediately on page load (Mike's directive: initial page
        // MUST show warning when existing gateway is paired).
        var (confirmingReplace, setConfirmingReplace) = UseState(hasExisting);

        string titleText = LocalizationHelper.GetString("Onboarding_SetupWarning_Title");
        string bodyText = LocalizationHelper.GetString("Onboarding_SetupWarning_Body");

        void ChooseLocal()
        {
            if (guard?.HasExistingConfiguration() == true)
            {
                // Show warn-and-confirm section in-place.
                setConfirmingReplace(true);
            }
            else
            {
                Props.SetupPath = Onboarding.Services.SetupPath.Local;
                Props.Mode = ConnectionMode.Local;
                Props.RequestAdvance();
            }
        }

        void ConfirmReplace()
        {
            Props.ReplaceExistingConfigurationConfirmed = true;
            Props.SetupPath = Onboarding.Services.SetupPath.Local;
            Props.Mode = ConnectionMode.Local;
            Props.RequestAdvance();
        }

        void CancelReplace()
        {
            setConfirmingReplace(false);
        }

        void ChooseAdvanced()
        {
            Props.SetupPath = Onboarding.Services.SetupPath.Advanced;
            Props.RequestAdvance();
        }

        // Row 2: either the local setup button or the warn-and-confirm section.
        Element row2;
        if (confirmingReplace)
        {
            var summary = guard?.GetSummary();
            var replaceBody = LocalizationHelper.GetString("Onboarding_SetupWarning_ReplaceBody");

            // Append dynamic lost-items detail (Mike Q2: list specifically what is lost).
            var lostItems = new System.Collections.Generic.List<string>();
            if (summary?.HasToken == true) lostItems.Add("gateway token");
            if (summary?.HasOperatorDeviceToken == true || summary?.HasNodeDeviceToken == true) lostItems.Add("device pairing");
            if (summary?.HasNonDefaultGatewayUrl == true) lostItems.Add("current gateway URL");
            if (summary?.HasBootstrapToken == true) lostItems.Add("bootstrap token");
            if (lostItems.Count > 0)
                replaceBody += $" This will overwrite: {string.Join(", ", lostItems)}.";

            row2 = VStack(8,
                TextBlock(LocalizationHelper.GetString("Onboarding_SetupWarning_ReplaceHeading"))
                    .FontSize(15)
                    .FontWeight(new global::Windows.UI.Text.FontWeight(600))
                    .HAlign(HorizontalAlignment.Center)
                    .TextWrapping(),

                TextBlock(replaceBody)
                    .FontSize(13)
                    .Opacity(0.75)
                    .HAlign(HorizontalAlignment.Center)
                    .TextWrapping()
                    .Margin(0, 4, 0, 8),

                Button(LocalizationHelper.GetString("Onboarding_SetupWarning_ReplaceConfirm"), ConfirmReplace)
                    .MinWidth(200)
                    .Height(44)
                    .HAlign(HorizontalAlignment.Center)
                    .Set(b =>
                    {
                        b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingReplaceConfirm");
                    }),

                Button(LocalizationHelper.GetString("Onboarding_SetupWarning_ReplaceCancel"), CancelReplace)
                    .HAlign(HorizontalAlignment.Center)
                    .Set(b =>
                    {
                        if (Application.Current.Resources.TryGetValue("TextBlockButtonStyle", out var hyperStyle) &&
                            hyperStyle is Style s)
                        {
                            b.Style = s;
                        }
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingReplaceCancel");
                    })
            )
            .HAlign(HorizontalAlignment.Center)
            .Grid(row: 2, column: 0);
        }
        else
        {
            row2 = Button(LocalizationHelper.GetString("Onboarding_SetupWarning_SetupLocally"), ChooseLocal)
                .MinWidth(200)
                .Height(44)
                .HAlign(HorizontalAlignment.Center)
                .Set(b =>
                {
                    b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "OnboardingSetupLocal");
                })
                .Grid(row: 2, column: 0);
        }

        return Grid(
            columns: ["1*"],
            rows: ["Auto", "1*", "Auto", "Auto"],

            TextBlock(titleText)
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .Grid(row: 0, column: 0),

            TextBlock(bodyText)
                .FontSize(14)
                .Opacity(0.65)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Top)
                .TextWrapping()
                .Margin(0, 12, 0, 12)
                .Grid(row: 1, column: 0),

            row2,

            Button(LocalizationHelper.GetString("Onboarding_SetupWarning_Advanced"), ChooseAdvanced)
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
