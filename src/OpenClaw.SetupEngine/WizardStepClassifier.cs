namespace OpenClaw.SetupEngine;

/// <summary>How a gateway wizard step should be handled by the client.</summary>
public enum WizardStepCategory
{
    /// <summary>Needs a concrete value (text) or a selected option (select/multiselect).</summary>
    RequiresAnswer,

    /// <summary>Yes/No acknowledgement with a safe default (confirm).</summary>
    Confirm,

    /// <summary>Informational acknowledgement-only step (note).</summary>
    Acknowledge,

    /// <summary>Non-interactive background/status step.</summary>
    Progress,

    /// <summary>Unrecognized or client-executed step with no answerable input.</summary>
    NonInteractive,
}

/// <summary>Pure classification of gateway wizard step types.</summary>
public static class WizardStepClassifier
{
    /// <summary>Step types that represent non-interactive background progress.</summary>
    public static readonly IReadOnlySet<string> ProgressTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "progress", "status", "working" };

    public static bool IsProgress(string? stepType) =>
        !string.IsNullOrWhiteSpace(stepType) && ProgressTypes.Contains(stepType.Trim());

    /// <summary>Categorizes a step, preserving option-based prompts for unknown types.</summary>
    public static WizardStepCategory Categorize(string? stepType, bool hasOptions)
    {
        var type = (stepType ?? string.Empty).Trim();

        if (IsProgress(type))
            return WizardStepCategory.Progress;

        switch (type.ToLowerInvariant())
        {
            case "text":
            case "select":
            case "multiselect":
                return WizardStepCategory.RequiresAnswer;
            case "confirm":
                return WizardStepCategory.Confirm;
            case "note":
                return WizardStepCategory.Acknowledge;
        }

        // Unknown types with options behave like choice prompts; input-less
        // unknown types are treated as non-interactive protocol steps.
        return hasOptions ? WizardStepCategory.RequiresAnswer : WizardStepCategory.NonInteractive;
    }

    /// <summary>True when the step advances without an answer payload.</summary>
    public static bool ContinuesWithoutAnswer(WizardStepCategory category) =>
        category is WizardStepCategory.Progress or WizardStepCategory.NonInteractive;
}
