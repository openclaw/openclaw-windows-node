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
