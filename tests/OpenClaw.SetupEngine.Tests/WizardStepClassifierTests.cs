namespace OpenClaw.SetupEngine.Tests;

public class WizardStepClassifierTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("select")]
    [InlineData("multiselect")]
    public void InteractiveTypes_RequireAnswer(string type)
    {
        Assert.Equal(WizardStepCategory.RequiresAnswer, WizardStepClassifier.Categorize(type, hasOptions: false));
    }

    [Fact]
    public void Confirm_IsConfirm()
    {
        Assert.Equal(WizardStepCategory.Confirm, WizardStepClassifier.Categorize("confirm", hasOptions: false));
    }

    [Fact]
    public void Note_IsAcknowledge()
    {
        Assert.Equal(WizardStepCategory.Acknowledge, WizardStepClassifier.Categorize("note", hasOptions: false));
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("status")]
    [InlineData("working")]
    [InlineData("PROGRESS")]
    [InlineData("  Progress ")]
    public void ProgressTypes_AreProgress(string type)
    {
        Assert.True(WizardStepClassifier.IsProgress(type));
        Assert.Equal(WizardStepCategory.Progress, WizardStepClassifier.Categorize(type, hasOptions: false));
    }

    [Fact]
    public void ProgressWithOptions_StillProgress()
    {
        // A status step that happens to carry options is still a poll-through.
        Assert.Equal(WizardStepCategory.Progress, WizardStepClassifier.Categorize("progress", hasOptions: true));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("info")]
    [InlineData("future-thing")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownWithoutOptions_IsNonInteractive(string? type)
    {
        Assert.Equal(WizardStepCategory.NonInteractive, WizardStepClassifier.Categorize(type, hasOptions: false));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("future-choice")]
    public void UnknownWithOptions_IsTreatedAsChoice(string type)
    {
        Assert.Equal(WizardStepCategory.RequiresAnswer, WizardStepClassifier.Categorize(type, hasOptions: true));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("SELECT")]
    [InlineData("Confirm")]
    [InlineData("NOTE")]
    public void Categorization_IsCaseInsensitive(string type)
    {
        // Should not fall through to NonInteractive just because of casing.
        Assert.NotEqual(WizardStepCategory.NonInteractive, WizardStepClassifier.Categorize(type, hasOptions: false));
    }

    [Theory]
    [InlineData(WizardStepCategory.Progress, true)]
    [InlineData(WizardStepCategory.NonInteractive, true)]
    [InlineData(WizardStepCategory.RequiresAnswer, false)]
    [InlineData(WizardStepCategory.Confirm, false)]
    [InlineData(WizardStepCategory.Acknowledge, false)]
    public void ContinuesWithoutAnswer_OnlyForDriveThrough(WizardStepCategory category, bool expected)
    {
        Assert.Equal(expected, WizardStepClassifier.ContinuesWithoutAnswer(category));
    }
}
