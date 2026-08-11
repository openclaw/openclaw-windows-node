namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Sequencing proof for the flag that gates terminal hosted-TUI acceptance. The wizard
/// only completes on a SIGTERM that immediately follows the authoritative final step, so
/// these tests replay the transitions the runner performs.
/// </summary>
public sealed class WizardFinalStepTrackerTests
{
    private const string SigtermError = "Error: TUI exited from signal SIGTERM";

    [Fact]
    public void FreshSession_DoesNotAcceptTerminalSigterm()
    {
        var tracker = new WizardFinalStepTracker();

        Assert.False(tracker.AnsweredFinalStep);
        Assert.False(Accepts(tracker));
    }

    [Fact]
    public void FinalDoneNoteAfterEarlierSteps_AcceptsTerminalSigterm()
    {
        var tracker = new WizardFinalStepTracker();

        AnswerNote(tracker, "web-search", "Web search");
        AnswerNote(tracker, "what-now", "What now");
        AnswerNote(tracker, "5c1c6f22-2f4f-4b0a-9d0e-2c0a6f1a7c31", "Done");

        Assert.True(tracker.AnsweredFinalStep);
        Assert.True(Accepts(tracker));
    }

    [Fact]
    public void EarlierStep_KeepsTerminalSigtermFatal()
    {
        var tracker = new WizardFinalStepTracker();

        AnswerNote(tracker, "5c1c6f22-2f4f-4b0a-9d0e-2c0a6f1a7c31", "Done");
        AnswerNote(tracker, "security-disclaimer", "Security disclaimer");

        Assert.False(tracker.AnsweredFinalStep);
        Assert.False(Accepts(tracker));
    }

    [Fact]
    public void ProgressPollAfterFinalStep_KeepsTerminalSigtermFatal()
    {
        var tracker = new WizardFinalStepTracker();

        AnswerNote(tracker, "done", "Done");
        tracker.RecordProgressAcknowledgement();

        Assert.False(tracker.AnsweredFinalStep);
        Assert.False(Accepts(tracker));
    }

    [Fact]
    public void WizardReplayAfterFinalStep_KeepsTerminalSigtermFatal()
    {
        var tracker = new WizardFinalStepTracker();

        AnswerNote(tracker, "done", "Done");
        tracker.ResetForNewSession();

        Assert.False(tracker.AnsweredFinalStep);
        Assert.False(Accepts(tracker));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("confirm")]
    [InlineData("select")]
    [InlineData("progress")]
    public void NonAcknowledgementStepNamedDone_KeepsTerminalSigtermFatal(string stepType)
    {
        var tracker = new WizardFinalStepTracker();

        tracker.RecordAnsweredStep(
            stepType,
            "done",
            "Done",
            hasOptions: false,
            stepIndex: 0,
            totalSteps: 0);

        Assert.False(tracker.AnsweredFinalStep);
        Assert.False(Accepts(tracker));
    }

    [Fact]
    public void RepeatedFinalStep_StillRequiresTheExactTerminalMessage()
    {
        var tracker = new WizardFinalStepTracker();

        AnswerNote(tracker, "done", "Done");
        AnswerNote(tracker, "done", "Done");

        Assert.True(tracker.AnsweredFinalStep);
        Assert.False(
            GatewayWizardRestartRecoveryPolicy.IsHostedWizardTerminationAfterFinalStep(
                payloadIsTerminal: true,
                "Error: TUI exited from signal SIGHUP",
                tracker.AnsweredFinalStep));
    }

    private static void AnswerNote(
        WizardFinalStepTracker tracker,
        string stepId,
        string title) =>
        tracker.RecordAnsweredStep(
            "note",
            stepId,
            title,
            hasOptions: false,
            stepIndex: 0,
            totalSteps: 0);

    private static bool Accepts(WizardFinalStepTracker tracker) =>
        GatewayWizardRestartRecoveryPolicy.IsHostedWizardTerminationAfterFinalStep(
            payloadIsTerminal: true,
            SigtermError,
            tracker.AnsweredFinalStep);
}
