using OpenClawTray.Onboarding.V2;

namespace OpenClawTray.OnboardingV2.Tests;

/// <summary>
/// Mirrors <c>OnboardingStateTests.Dismiss_*</c> for the V2 state surface.
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
}
