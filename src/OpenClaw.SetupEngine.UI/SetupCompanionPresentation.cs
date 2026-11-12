using OpenClaw.SetupEngine.UI.Pages;

namespace OpenClaw.SetupEngine.UI;

/// <summary>A page's default companion line (a resource key) and mascot mood.</summary>
internal sealed record SetupCompanionPresentation(string LineResourceKey, OnboardingMascotMood Mood)
{
    /// <summary>
    /// Defaults applied when navigation starts. Pages refine them for their own state, for
    /// example the install review's replacement confirmation or a failed completion.
    /// </summary>
    public static SetupCompanionPresentation? For(Type? page) => page?.Name switch
    {
        nameof(SecurityNoticePage) => new("Onboarding_Bubble_Welcome", OnboardingMascotMood.Idle),
        nameof(WelcomePage) or nameof(AdvancedSetupPage) =>
            new("Onboarding_Bubble_Gateway", OnboardingMascotMood.Curious),
        nameof(SetupNativeConnectionPage) => new("Onboarding_Bubble_Connect", OnboardingMascotMood.Curious),
        nameof(CapabilitiesPage) => new("Onboarding_Bubble_Capabilities", OnboardingMascotMood.Curious),
        nameof(GatewaySetupPage) => new("Onboarding_Bubble_Plan", OnboardingMascotMood.Curious),
        nameof(GatewaySetupDetailPage) => new("Onboarding_Bubble_MoreOptions", OnboardingMascotMood.Curious),
        nameof(ProgressPage) or nameof(NativeGatewaySetupPage) =>
            new("Onboarding_Bubble_Working", OnboardingMascotMood.Working),
        nameof(WizardPage) or nameof(AiSetupPage) => new("Onboarding_Bubble_Ai", OnboardingMascotMood.Curious),
        nameof(AiCompletionPage) => new("Onboarding_Bubble_Checking", OnboardingMascotMood.Thinking),
        nameof(AiReadyPage) => new("Onboarding_Bubble_Ready", OnboardingMascotMood.Thinking),
        _ => null,
    };
}
