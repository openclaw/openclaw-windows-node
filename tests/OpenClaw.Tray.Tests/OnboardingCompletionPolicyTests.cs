using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class OnboardingCompletionPolicyTests
{
    [Fact]
    public void Decide_TerminalPageWithSetupStillRequired_BlocksCompletion()
    {
        var outcome = OnboardingCompletionPolicy.Decide(atTerminalPage: true, setupStillRequired: true);

        Assert.Equal(OnboardingCompletionOutcome.BlockIncompleteReady, outcome);
    }

    [Fact]
    public void Decide_TerminalPageWithSetupComplete_AllowsCompletion()
    {
        var outcome = OnboardingCompletionPolicy.Decide(atTerminalPage: true, setupStillRequired: false);

        Assert.Equal(OnboardingCompletionOutcome.Complete, outcome);
    }

    [Fact]
    public void Decide_NonTerminalPage_PreservesExistingCompletionBehavior()
    {
        var outcome = OnboardingCompletionPolicy.Decide(atTerminalPage: false, setupStillRequired: true);

        Assert.Equal(OnboardingCompletionOutcome.Complete, outcome);
    }
}
