using System.Text.Json;
using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class WizardStepParsingTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    #region Done flag

    [Fact]
    public void Parse_DoneTrue_IsDone()
    {
        var payload = Parse("""{"done": true, "sessionId": "s1"}""");
        var step = WizardStepParser.Parse(payload);

        Assert.True(step.IsDone);
        Assert.Null(step.Error);
    }

    [Fact]
    public void Parse_DoneFalse_NotDone()
    {
        var payload = Parse("""{"done": false, "step": {"type":"note","title":"T","message":"M","id":"1"}, "stepIndex": 0, "totalSteps": 3}""");
        var step = WizardStepParser.Parse(payload);

        Assert.False(step.IsDone);
    }

    #endregion

    #region Step types

    [Theory]
    [InlineData("text")]
    [InlineData("confirm")]
    [InlineData("select")]
    [InlineData("note")]
    public void Parse_StepType_Extracted(string type)
    {
        var json = $$$"""{"step": {"type":"{{{type}}}","title":"T","message":"M","id":"1"}}""";
        var payload = Parse(json);
        var step = WizardStepParser.Parse(payload);

        Assert.Equal(type, step.StepType);
    }

    #endregion

    #region Options — string array

    [Fact]
    public void Parse_StringOptions_PopulatesLabelsAndValues()
    {
        var payload = Parse("""{"step":{"type":"select","title":"T","message":"M","id":"1","options":["A","B","C"]}}""");
        var step = WizardStepParser.Parse(payload);

        Assert.Equal(["A", "B", "C"], step.OptionLabels);
        Assert.Equal(["A", "B", "C"], step.OptionValues);
        Assert.Equal(["", "", ""], step.OptionHints);
    }

    #endregion

    #region Options — object array

    [Fact]
    public void Parse_ObjectOptions_LabelsIncludeHint()
    {
        var payload = Parse("""{"step":{"type":"select","title":"T","message":"M","id":"1","options":[{"value":"v1","label":"Label1","hint":"Hint1"},{"value":"v2","label":"Label2"}]}}""");
        var step = WizardStepParser.Parse(payload);

        Assert.Equal(["Label1 — Hint1", "Label2"], step.OptionLabels);
        Assert.Equal(["v1", "v2"], step.OptionValues);
        Assert.Equal(["Hint1", ""], step.OptionHints);
    }

    #endregion

    #region No options

    [Fact]
    public void Parse_NoOptions_EmptyArrays()
    {
        var payload = Parse("""{"step":{"type":"text","title":"T","message":"M","id":"1"}}""");
        var step = WizardStepParser.Parse(payload);

        Assert.Empty(step.OptionLabels);
        Assert.Empty(step.OptionValues);
    }

    #endregion

    #region Sensitive and initialValue

    [Fact]
    public void Parse_SensitiveTrue_SensitiveSet()
    {
        var payload = Parse("""{"step":{"type":"text","title":"T","message":"M","id":"1","sensitive":true}}""");
        var step = WizardStepParser.Parse(payload);

        Assert.True(step.Sensitive);
    }

    [Fact]
    public void Parse_InitialValue_Extracted()
    {
        var payload = Parse("""{"step":{"type":"text","title":"T","message":"M","id":"1","initialValue":"hello"}}""");
        var step = WizardStepParser.Parse(payload);

        Assert.Equal("hello", step.InitialValue);
    }

    #endregion

    #region SessionId and step/total

    [Fact]
    public void Parse_SessionId_Extracted()
    {
        var payload = Parse("""{"sessionId":"abc-123","step":{"type":"note","title":"T","message":"M","id":"1"},"stepIndex":2,"totalSteps":5}""");
        var step = WizardStepParser.Parse(payload);

        Assert.Equal("abc-123", step.SessionId);
        Assert.Equal(2, step.StepNumber);
        Assert.Equal(5, step.TotalSteps);
    }

    #endregion

    #region Missing step property

    [Fact]
    public void Parse_MissingStep_ReturnsError()
    {
        var payload = Parse("""{"sessionId":"s1"}""");
        var step = WizardStepParser.Parse(payload);

        Assert.NotNull(step.Error);
        Assert.Contains("step", step.Error, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Null/undefined payload

    [Fact]
    public void Parse_DefaultJsonElement_ReturnsError()
    {
        var step = WizardStepParser.Parse(default);

        Assert.NotNull(step.Error);
        Assert.Contains("Empty", step.Error);
    }

    #endregion

    #region Title fallback

    [Fact]
    public void Parse_EmptyTitle_FallsBackFromType()
    {
        var payload = Parse("""{"step":{"type":"confirm","title":"","message":"Do you want to continue?","id":"1"}}""");
        var step = WizardStepParser.Parse(payload);

        Assert.Equal("Confirm", step.Title);
    }

    #endregion
}
