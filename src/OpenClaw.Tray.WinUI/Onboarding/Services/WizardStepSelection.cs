namespace OpenClawTray.Onboarding.Services;

public static class WizardStepSelection
{
    public static bool RequiresSelection(string stepType) => stepType is "select" or "multiselect";

    public static int SelectedIndex(string stepInput, IReadOnlyList<string> optionValues)
    {
        for (var i = 0; i < optionValues.Count; i++)
        {
            if (optionValues[i] == stepInput)
                return i;
        }

        return -1;
    }

    public static bool HasValidSelection(string stepType, string stepInput, IReadOnlyCollection<string> optionValues)
    {
        if (stepType == "select")
            return optionValues.Contains(stepInput);

        if (stepType == "multiselect")
        {
            var selected = SplitMultiSelectValues(stepInput);
            return selected.Length > 0 && selected.All(optionValues.Contains);
        }

        return true;
    }

    public static bool ShouldDisableContinue(string stepType, string stepInput, IReadOnlyCollection<string> optionValues) =>
        RequiresSelection(stepType) && !HasValidSelection(stepType, stepInput, optionValues);

    public static bool TryBuildAnswerValue(string stepType, string stepInput, IReadOnlyCollection<string> optionValues, out string answerValue)
    {
        if (RequiresSelection(stepType) && !HasValidSelection(stepType, stepInput, optionValues))
        {
            answerValue = "";
            return false;
        }

        answerValue = string.IsNullOrEmpty(stepInput) ? "true" : stepInput;
        return true;
    }

    private static string[] SplitMultiSelectValues(string stepInput) =>
        stepInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
