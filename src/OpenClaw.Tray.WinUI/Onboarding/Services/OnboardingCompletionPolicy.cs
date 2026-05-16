namespace OpenClawTray.Onboarding.Services;

public enum OnboardingCompletionOutcome
{
    Complete,
    BlockIncompleteReady
}

public static class OnboardingCompletionPolicy
{
    public static OnboardingCompletionOutcome Decide(bool atTerminalPage, bool setupStillRequired) =>
        atTerminalPage && setupStillRequired
            ? OnboardingCompletionOutcome.BlockIncompleteReady
            : OnboardingCompletionOutcome.Complete;
}
