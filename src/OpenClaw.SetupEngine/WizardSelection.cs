namespace OpenClaw.SetupEngine;

public static class WizardSelection
{
    public static bool RequiresSelection(string stepType) => stepType is "select" or "multiselect";
    public static bool RequiresAnswer(string stepType) => RequiresSelection(stepType) || stepType == "text";

    public static bool HasSelectableOptions(string stepType, IReadOnlyCollection<string> optionValues) =>
        !RequiresSelection(stepType) || optionValues.Any(value => !string.IsNullOrWhiteSpace(value));

    public static int SelectedIndex(string? stepInput, IReadOnlyList<string> optionValues)
    {
        if (string.IsNullOrEmpty(stepInput))
            return -1;

        for (var i = 0; i < optionValues.Count; i++)
        {
            if (optionValues[i] == stepInput)
                return i;
        }

        return -1;
    }

    public static bool HasValidSelection(string stepType, IReadOnlyCollection<string> selectedValues, IReadOnlyCollection<string> optionValues)
    {
        if (!HasSelectableOptions(stepType, optionValues))
            return false;

        if (stepType == "select")
            return selectedValues.Count == 1 && optionValues.Contains(selectedValues.First());

        if (stepType == "multiselect")
            return selectedValues.Count > 0 && selectedValues.All(optionValues.Contains);

        return true;
    }

    public static bool ShouldDisableContinue(string stepType, IReadOnlyCollection<string> selectedValues, IReadOnlyCollection<string> optionValues) =>
        RequiresSelection(stepType) && !HasValidSelection(stepType, selectedValues, optionValues);

    public static bool ShouldDisableContinue(string stepType, string? textInput) =>
        stepType == "text" && string.IsNullOrWhiteSpace(textInput);

    public static string? PreferredDesktopSelectAnswer(
        IReadOnlyList<WizardOptionValue> options,
        string? initialValue,
        string? title = null,
        string? message = null,
        string? stepId = null)
        => DesktopAutoSelectAnswer(options, title, message, stepId)
            ?? (string.IsNullOrWhiteSpace(initialValue) ? null : initialValue);

    public static string? DesktopAutoSelectAnswer(
        IReadOnlyList<WizardOptionValue> options,
        string? title = null,
        string? message = null,
        string? stepId = null)
    {
        // The desktop wizard is RPC-driven and cannot host the terminal TUI that
        // the gateway CLI recommends by default. Prefer the explicit deferred
        // hatch path whenever the gateway offers it.
        if (options.Any(option => string.Equals(option.Value, "tui", StringComparison.Ordinal))
            && options.Any(option => string.Equals(option.Value, "later", StringComparison.Ordinal)))
        {
            return "later";
        }

        var promptText = $"{title} {message} {stepId}";
        if (promptText.Contains("gateway service", StringComparison.OrdinalIgnoreCase)
            && promptText.Contains("already installed", StringComparison.OrdinalIgnoreCase)
            && options.Any(option => string.Equals(option.Value, "skip", StringComparison.OrdinalIgnoreCase)))
        {
            return "skip";
        }

        return null;
    }
}
