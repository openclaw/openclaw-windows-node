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
        (Stage.RemovingExistingGateway, "V2_Progress_Stage_RemovingExistingGateway"),
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
            if (stage == Stage.RemovingExistingGateway
                && Props.ExistingGateway != OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl)
            {
                continue;
            }

            var rowState = Props.LocalSetupRows.TryGetValue(stage, out var s)
                ? s
                : RowState.Idle;

            // Optional inline hint — embedded INSIDE the row container so the
            // reconciler doesn't reuse a stale sibling background when the row
            // transitions out of Running. Anchored to CheckSystem only since
            // that's the only stage whose engine phases (Preflight + EnsureWslEnabled)
            // can dwell long enough to warrant a hint.
            var inlineHint = (stage == Stage.CheckSystem
                              && rowState == RowState.Running
                              && !string.IsNullOrWhiteSpace(Props.LocalSetupInfoMessage)
                              && Props.LocalSetupErrorMessage is null)
                ? Props.LocalSetupInfoMessage
                : null;

            // Optional inline error card — also embedded INSIDE the row
            // container for the same reconciler-stability reason. Previously
            // this was a sibling element which produced "pink box around
            // an unrelated row" artifacts as the engine moved through
            // different failure modes on successive Try-again clicks
            // (FunctionalUI position-based reconciliation reuses the error
            // card's BorderElement slot when the failed row index shifts).
            string? inlineError = null;
            Action? inlineRetry = null;
            if (rowState == RowState.Failed && Props.LocalSetupErrorMessage is { } msg)
            {
                inlineError = msg;
                inlineRetry = Props.LocalSetupCanRetry ? (() => Props.RequestRetry()) : null;
            }

            rowChildren.Add(BuildStageRow(theme, V2Strings.Get(labelKey), rowState, inlineHint, inlineError, inlineRetry));
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

    private static Element BuildStageRow(ElementTheme theme, string label, RowState state, string? inlineHint = null, string? inlineErrorMessage = null, Action? inlineRetry = null)
    {
        var headerGrid = Grid(
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

        // No optional content → just the header. Predictable, single Grid.
        if (string.IsNullOrWhiteSpace(inlineHint) && string.IsNullOrWhiteSpace(inlineErrorMessage))
            return headerGrid;

        var stack = new List<Element> { headerGrid };

        if (!string.IsNullOrWhiteSpace(inlineHint))
        {
            // Inline hint subtitle. Embedded as a child of the row container
            // so the row's position in the parent VStack stays stable when
            // the hint appears/disappears, preventing FunctionalUI from
            // reusing the hint's element slot for a different row.
            stack.Add(TextBlock(inlineHint!)
                .FontSize(14)
                .TextWrapping()
                .Set(t => t.Foreground = V2Theme.TextSubtle(theme))
                .Margin(0, 4, 0, 0));
        }

        if (!string.IsNullOrWhiteSpace(inlineErrorMessage))
        {
            // Inline error card — embedded for the same reconciler-stability
            // reason as the hint. Keeps the pink failure card glued to the
            // row that actually failed instead of leaking to whichever row
            // happens to occupy the previous error card's position after a
            // re-render shifts the children list.
            stack.Add(BuildErrorCard(theme, inlineErrorMessage!, inlineRetry));
        }

        return VStack(8, stack.ToArray());
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
        // CRITICAL: explicit Background(Transparent) is required. Without it,
        // FunctionalUI's position-based reconciliation can reuse the
        // BorderElement created for a previous render's CheckmarkBadge or
        // ErrorBadge at this row position — keeping the green/pink fill
        // because the next render never overwrites the Background property.
        // Setting it to Transparent every time guarantees a clean slate.
        // (Same hazard fixed inline for the row container's hint card.)
        return new BorderElement(null)
            .Width(24)
            .Height(24)
            .Background(V2Theme.Transparent());
    }

    /// <summary>
    /// Renders the inline error card under a failed row. When
    /// <paramref name="onTryAgain"/> is null, the Try-again button is omitted
    /// (terminal/blocked failures); when non-null, it renders as the
    /// right-aligned action button.
    /// </summary>
    private static Element BuildErrorCard(ElementTheme theme, string message, Action? onTryAgain)
    {
        var children = new List<Element>
        {
            TextBlock(message)
                .FontSize(14)
                .TextWrapping()
                .VAlign(VerticalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.ErrorCardForeground(theme))
                .Grid(row: 0, column: 0),
        };

        if (onTryAgain is not null)
        {
            children.Add(
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
                    .Grid(row: 0, column: 1));
        }

        // Single column when no retry button — error message expands to full width.
        var columns = onTryAgain is not null ? new[] { "*", "auto" } : new[] { "*" };
        var inner = Grid(columns, new[] { "auto" }, children.ToArray());

        return new BorderElement(inner)
            .Background(V2Theme.ErrorCardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Margin(0, -8, 0, 0)
            .Set(b => b.CornerRadius = new CornerRadius(8))
            .WithSlideInFromBelow(distance: 14, durationMs: 320);
    }
}
