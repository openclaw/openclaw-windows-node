using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Stage = OpenClawTray.Onboarding.V2.OnboardingV2State.LocalSetupStage;
using RowState = OpenClawTray.Onboarding.V2.OnboardingV2State.LocalSetupRowState;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Local setup progress page (Dialog-1 + Dialog-6).
///
/// Layout:
///   * Centered title "Setting up locally" (28pt SemiBold).
///   * Centered subtitle "Creating OpenClaw Gateway WSL instance"
///     (14pt 60% white).
///   * Vertical list of seven stage rows, left-aligned with a status
///     badge on the right (green checkmark, blue spinner, pink X, or
///     nothing for idle). Generous ~56px row spacing.
///   * When LocalSetupErrorMessage is non-null, an inline error card
///     (dark maroon bg, body text + "Try again" button on the right)
///     slides in immediately below the failed row.
///
/// Driven entirely by state.LocalSetupRows + state.LocalSetupErrorMessage,
/// which the preview populates from env vars (PROGRESS_FROZEN_STAGE,
/// FAIL_STAGE) and the real engine populates from
/// LocalGatewaySetupEngine progress events at cutover.
///
/// Colours come from <see cref="V2Theme"/> keyed on <see cref="OnboardingV2State.EffectiveTheme"/>.
/// </summary>
public sealed class LocalSetupProgressPage : Component<OnboardingV2State>
{
    private static readonly (Stage Stage, string LabelKey)[] StageLabels =
    {
        (Stage.CheckSystem, "V2_Progress_Stage_CheckSystem"),
        (Stage.InstallingUbuntu, "V2_Progress_Stage_InstallingUbuntu"),
        (Stage.ConfiguringInstance, "V2_Progress_Stage_ConfiguringInstance"),
        (Stage.InstallingOpenClaw, "V2_Progress_Stage_InstallingOpenClaw"),
        (Stage.PreparingGateway, "V2_Progress_Stage_PreparingGateway"),
        (Stage.StartingGateway, "V2_Progress_Stage_StartingGateway"),
        (Stage.GeneratingSetupCode, "V2_Progress_Stage_GeneratingSetupCode"),
    };

    public override Element Render()
    {
        var theme = Props.EffectiveTheme;
        var rowChildren = new List<Element>();
        foreach (var (stage, labelKey) in StageLabels)
        {
            var rowState = Props.LocalSetupRows.TryGetValue(stage, out var s)
                ? s
                : RowState.Idle;
            rowChildren.Add(BuildStageRow(theme, V2Strings.Get(labelKey), rowState));

            // The error card sits immediately under the failed row in Dialog-6.
            if (rowState == RowState.Failed && Props.LocalSetupErrorMessage is { } msg)
            {
                rowChildren.Add(BuildErrorCard(theme, msg, () => Props.RequestRetry()));
            }
        }

        return VStack(0,
            // Top spacer pushes the title down from the title bar.
            new BorderElement(null).Height(40),

            TextBlock(V2Strings.Get("V2_Progress_Title"))
                .FontSize(28)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.TextStrong(theme)),

            TextBlock(V2Strings.Get("V2_Progress_Subtitle"))
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 0)
                .Set(t => t.Foreground = V2Theme.TextSubtle(theme)),

            // Spacer between header and list.
            new BorderElement(null).Height(48),

            VStack(28, rowChildren.ToArray())
                .HAlign(HorizontalAlignment.Stretch)
                .Margin(56, 0, 56, 0)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Top);
    }

    private static Element BuildStageRow(ElementTheme theme, string label, RowState state)
    {
        return Grid(
            new[] { "*", "auto" },
            new[] { "auto" },
            TextBlock(label)
                .FontSize(18)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.TextPrimary(theme))
                .Grid(row: 0, column: 0),

            BuildStatusBadge(state)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1)
        );
    }

    private static Element BuildStatusBadge(RowState state) => state switch
    {
        RowState.Done => CheckmarkBadge(),
        RowState.Running => SpinnerBadge(),
        RowState.Failed => ErrorBadge(),
        _ => InvisibleBadge(),
    };

    private static Element CheckmarkBadge()
    {
        // 24px green circle with a white tick.
        return new BorderElement(
            TextBlock("\u2713") // ✓
                .FontSize(14)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.White())
        )
        .Background(V2Theme.BadgeCheckGreen())
        .Width(24)
        .Height(24)
        .Set(b => b.CornerRadius = new CornerRadius(12));
    }

    private static Element SpinnerBadge()
    {
        // ProgressRing tinted to the design accent.
        return ProgressRing()
            .Width(24)
            .Height(24)
            .Set(p =>
            {
                p.IsActive = true;
                p.IsIndeterminate = true;
                p.Foreground = V2Theme.AccentCyan();
            });
    }

    private static Element ErrorBadge()
    {
        // 24px pink circle with a darker X.
        return new BorderElement(
            TextBlock("\u2715") // ✕
                .FontSize(14)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.OnAccentText())
        )
        .Background(V2Theme.BadgeErrorPink())
        .Width(24)
        .Height(24)
        .Set(b => b.CornerRadius = new CornerRadius(12));
    }

    private static Element InvisibleBadge()
    {
        // Reserve the same width so idle rows align with checkmarked rows.
        return new BorderElement(null).Width(24).Height(24);
    }

    private static Element BuildErrorCard(ElementTheme theme, string message, Action onTryAgain)
    {
        var inner = Grid(
            new[] { "*", "auto" },
            new[] { "auto" },
            TextBlock(message)
                .FontSize(14)
                .TextWrapping()
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.ErrorCardForeground(theme))
                .Grid(row: 0, column: 0),

            Button(V2Strings.Get("V2_Progress_TryAgain"), onTryAgain)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Margin(16, 0, 0, 0)
                .Width(120)
                .Height(40)
                .Set(b =>
                {
                    b.Foreground = V2Theme.White();
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Progress_TryAgain");
                    b.Resources["ButtonBackground"] = V2Theme.ErrorButtonBackground(theme);
                    b.Resources["ButtonBackgroundPointerOver"] = V2Theme.ErrorButtonHover(theme);
                    b.Resources["ButtonBackgroundPressed"] = V2Theme.ErrorButtonPressed(theme);
                    b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
                    b.Resources["ButtonForeground"] = V2Theme.White();
                    b.Resources["ButtonForegroundPointerOver"] = V2Theme.White();
                    b.Resources["ButtonForegroundPressed"] = V2Theme.White();
                })
                .Grid(row: 0, column: 1)
        );

        return new BorderElement(inner)
            .Background(V2Theme.ErrorCardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Margin(0, -8, 0, 0)
            .Set(b => b.CornerRadius = new CornerRadius(8))
            .WithSlideInFromBelow(distance: 14, durationMs: 320);
    }
}
