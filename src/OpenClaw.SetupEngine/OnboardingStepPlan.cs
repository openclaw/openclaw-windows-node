namespace OpenClaw.SetupEngine;

/// <summary>The four user-facing steps shown by the setup window's step timeline.</summary>
public enum OnboardingDesignStep
{
    ChooseTools,
    SetUp,
    ConnectAi,
    StartChatting,
}

/// <summary>How the timeline renders one design step for the current page.</summary>
public enum OnboardingDesignStepState
{
    Upcoming,
    Current,
    Done,
    /// <summary>Passed because the route has no page for it. Renders as done; announced as skipped.</summary>
    Skipped,
}

/// <summary>
/// Projects the route's internal <see cref="OnboardingStage"/> list onto the four design steps.
/// The timeline always shows all four steps; steps the route skips render as done once passed.
/// </summary>
/// <example>
/// <code>
/// var states = OnboardingStepPlan.Build(
///     OnboardingFlowPolicy.GetStages(route, config), OnboardingStage.AiSetup);
/// // states[(int)OnboardingDesignStep.SetUp] == Skipped for an existing Gateway.
/// </code>
/// </example>
public static class OnboardingStepPlan
{
    public const int StepCount = 4;

    public static OnboardingDesignStep GetDesignStep(OnboardingStage stage) => stage switch
    {
        OnboardingStage.Welcome or OnboardingStage.Gateway or OnboardingStage.Capabilities or
            OnboardingStage.GatewayReview => OnboardingDesignStep.ChooseTools,
        OnboardingStage.Install => OnboardingDesignStep.SetUp,
        OnboardingStage.AiSetup => OnboardingDesignStep.ConnectAi,
        OnboardingStage.Ready => OnboardingDesignStep.StartChatting,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    public static IReadOnlyList<OnboardingDesignStepState> Build(
        IReadOnlyList<OnboardingStage> routeStages,
        OnboardingStage current)
    {
        ArgumentNullException.ThrowIfNull(routeStages);
        var currentStep = GetDesignStep(current);
        var states = new OnboardingDesignStepState[StepCount];
        for (var index = 0; index < StepCount; index++)
        {
            var step = (OnboardingDesignStep)index;
            states[index] = step == currentStep
                ? OnboardingDesignStepState.Current
                : step > currentStep
                    ? OnboardingDesignStepState.Upcoming
                    : routeStages.Any(stage => GetDesignStep(stage) == step)
                        ? OnboardingDesignStepState.Done
                        : OnboardingDesignStepState.Skipped;
        }
        return states;
    }
}
