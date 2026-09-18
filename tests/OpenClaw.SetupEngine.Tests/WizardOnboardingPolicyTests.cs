using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WizardOnboardingPolicyTests
{
    [Theory]
    [InlineData("Existing config detected")]
    [InlineData("QuickStart")]
    [InlineData("Model check")]
    [InlineData("How channels work")]
    [InlineData("Web search")]
    [InlineData("Skills status")]
    [InlineData("Gateway")]
    public void AuditedNotesAreAcknowledgedWithoutRendering(string title)
    {
        var step = JsonSerializer.SerializeToElement(new { id = "random", type = "note", title });
        Assert.Equal(WizardOnboardingAction.Acknowledge, WizardOnboardingPolicy.Evaluate(step).Action);
    }

    [Theory]
    [InlineData("Security")]
    [InlineData("Telemetry")]
    [InlineData("Invalid config")]
    [InlineData("Model / auth provider")]
    [InlineData("OAuth")]
    [InlineData("New upstream prompt")]
    public void ConsentAuthenticationErrorsAndUnknownNotesRemainVisible(string title)
    {
        var step = JsonSerializer.SerializeToElement(new { type = "note", title });
        Assert.Equal(WizardOnboardingAction.Show, WizardOnboardingPolicy.Evaluate(step).Action);
    }

    [Theory]
    [InlineData("Select channel (QuickStart)", "skip")]
    [InlineData("Search provider", "skip")]
    [InlineData("Config handling", "keep")]
    [InlineData("Onboarding mode", "quickstart")]
    public void ChoiceDefaultsUseAuditedValuesNotOptionPosition(string message, string expected)
    {
        var step = JsonSerializer.SerializeToElement(new
        {
            type = "select", message,
            options = new[] { new { value = "destructive", label = "Reset everything" },
                new { value = expected, label = "Audited option" } }
        });
        var decision = WizardOnboardingPolicy.Evaluate(step);
        Assert.Equal(WizardOnboardingAction.Answer, decision.Action);
        Assert.Equal(expected, decision.Answer);
    }

    [Fact]
    public void ExistingModelConfigurationIsKeptInsteadOfReconfigured()
    {
        var step = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"select","message":"Setup mode","options":[
              {"value":"quickstart","label":"QuickStart"},
              {"value":"keep-model","label":"Keep existing model"}]}
            """);
        Assert.Equal("keep-model", WizardOnboardingPolicy.Evaluate(step).Answer);
    }

    [Theory]
    [InlineData("Configure skills now? (recommended)")]
    [InlineData("Install Gateway service?")]
    public void OptionalInstallConfirmationsAreDeclined(string message)
    {
        var step = JsonSerializer.SerializeToElement(new { type = "confirm", message });
        var decision = WizardOnboardingPolicy.Evaluate(step);
        Assert.Equal("false", decision.Answer);
        Assert.Equal(false, WizardAnswerBuilder.BuildWireValue("confirm", decision.Answer!, []));
    }

    [Fact]
    public void SkillDependencySkipUsesTheProtocolSentinel()
    {
        var step = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"multiselect","message":"Install missing skill dependencies","options":[
              {"value":"install-all","label":"All"},
              {"value":"__skip__","label":"Skip for now"}]}
            """);
        var decision = WizardOnboardingPolicy.Evaluate(step);
        Assert.Equal(new[] { "__skip__" }, Assert.IsType<string[]>(
            WizardAnswerBuilder.BuildWireValue("multiselect", decision.Answer!, WizardAnswerBuilder.ReadOptions(step))));
    }

    [Theory]
    [InlineData("""{"type":"select","message":"Search provider","options":[{"value":"paid-provider","label":"Use now"}]}""")]
    [InlineData("""{"type":"select","message":"AI provider","options":[{"value":"skip","label":"Skip"}]}""")]
    [InlineData("""{"type":"confirm","message":"I understand the security risks"}""")]
    [InlineData("""{"type":"confirm","message":"Enable telemetry?"}""")]
    [InlineData("""{"type":"note","title":"Optional apps","sensitive":true}""")]
    [InlineData("""{"type":"text","title":"Existing config detected","sensitive":true}""")]
    public void UnknownChoicesConsentAndSensitiveStepsAreNeverAutoAnswered(string json)
    {
        Assert.Equal(WizardOnboardingAction.Show,
            WizardOnboardingPolicy.Evaluate(JsonSerializer.Deserialize<JsonElement>(json)).Action);
    }

    [Fact]
    public void OptionalAppsIsAHandoffNotAnotherAcknowledgement()
    {
        var step = JsonSerializer.SerializeToElement(new { type = "note", title = "Optional apps" });
        Assert.Equal(WizardOnboardingAction.Finish, WizardOnboardingPolicy.Evaluate(step).Action);
    }
}
