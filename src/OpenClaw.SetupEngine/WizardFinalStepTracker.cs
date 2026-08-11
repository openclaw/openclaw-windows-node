namespace OpenClaw.SetupEngine;

/// <summary>
/// Tracks whether the wizard request that is about to be sent answers the authoritative
/// final step. Only that request may be followed by a terminal hosted-TUI termination
/// that counts as completion, so every other transition clears the flag.
/// </summary>
internal sealed class WizardFinalStepTracker
{
    /// <summary>True when the most recent request answered the authoritative final step.</summary>
    public bool AnsweredFinalStep { get; private set; }

    /// <summary>Clears the flag for a fresh or replayed wizard session.</summary>
    public void ResetForNewSession() => AnsweredFinalStep = false;

    /// <summary>A progress poll carries no answer, so it is never the final step.</summary>
    public void RecordProgressAcknowledgement() => AnsweredFinalStep = false;

    /// <summary>Records the step whose answer is about to be sent.</summary>
    public void RecordAnsweredStep(
        string? stepType,
        string? stepId,
        string? title,
        bool hasOptions,
        int stepIndex,
        int totalSteps) =>
        AnsweredFinalStep =
            GatewayWizardRestartRecoveryPolicy.IsAuthoritativeFinalWizardStep(
                stepType,
                stepId,
                title,
                hasOptions,
                stepIndex,
                totalSteps);
}
