using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Pure mapping helpers for <c>LocalSetupProgressPage</c> nav-bar policy
/// (Phase 5 final). Lives in the Services namespace (no WinUI / FunctionalUI
/// dependencies) so unit tests in <c>OpenClaw.Tray.Tests</c> can import it
/// directly via the project's selective <c>&lt;Compile Include&gt;</c> list.
/// </summary>
public static class LocalSetupProgressPolicy
{
    /// <summary>
    /// Maps a <see cref="LocalGatewaySetupState"/> snapshot to the nav-bar
    /// Next button state per the Phase 5 final Next/Back-button policy.
    ///
    /// Mapping:
    ///   null / Pending             → Hidden            (engine not started; Idle)
    ///   Running                    → VisibleDisabled   (engine progressing)
    ///   Complete                   → VisibleEnabled    (1s pre-auto-advance; tap to skip)
    ///   FailedRetryable            → VisibleDisabled   (in-page Try Again is the action)
    ///   FailedTerminal             → VisibleDisabled   (force Back-out; no advancing past broken gateway)
    ///   RequiresAdmin / RequiresRestart / Blocked / Cancelled → VisibleDisabled
    ///
    /// Back is always enabled by the OnboardingApp default (pageIndex &gt; 0
    /// on LocalSetupProgress because SetupWarning is page 0).
    /// </summary>
    public static OnboardingNextButtonState MapStatusToNextButtonState(LocalGatewaySetupState? snapshot, LocalGatewaySetupStatus status)
    {
        if (snapshot == null)
            return OnboardingNextButtonState.Hidden;

        return status switch
        {
            LocalGatewaySetupStatus.Pending => OnboardingNextButtonState.Hidden,
            LocalGatewaySetupStatus.Complete => OnboardingNextButtonState.VisibleEnabled,
            _ => OnboardingNextButtonState.VisibleDisabled,
        };
    }
}
