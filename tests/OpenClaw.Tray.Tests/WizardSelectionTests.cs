using System.Text.Json;
using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class WizardSelectionTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void SelectWithoutInitialValue_LeavesStepInputEmptyAndNoSelectedIndex()
    {
        var step = WizardStepParser.Parse(Parse("""{"step":{"type":"select","id":"provider","options":["BlueBubbles","Matrix"]}}"""));

        Assert.Equal("", step.InitialValue);
        Assert.Equal(-1, WizardStepSelection.SelectedIndex(step.InitialValue, step.OptionValues));
    }

    [Fact]
    public void SelectWithExplicitInitialValue_UsesMatchingSelectedIndex()
    {
        var step = WizardStepParser.Parse(Parse("""{"step":{"type":"select","id":"provider","initialValue":"Matrix","options":["BlueBubbles","Matrix"]}}"""));

        Assert.Equal("Matrix", step.InitialValue);
        Assert.Equal(1, WizardStepSelection.SelectedIndex(step.InitialValue, step.OptionValues));
    }

    [Fact]
    public void EmptySelectInput_DoesNotBuildTrueOrFirstOptionAnswer()
    {
        var values = new[] { "bluebubbles" };

        var valid = WizardStepSelection.TryBuildAnswerValue("select", "", values, out var answerValue);

        Assert.False(valid);
        Assert.NotEqual("true", answerValue);
        Assert.NotEqual("bluebubbles", answerValue);
    }

    [Theory]
    [InlineData("select", "", true)]
    [InlineData("select", "bogus", true)]
    [InlineData("select", "matrix", false)]
    [InlineData("multiselect", "", true)]
    [InlineData("multiselect", "matrix,bogus", true)]
    [InlineData("multiselect", "matrix,bluebubbles", false)]
    public void ContinueDisabled_ForSelectAndMultiselectInvalidInput(string stepType, string input, bool expectedDisabled)
    {
        var values = new[] { "bluebubbles", "matrix" };

        Assert.Equal(expectedDisabled, WizardStepSelection.ShouldDisableContinue(stepType, input, values));
    }

    [Theory]
    [InlineData("note")]
    [InlineData("confirm")]
    [InlineData("action")]
    public void EmptyAcknowledgeSteps_AllowContinueAndBuildTrueAnswer(string stepType)
    {
        var values = Array.Empty<string>();

        Assert.False(WizardStepSelection.ShouldDisableContinue(stepType, "", values));
        Assert.True(WizardStepSelection.TryBuildAnswerValue(stepType, "", values, out var answerValue));
        Assert.Equal("true", answerValue);
    }
}
