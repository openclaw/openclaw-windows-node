using OpenClawTray.Onboarding.V2;

namespace OpenClawTray.OnboardingV2.Tests;

/// <summary>
/// Locks down V2 state dismissal behavior.
/// The V2 implementation copies the legacy idempotent-flag pattern; these
/// tests guarantee the symmetric contract continues to hold.
/// </summary>
public class OnboardingV2StateTests
{
    [Fact]
    public void Dismiss_FiresDismissedEvent()
    {
        var state = new OnboardingV2State();
        var fired = false;
        state.Dismissed += (_, _) => fired = true;

        state.Dismiss();

        Assert.True(fired);
    }

    [Fact]
    public void Dismiss_IsIdempotent_FiresDismissedAtMostOnce()
    {
        // Hardening: lifecycle signal must not fire twice if a page accidentally
        // calls Dismiss again (e.g., double-click or repeated handler invocation).
        var state = new OnboardingV2State();
        var count = 0;
        state.Dismissed += (_, _) => count++;

        state.Dismiss();
        state.Dismiss();
        state.Dismiss();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Dismiss_DoesNotFireFinishedEvent()
    {
        // "Keep my setup" must NOT route through the completion pipeline —
        // OnboardingWindow relies on Finished being unraised so it skips
        // TryCompleteOnboarding and leaves prior settings/connection untouched.
        var state = new OnboardingV2State();
        var finished = false;
        state.Finished += (_, _) => finished = true;

        state.Dismiss();

        Assert.False(finished);
    }

    [Fact]
    public void Dismiss_DoesNotThrow_WithoutHandler()
    {
        var state = new OnboardingV2State();
        var ex = Record.Exception(() => state.Dismiss());
        Assert.Null(ex);
    }

    // -----------------------------------------------------------------------
    // LocalSetupCanRetry — controls the Try-again button on the error card.
    // Hanselman review: terminal/blocked failures must not show retry; only
    // FailedRetryable errors should expose the affordance.
    // -----------------------------------------------------------------------

    [Fact]
    public void LocalSetupCanRetry_DefaultsToFalse()
    {
        var state = new OnboardingV2State();
        Assert.False(state.LocalSetupCanRetry);
    }

    [Fact]
    public void LocalSetupCanRetry_SetTrue_FiresStateChanged()
    {
        var state = new OnboardingV2State();
        var changed = 0;
        state.StateChanged += (_, _) => changed++;

        state.LocalSetupCanRetry = true;

        Assert.Equal(1, changed);
        Assert.True(state.LocalSetupCanRetry);
    }

    [Fact]
    public void LocalSetupCanRetry_SetSameValue_DoesNotFireStateChanged()
    {
        // Idempotent setter avoids redundant page re-renders when the bridge
        // re-emits the same status (e.g. multiple Running ticks).
        var state = new OnboardingV2State { LocalSetupCanRetry = true };
        var changed = 0;
        state.StateChanged += (_, _) => changed++;

        state.LocalSetupCanRetry = true;

        Assert.Equal(0, changed);
    }

    [Fact]
    public void ExistingGateway_SetChanged_FiresStateChanged()
    {
        var state = new OnboardingV2State();
        var changed = 0;
        state.StateChanged += (_, _) => changed++;

        state.ExistingGateway = OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl;

        Assert.Equal(1, changed);
        Assert.Equal(OnboardingV2State.ExistingGatewayKind.AppOwnedLocalWsl, state.ExistingGateway);
    }

    [Fact]
    public void RequestPrimarySetup_FiresPrimarySetupRequested()
    {
        var state = new OnboardingV2State();
        var fired = false;
        state.PrimarySetupRequested += (_, _) => fired = true;

        state.RequestPrimarySetup();

        Assert.True(fired);
    }
}
