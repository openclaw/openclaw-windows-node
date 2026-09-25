namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Pins the wiring between the pure terminal-payload decision and the best-effort
/// wizard.cancel suppression so a future edit cannot mark the wizard completed on a
/// path the decision rejected, or cancel a wizard that already finished.
/// </summary>
public sealed class SetupWizardTerminalCompletionContractTests
{
    [Fact]
    public void TerminalPayloadErrors_AreClassifiedOnlyByTheDecisionSeam()
    {
        var source = RunnerSource();

        Assert.Contains(
            "wizardCompleted = decision.MarksWizardCompleted;",
            source,
            StringComparison.Ordinal);

        // Only the plain terminal-done path and the exact 2026.7.1 terminal-restart
        // recovery may hard-set completion; terminal errors must go through the
        // decision seam so the accepted and rejected paths stay testable.
        Assert.Equal(2, CountOccurrences(source, "wizardCompleted = true;"));
    }

    [Fact]
    public void BestEffortCancel_StaysGatedOnWizardCompleted()
    {
        Assert.Contains(
            "if (client is not null && wizardStarted && !wizardCompleted && !string.IsNullOrWhiteSpace(sessionId))",
            RunnerSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiDonePayload_UsesDecideTerminalWizardError()
    {
        var source = WizardPageSource();
        var apply = ExtractMethod(source, "ApplyPayloadAsync");
        var start = ExtractMethod(source, "StartWizardAsync");
        var sendAnswer = ExtractMethod(source, "SendCurrentAnswerAsync");
        var sendOption = ExtractMethod(source, "SendOptionValueAsync");
        var expandMore = ExtractMethod(source, "ExpandMoreOptionsAsync");

        Assert.Contains("new WizardFinalStepTracker()", source, StringComparison.Ordinal);
        Assert.Contains(
            "SetupWizardRunner.DecideTerminalWizardError(",
            apply,
            StringComparison.Ordinal);
        Assert.Contains("_finalStepTracker.AnsweredFinalStep", apply, StringComparison.Ordinal);
        Assert.Contains("decision.MarksWizardCompleted", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("this.prompt is not a function", apply, StringComparison.Ordinal);
        AssertInOrder(
            apply,
            "_finalStepTracker.RecordProgressAcknowledgement();",
            "SendWizardRequestAsync(");
        Assert.Contains("_finalStepTracker.ResetForNewSession();", start, StringComparison.Ordinal);
        AssertRecordsAnswerBeforeNext(sendAnswer);
        AssertRecordsAnswerBeforeNext(sendOption);
        AssertRecordsAnswerBeforeNext(expandMore);
    }

    [Fact]
    public void FinalStepTracking_IsOwnedByTheTrackerSeam()
    {
        var source = RunnerSource();

        Assert.Contains("new WizardFinalStepTracker()", source, StringComparison.Ordinal);
        Assert.Contains(
            "finalStepTracker.RecordAnsweredStep(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "finalStepTracker.RecordProgressAcknowledgement();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "finalStepTracker.ResetForNewSession();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "finalStepTracker.AnsweredFinalStep);",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertRecordsAnswerBeforeNext(string method)
    {
        AssertInOrder(
            method,
            "_finalStepTracker.RecordAnsweredStep(",
            "SendWizardRequestAsync(");
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var current = -1;
        foreach (var marker in markers)
        {
            var next = source.IndexOf(marker, current + 1, StringComparison.Ordinal);
            Assert.True(next >= 0, $"Could not find marker after index {current}: {marker}");
            current = next;
        }
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var signature = source.IndexOf($"async Task {methodName}(", StringComparison.Ordinal);
        Assert.True(signature >= 0, $"Could not find method {methodName}.");
        var brace = source.IndexOf('{', signature);
        Assert.True(brace >= 0, $"Could not find body for method {methodName}.");

        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(signature, index - signature + 1);
            }
        }

        throw new InvalidOperationException($"Could not extract method {methodName}.");
    }

    private static string WizardPageSource() =>
        File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "OpenClaw.SetupEngine.UI",
                "Pages",
                "WizardPage.xaml.cs"));

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string RunnerSource() =>
        File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "OpenClaw.SetupEngine",
                "SetupWizardRunner.cs"));

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "openclaw-windows-node.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
