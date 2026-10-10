using OpenClaw.TestSupport;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WizardRecoveryTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterFailureWritesOnlyActionableStepsAndRedactsSensitiveValues(bool sensitiveIsMissing)
    {
        using var temp = new TempDirectory();
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        using var journal = new TransactionJournal(filePath: null);
        var configured = new Dictionary<string, string> { ["credential"] = "fixture-private-answer" };
        var context = new SetupContext(
            new SetupConfig { LogPath = Path.Combine(temp.Path, "setup.jsonl"), WizardAnswers = configured },
            logger, journal, new CommandRunner(logger), CancellationToken.None,
            dataDir: temp.Path, localDataDir: temp.Path);
        var runner = new SetupWizardRunner(context);
        var steps = new List<SetupWizardRunner.WizardTemplateStep>();

        Record(steps, """{"id":"note","type":"note","title":"QuickStart"}""");
        Record(steps, """
            {"id":"search","type":"select","message":"Search provider","options":[{"value":"skip"}]}
            """);
        Record(steps, """{"id":"service","type":"confirm","message":"Install Gateway service?"}""");
        Record(steps, """
            {"id":"provider","type":"select","title":"AI provider","initialValue":"local",
             "options":[{"value":"local"},{"value":"remote"}]}
            """);
        var sensitive = Record(steps, """
            {"id":"credential","type":"text","title":"API credential","sensitive":true,
             "initialValue":"fixture-private-initial"}
            """);
        var missing = sensitiveIsMissing ? sensitive :
            Record(steps, """{"id":"required","type":"text","title":"Required value"}""");
        var resolution = SetupWizardRunner.ResolveOnboardingAnswer(
            missing, new WizardOnboardingDecision(WizardOnboardingAction.Show), null);
        // A missing sensitive prompt must not inherit a real initial value in the recovery artifact.
        if (!sensitiveIsMissing)
            Assert.False(resolution.Success);
        var path = runner.WriteAnswerTemplate(steps, missing);

        Assert.Equal(Path.Combine(temp.Path, "setup.wizard-answers.template.json"), path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("fixture-private-initial", text);
        Assert.DoesNotContain("fixture-private-answer", text);
        using var artifact = JsonDocument.Parse(text);
        var answers = artifact.RootElement.GetProperty("WizardAnswers");
        Assert.Equal("local", answers.GetProperty("ai-provider").GetString());
        Assert.Equal("<sensitive value>", answers.GetProperty("api-credential").GetString());
        Assert.Equal(sensitiveIsMissing ? 2 : 3, answers.EnumerateObject().Count());
        if (!sensitiveIsMissing)
            Assert.Equal("<text value>", answers.GetProperty("required-value").GetString());
        var recorded = artifact.RootElement.GetProperty("Steps").EnumerateArray().ToArray();
        Assert.Equal(answers.EnumerateObject().Count(), recorded.Length);
        Assert.DoesNotContain(recorded, step => new[] { "note", "search", "service" }
            .Contains(step.GetProperty("StepId").GetString()));
        Assert.Equal("<sensitive value>", recorded.Single(step =>
            step.GetProperty("StepId").GetString() == "credential").GetProperty("SuggestedAnswer").GetString());
    }

    [Fact]
    public void PolicyConflictArtifactExplainsRemovalWithoutRecreatingManagedOverride()
    {
        using var temp = new TempDirectory();
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        using var journal = new TransactionJournal(filePath: null);
        var context = new SetupContext(new SetupConfig(), logger, journal,
            new CommandRunner(logger), CancellationToken.None, dataDir: temp.Path, localDataDir: temp.Path);
        var runner = new SetupWizardRunner(context);
        var steps = new List<SetupWizardRunner.WizardTemplateStep>();
        var conflict = Record(steps, """
            {"id":"search","type":"select","message":"Search provider","options":[{"value":"skip"}]}
            """);
        var result = SetupWizardRunner.ResolveOnboardingAnswer(conflict,
            new WizardOnboardingDecision(WizardOnboardingAction.Answer, "skip"),
            new Dictionary<string, string> { ["search"] = "fixture-private-conflict" });
        Assert.False(result.Success);
        var text = File.ReadAllText(runner.WriteAnswerTemplate(steps, conflict));
        Assert.DoesNotContain("fixture-private-conflict", text);
        using var artifact = JsonDocument.Parse(text);
        Assert.Empty(artifact.RootElement.GetProperty("WizardAnswers").EnumerateObject());
        Assert.Empty(artifact.RootElement.GetProperty("Steps").EnumerateArray());
        Assert.Contains("remove that entry from your original configuration",
            artifact.RootElement.GetProperty("_instructions").GetString());
    }

    private static SetupWizardRunner.WizardPayload Record(
        List<SetupWizardRunner.WizardTemplateStep> steps, string json)
    {
        var step = JsonSerializer.Deserialize<JsonElement>(json);
        var parsed = SetupWizardRunner.WizardPayload.Parse(JsonSerializer.SerializeToElement(new { step }));
        var policy = WizardOnboardingPolicy.Evaluate(step);
        SetupWizardRunner.RecordActionableTemplateStep(steps, parsed, policy);
        return parsed;
    }
}
