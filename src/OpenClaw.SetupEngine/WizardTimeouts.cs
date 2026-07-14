namespace OpenClaw.SetupEngine;

/// <summary>Selects the request timeout for a wizard step.</summary>
public static class WizardTimeouts
{
    /// <summary>Default per-step wizard request timeout.</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>Extended timeout for steps that wait on auth or external setup work.</summary>
    public const int SlowStepTimeoutMs = 300_000;

    /// <summary>Polling delay for gateway progress/status wizard steps.</summary>
    public static readonly TimeSpan ProgressPollDelay = TimeSpan.FromSeconds(1);

    /// <summary>Total progress/status updates before setup fails as stalled.</summary>
    public const int MaxTotalProgressPolls = 1200;

    /// <summary>Per-step progress/status budget; allow a single long install to consume the total budget.</summary>
    public const int MaxProgressPollsPerStep = MaxTotalProgressPolls;

    private static readonly string[] s_authHints =
    {
        "device", "authorize", "login", "sign in", "oauth",
        "browser", "authenticate", "verification",
    };

    private static readonly string[] s_slowSetupHints =
    {
        "plugin", "install", "download", "teams",
    };

    /// <summary>
    /// Auth and external setup steps get <see cref="SlowStepTimeoutMs"/>; ordinary
    /// questions get <see cref="DefaultTimeoutMs"/>. Only selected option metadata
    /// is considered so an unselected slow integration does not extend every choice.
    /// </summary>
    public static int ForStep(
        string? title,
        string? message,
        string? stepId = null,
        IReadOnlyCollection<WizardOptionValue>? selectedOptions = null)
    {
        var promptText = JoinText(title, message);
        if (HasAnyHint(promptText, s_authHints))
            return SlowStepTimeoutMs;

        // With a choice step, only the submitted option proves which operation
        // will run. This keeps "skip" and unrelated options on the short path.
        var slowText = selectedOptions is null
            ? JoinText(title, message, stepId)
            : JoinOptionText(selectedOptions);
        if (HasAnyHint(slowText, s_slowSetupHints))
            return SlowStepTimeoutMs;

        return DefaultTimeoutMs;
    }

    internal static int ForGatewayStep(
        string? title,
        string? message,
        string? stepId,
        string? stepType,
        IReadOnlyList<WizardOptionValue> options,
        string? answer = null)
    {
        var category = WizardStepClassifier.Categorize(stepType, options.Count > 0);

        // Note/acknowledge steps carry no user input, so their request timeout is only
        // a ceiling -- a fast reply still returns immediately. Acking a note can trigger
        // heavy backend work before the gateway emits the next step: the finalize
        // "Security" note is followed by Windows shell-completion cache generation
        // (Node cold start + PowerShell $PROFILE write), which can exceed the 30s default.
        // Give acknowledgements the slow ceiling so post-ack work cannot trip the timeout.
        if (category == WizardStepCategory.Acknowledge)
            return SlowStepTimeoutMs;

        IReadOnlyCollection<WizardOptionValue>? selectedOptions =
            category == WizardStepCategory.RequiresAnswer && options.Count > 0 ? [] : null;

        if (selectedOptions is not null && !string.IsNullOrWhiteSpace(answer))
        {
            if (string.Equals(stepType, "multiselect", StringComparison.OrdinalIgnoreCase)
                && WizardAnswerBuilder.TryResolveOptions(options, answer, out var multiselectOptions))
            {
                selectedOptions = multiselectOptions;
            }
            else if (!string.Equals(stepType, "multiselect", StringComparison.OrdinalIgnoreCase)
                && WizardAnswerBuilder.TryFindOption(options, answer, out var selectedOption))
            {
                selectedOptions = [selectedOption];
            }
        }

        return ForStep(title, message, stepId, selectedOptions);
    }

    private static string JoinOptionText(IEnumerable<WizardOptionValue> options)
    {
        var parts = new List<string?>();
        foreach (var option in options)
        {
            parts.Add(option.Value);
            parts.Add(option.Label);
            parts.Add(option.Hint);
        }

        return JoinText(parts.ToArray());
    }

    private static string JoinText(params string?[] parts) =>
        string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static bool HasAnyHint(string text, IEnumerable<string> hints) =>
        hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
