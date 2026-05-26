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
}
