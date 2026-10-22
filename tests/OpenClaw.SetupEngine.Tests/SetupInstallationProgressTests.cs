namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupInstallationProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryActualStep_HasAnExplicitPhaseInPipelineOrder(bool recovery)
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(recovery);
        var phases = steps.Select(step => SetupInstallationProgress.PhaseFor(step.Id, recovery)).ToArray();
        Assert.Equal(phases.Order(), phases);
        var progress = new SetupInstallationProgress(steps, recovery);
        Assert.Equal(steps.Count, progress.TotalSteps);
        Assert.Equal(0, progress.CompletedSteps);
        Assert.All(progress.Phases, phase => Assert.Equal(SetupInstallationStatus.Pending, phase.Status));
        foreach (var step in steps)
        {
            progress.Apply(new(step.Id, step.DisplayName, null, null));
            Assert.Equal(step.DisplayName, progress.CurrentActivity);
            Assert.True(progress.IsRunning);
            progress.Apply(new(step.Id, step.DisplayName, StepOutcome.Success, TimeSpan.Zero));
        }
        Assert.Equal(steps.Count, progress.CompletedSteps);
        Assert.Null(progress.CurrentActivity);
        Assert.False(progress.IsRunning);
        Assert.All(progress.Phases, phase => Assert.Equal(SetupInstallationStatus.Complete, phase.Status));
    }

    [Fact]
    public void AllFactorySteps_IncludingProofAndLegacyWizardHaveAnOwner()
    {
        var steps = SetupStepFactory.BuildDefaultSteps().Concat(SetupStepFactory.BuildLocalAiRecoverySteps())
            .Concat(SetupStepFactory.BuildLocalAiInferenceProofSteps()).Concat(SetupStepFactory.BuildWizardOnlySteps());
        foreach (var step in steps)
            Assert.True(Enum.IsDefined(SetupInstallationProgress.PhaseFor(step.Id, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupInstallationProgress.PhaseFor("future-step", false));
    }

    [Fact]
    public void SkippedSteps_CountAsFinishedWithoutClaimingTheyWereInstalled()
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
        var progress = new SetupInstallationProgress(steps, false);
        foreach (var step in steps)
            progress.Apply(new(step.Id, step.DisplayName, StepOutcome.Skipped, null));
        Assert.Equal(steps.Count, progress.CompletedSteps);
        Assert.All(progress.Phases, phase => Assert.Equal(SetupInstallationStatus.Skipped, phase.Status));
        progress.Apply(new(steps[0].Id, steps[0].DisplayName, StepOutcome.Success, null));
        Assert.Equal(SetupInstallationStatus.Complete, progress.Phases[0].Status);
    }

    [Theory]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.FailedTerminal)]
    public void Failure_TakesPriorityOverRunningAndCompletedWork(StepOutcome outcome)
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
        var progress = new SetupInstallationProgress(steps, false);
        progress.Apply(new(steps[0].Id, steps[0].DisplayName, StepOutcome.Success, null));
        progress.Apply(new(steps[1].Id, steps[1].DisplayName, outcome, null));
        progress.Apply(new(steps[2].Id, steps[2].DisplayName, null, null));
        Assert.Equal(SetupInstallationStatus.Failed, progress.Phases[0].Status);
        Assert.Equal(steps[1].DisplayName, progress.CurrentActivity);
        Assert.False(progress.IsRunning);
        Assert.Equal(1, progress.CompletedSteps);
    }

    [Fact]
    public void ForeignEventsAndDuplicateSteps_AreRejected()
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
        var progress = new SetupInstallationProgress(steps, false);
        Assert.Throws<ArgumentException>(() => progress.Apply(new("restart-gateway", "Foreign", null, null)));
        Assert.Throws<ArgumentException>(() => new SetupInstallationProgress([steps[0], steps[0]], false));
        Assert.Equal(0, progress.CompletedSteps);
    }

    [Fact]
    public void Cancellation_StopsTheCurrentPhaseWithoutCompletingPendingSteps()
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
        var progress = new SetupInstallationProgress(steps, false);
        progress.Apply(new(steps[0].Id, steps[0].DisplayName, StepOutcome.Success, null));
        progress.Apply(new(steps[1].Id, steps[1].DisplayName, null, null));
        progress.Cancel();
        Assert.Equal(SetupInstallationStatus.Cancelled, progress.Phases[0].Status);
        Assert.False(progress.IsRunning);
        Assert.Equal(steps[1].DisplayName, progress.CurrentActivity);
        Assert.Equal(1, progress.CompletedSteps);
        Assert.All(progress.Phases.Skip(1), phase => Assert.Equal(SetupInstallationStatus.Pending, phase.Status));
    }

    [Fact]
    public void Cancellation_BetweenStepsDoesNotOverwriteFailureOrSuccess()
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
        var progress = new SetupInstallationProgress(steps, false);
        progress.Apply(new(steps[0].Id, steps[0].DisplayName, StepOutcome.Failed, null));
        progress.Cancel();
        Assert.Equal(SetupInstallationStatus.Failed, progress.Phases[0].Status);
        Assert.Equal(steps[0].DisplayName, progress.CurrentActivity);
        var complete = new SetupInstallationProgress(steps, false);
        foreach (var step in steps) complete.Apply(new(step.Id, step.DisplayName, StepOutcome.Success, null));
        complete.Cancel();
        Assert.All(complete.Phases, phase => Assert.Equal(SetupInstallationStatus.Complete, phase.Status));
    }

    [Fact]
    public void Recovery_PreparesExistingGatewayThenLocalAiWithoutGatewayInstallClaim()
    {
        Assert.Equal(SetupInstallationPhase.Prepare, SetupInstallationProgress.PhaseFor("preserve-local-ai-recovery-gateway", true));
        Assert.Equal(SetupInstallationPhase.Install, SetupInstallationProgress.PhaseFor("acquire-local-ai-model", true));
        Assert.Equal(SetupInstallationPhase.Connect, SetupInstallationProgress.PhaseFor("restart-gateway", true));
    }
}
