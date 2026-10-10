namespace OpenClaw.SetupEngine;

public enum OnboardingStage
{
    Welcome,
    Gateway,
    Capabilities,
    GatewayReview,
    Install,
    AiSetup,
    Ready,
}

public enum OnboardingAccessDestination
{
    GatewayReview,
    AiSetup,
    CompleteWithoutGateway,
    NativeGatewaySetup,
}

public static class OnboardingFlowPolicy
{
    public static bool RequiresAiSetup(SetupConfig config) =>
        config.LocalAi.Enabled || !config.SkipWizard;

    public static bool RequiresAiSetup(SetupGatewayRoute route, SetupConfig config) => route switch
    {
        SetupGatewayRoute.ManagedWsl => RequiresAiSetup(config),
        SetupGatewayRoute.Existing or SetupGatewayRoute.Remote => true,
        SetupGatewayRoute.McpOnly or SetupGatewayRoute.Deferred => false,
        SetupGatewayRoute.Native => true,
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    public static OnboardingAccessDestination GetAccessDestination(SetupGatewayRoute route) => route switch
    {
        SetupGatewayRoute.ManagedWsl => OnboardingAccessDestination.GatewayReview,
        SetupGatewayRoute.Existing or SetupGatewayRoute.Remote => OnboardingAccessDestination.AiSetup,
        SetupGatewayRoute.McpOnly or SetupGatewayRoute.Deferred => OnboardingAccessDestination.CompleteWithoutGateway,
        SetupGatewayRoute.Native => OnboardingAccessDestination.NativeGatewaySetup,
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    public static bool UsesWslWorkspaceFinalization(SetupGatewayRoute route) =>
        route == SetupGatewayRoute.ManagedWsl;

    public static string GetCompletionLaunchTarget(SetupGatewayRoute route) => route switch
    {
        SetupGatewayRoute.ManagedWsl or SetupGatewayRoute.Existing or SetupGatewayRoute.Remote => "chat",
        SetupGatewayRoute.Native => "chat",
        SetupGatewayRoute.McpOnly => "settings",
        SetupGatewayRoute.Deferred => "connection",
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    public static IReadOnlyList<OnboardingStage> GetStages(
        bool installGateway,
        SetupConfig config,
        bool localAiRecovery = false) =>
        GetStages(installGateway ? SetupGatewayRoute.ManagedWsl : SetupGatewayRoute.Existing, config, localAiRecovery);

    public static IReadOnlyList<OnboardingStage> GetStages(
        SetupGatewayRoute route,
        SetupConfig config,
        bool localAiRecovery = false,
        bool includeReadyChoice = true)
    {
        bool installGateway = route == SetupGatewayRoute.ManagedWsl;
        List<OnboardingStage> stages = [];
        if (!localAiRecovery || !installGateway)
        {
            stages.Add(OnboardingStage.Welcome);
            stages.Add(OnboardingStage.Gateway);
            stages.Add(OnboardingStage.Capabilities);
        }
        if (installGateway)
        {
            stages.Add(OnboardingStage.GatewayReview);
            stages.Add(OnboardingStage.Install);
        }
        else if (route == SetupGatewayRoute.Native)
        {
            stages.Add(OnboardingStage.Install);
        }
        if (RequiresAiSetup(route, config))
        {
            stages.Add(OnboardingStage.AiSetup);
            if (includeReadyChoice) stages.Add(OnboardingStage.Ready);
        }
        return stages.AsReadOnly();
    }

    public static List<SetupStep> BuildInstallationSteps(bool localAiRecovery) =>
        (localAiRecovery
            ? SetupStepFactory.BuildLocalAiRecoverySteps()
            : SetupStepFactory.BuildDefaultSteps())
        .Where(step => step is not RunGatewayWizardStep and not WindowsNodeBootstrapContextStep)
        .ToList();
}
