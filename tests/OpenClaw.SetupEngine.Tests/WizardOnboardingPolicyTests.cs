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

    [Theory]
    [InlineData("Search provider", "skip", "brave")]
    [InlineData("Select channel (QuickStart)", "__skip__", "teams")]
    [InlineData("Config handling", "keep", "reset")]
    [InlineData("Setup mode", "keep-model", "quickstart")]
    [InlineData("Onboarding mode", "quickstart", "advanced")]
    [InlineData("Gateway service already installed", "skip", "restart")]
    public void ManagedChoicesAcceptMatchingOrAbsentAnswersButRejectOverrides(
        string message, string managed, string conflicting)
    {
        var step = JsonSerializer.SerializeToElement(new
        {
            id = "random", type = "select", message,
            options = new[] { new { value = managed }, new { value = conflicting } }
        });
        Assert.Equal(managed, Resolve(step).Answer);
        Assert.Equal(managed, Resolve(step, managed).Answer);
        Assert.Equal(managed, Resolve(step, JsonSerializer.Serialize(managed)).Answer);
        AssertConflict(Resolve(step, conflicting), conflicting);
        AssertConflict(Resolve(step, "not-an-option"), "not-an-option");
    }

    [Theory]
    [InlineData("Configure skills now? (recommended)")]
    [InlineData("Install Gateway service?")]
    public void ManagedConfirmationsDoNotPermitExplicitInstallation(string message)
    {
        var step = JsonSerializer.SerializeToElement(new { id = "random", type = "confirm", message });
        Assert.Equal("false", Resolve(step).Answer);
        Assert.Equal("false", Resolve(step, "FALSE").Answer);
        AssertConflict(Resolve(step, "true"), null);
        AssertConflict(Resolve(step, "secret-configured-value"), "secret-configured-value");
    }

    [Theory]
    [InlineData(true, "__skip__")]
    [InlineData(false, "[]")]
    public void ManagedDependenciesCompareWireValues(bool offersSkip, string expected)
    {
        var step = JsonSerializer.SerializeToElement(new
        {
            id = "random", type = "multiselect", message = "Install missing skill dependencies",
            options = offersSkip ? new[] { new { value = "__skip__" }, new { value = "install-all" } }
                : new[] { new { value = "install-all" } }
        });
        Assert.Equal(expected, Resolve(step).Answer);
        Assert.True(Resolve(step, expected).Success);
        if (offersSkip)
            Assert.True(Resolve(step, """["__skip__"]""").Success);
        AssertConflict(Resolve(step, "install-all"), "install-all");
    }

    [Theory]
    [InlineData("QuickStart")]
    [InlineData("Optional apps")]
    public void ManagedNotesAllowAcknowledgementButRejectContradictions(string title)
    {
        var step = JsonSerializer.SerializeToElement(new { id = "random", type = "note", title });
        Assert.False(Resolve(step).HasAnswer);
        Assert.True(Resolve(step, "true").Success);
        AssertConflict(Resolve(step, "false"), null);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("SEARCH-PROVIDER")]
    [InlineData("Search provider")]
    public void ConflictDetectionUsesExistingAnswerKeyMatching(string key)
    {
        var step = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"random","type":"select","message":"Search provider",
             "options":[{"value":"skip"},{"value":"brave"}]}
            """);
        AssertConflict(Resolve(step, "brave", key), "brave");
    }

    [Fact]
    public void UnmanagedChoicesStillHonorAndValidateExplicitAnswers()
    {
        var step = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"random","type":"select","message":"Model / auth provider",
             "options":[{"value":"skip"},{"value":"openai"}]}
            """);
        Assert.Equal("openai", Resolve(step, "openai").Answer);
        Assert.False(Resolve(step, "missing").Success);
    }

    private static SetupWizardRunner.AnswerResolution Resolve(
        JsonElement step, string? answer = null, string key = "random")
    {
        var payload = SetupWizardRunner.WizardPayload.Parse(
            JsonSerializer.SerializeToElement(new { step }));
        return SetupWizardRunner.ResolveOnboardingAnswer(payload, WizardOnboardingPolicy.Evaluate(step),
            answer is null ? null : new Dictionary<string, string> { [key] = answer });
    }

    private static void AssertConflict(SetupWizardRunner.AnswerResolution result, string? configuredValue)
    {
        Assert.False(result.Success);
        Assert.False(result.HasAnswer);
        Assert.Contains("conflicts with Companion onboarding policy", result.Error);
        Assert.Contains("Remove the conflicting entry", result.Error);
        if (configuredValue is not null)
            Assert.DoesNotContain(configuredValue, result.Error);
    }
}
