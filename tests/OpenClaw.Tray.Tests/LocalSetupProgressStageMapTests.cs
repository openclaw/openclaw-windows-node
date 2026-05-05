using System;
using System.Linq;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services.LocalGatewaySetup;
using SS = OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap.StageState;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Locks down the stage-list mapping that <see cref="LocalSetupProgressPage"/>
/// renders. Bug 2 (Aaron e2e drive 2026-05-04) showed two distinct symptoms
/// that traced to a single page-binding root cause:
///   1. The stage list never advanced past the first stage even though the
///      engine progressed through 9+ phases.
///   2. <c>FailedRetryable</c> never rendered the error/retry row.
///
/// The root cause was reference-equality in <c>UseState</c> swallowing every
/// state change after the first (the engine raises <c>StateChanged</c> with
/// the same mutating <see cref="LocalGatewaySetupState"/> instance). The page
/// now stores an immutable record snapshot, and the stage-list logic is
/// hosted in this pure helper so the mapping is exhaustively testable
/// without WinUI dependencies.
/// </summary>
public class LocalSetupProgressStageMapTests
{
    // ---------- Stage advancement: every engine phase resolves to a stage ----------

    [Theory]
    [InlineData(LocalGatewaySetupPhase.Preflight, 0)]
    [InlineData(LocalGatewaySetupPhase.EnsureWslEnabled, 0)]
    [InlineData(LocalGatewaySetupPhase.ElevationCheck, 0)]
    [InlineData(LocalGatewaySetupPhase.CreateWslInstance, 1)]
    [InlineData(LocalGatewaySetupPhase.ConfigureWslInstance, 2)]
    [InlineData(LocalGatewaySetupPhase.InstallOpenClawCli, 3)]
    [InlineData(LocalGatewaySetupPhase.PrepareGatewayConfig, 4)]
    [InlineData(LocalGatewaySetupPhase.InstallGatewayService, 4)]
    [InlineData(LocalGatewaySetupPhase.StartGateway, 5)]
    [InlineData(LocalGatewaySetupPhase.WaitForGateway, 5)]
    [InlineData(LocalGatewaySetupPhase.MintBootstrapToken, 6)]
    [InlineData(LocalGatewaySetupPhase.PairOperator, 6)]
    [InlineData(LocalGatewaySetupPhase.CheckWindowsNodeReadiness, 6)]
    [InlineData(LocalGatewaySetupPhase.PairWindowsTrayNode, 6)]
    [InlineData(LocalGatewaySetupPhase.VerifyEndToEnd, 6)]
    public void EveryRunningEnginePhase_AdvancesActiveStage_ToExpectedIndex(LocalGatewaySetupPhase phase, int expectedActiveIndex)
    {
        var states = LocalSetupProgressStageMap.VisibleStages
            .Select(s => LocalSetupProgressStageMap.ComputeStageState(s.Phases, phase, LocalGatewaySetupStatus.Running, phase))
            .ToArray();

        Assert.Equal(SS.Active, states[expectedActiveIndex]);
        for (int i = 0; i < expectedActiveIndex; i++)
            Assert.Equal(SS.Complete, states[i]);
        for (int i = expectedActiveIndex + 1; i < states.Length; i++)
            Assert.Equal(SS.Pending, states[i]);
    }

    [Fact]
    public void NotStarted_RendersAllStagesPending()
    {
        foreach (var s in LocalSetupProgressStageMap.VisibleStages)
        {
            var state = LocalSetupProgressStageMap.ComputeStageState(
                s.Phases, LocalGatewaySetupPhase.NotStarted, LocalGatewaySetupStatus.Pending, LocalGatewaySetupPhase.NotStarted);
            Assert.Equal(SS.Pending, state);
        }
    }

    [Fact]
    public void Complete_RendersAllStagesComplete()
    {
        foreach (var s in LocalSetupProgressStageMap.VisibleStages)
        {
            var state = LocalSetupProgressStageMap.ComputeStageState(
                s.Phases, LocalGatewaySetupPhase.Complete, LocalGatewaySetupStatus.Complete, LocalGatewaySetupPhase.Complete);
            Assert.Equal(SS.Complete, state);
        }
    }

    [Fact]
    public void EveryDeclaredEnginePhase_IsCoveredBySomeVisibleStageOrIsTerminal()
    {
        // Guards against future enum additions silently dropping off the page.
        var covered = LocalSetupProgressStageMap.VisibleStages.SelectMany(s => s.Phases).ToHashSet();
        var terminal = new[]
        {
            LocalGatewaySetupPhase.NotStarted,
            LocalGatewaySetupPhase.Complete,
            LocalGatewaySetupPhase.Failed,
            LocalGatewaySetupPhase.Cancelled,
        };
        foreach (LocalGatewaySetupPhase p in Enum.GetValues(typeof(LocalGatewaySetupPhase)))
        {
            Assert.True(covered.Contains(p) || terminal.Contains(p),
                $"LocalGatewaySetupPhase.{p} is neither a terminal phase nor covered by any visible stage. Add it to LocalSetupProgressStageMap.VisibleStages.");
        }
    }

    // ---------- FailedRetryable: pin failure marker on the stage where engine broke ----------

    [Fact]
    public void FailedRetryable_AtPairOperator_PinsFailureOnLastVisibleStage()
    {
        // PairOperator is the failure mode Aaron's e2e drive hit (Bug 1).
        // The engine sets Phase=Failed on Block(); LastRunningPhase tells us
        // PairOperator was the last running phase, which lives in the MintToken
        // visible stage (index 6).
        var states = LocalSetupProgressStageMap.VisibleStages
            .Select(s => LocalSetupProgressStageMap.ComputeStageState(
                s.Phases,
                LocalGatewaySetupPhase.Failed,
                LocalGatewaySetupStatus.FailedRetryable,
                LocalGatewaySetupPhase.PairOperator))
            .ToArray();

        for (int i = 0; i < 6; i++) Assert.Equal(SS.Complete, states[i]);
        Assert.Equal(SS.Failed, states[6]);
    }

    [Fact]
    public void FailedRetryable_AtCreateWslInstance_PinsFailureOnSecondStage()
    {
        var states = LocalSetupProgressStageMap.VisibleStages
            .Select(s => LocalSetupProgressStageMap.ComputeStageState(
                s.Phases,
                LocalGatewaySetupPhase.Failed,
                LocalGatewaySetupStatus.FailedRetryable,
                LocalGatewaySetupPhase.CreateWslInstance))
            .ToArray();

        Assert.Equal(SS.Complete, states[0]);
        Assert.Equal(SS.Failed, states[1]);
        for (int i = 2; i < states.Length; i++) Assert.Equal(SS.Pending, states[i]);
    }

    [Fact]
    public void FailedTerminal_AtPreflight_PinsFailureOnFirstStage()
    {
        var states = LocalSetupProgressStageMap.VisibleStages
            .Select(s => LocalSetupProgressStageMap.ComputeStageState(
                s.Phases,
                LocalGatewaySetupPhase.Failed,
                LocalGatewaySetupStatus.FailedTerminal,
                LocalGatewaySetupPhase.Preflight))
            .ToArray();

        Assert.Equal(SS.Failed, states[0]);
        for (int i = 1; i < states.Length; i++) Assert.Equal(SS.Pending, states[i]);
    }

    // ---------- Error row + retry button visibility ----------

    [Theory]
    [InlineData(LocalGatewaySetupStatus.FailedRetryable, true)]
    [InlineData(LocalGatewaySetupStatus.FailedTerminal, true)]
    [InlineData(LocalGatewaySetupStatus.Pending, false)]
    [InlineData(LocalGatewaySetupStatus.Running, false)]
    [InlineData(LocalGatewaySetupStatus.Complete, false)]
    [InlineData(LocalGatewaySetupStatus.RequiresAdmin, false)]
    [InlineData(LocalGatewaySetupStatus.RequiresRestart, false)]
    [InlineData(LocalGatewaySetupStatus.Blocked, false)]
    [InlineData(LocalGatewaySetupStatus.Cancelled, false)]
    public void ShouldShowErrorRow_OnlyOnFailureStates(LocalGatewaySetupStatus status, bool expected)
    {
        Assert.Equal(expected, LocalSetupProgressStageMap.ShouldShowErrorRow(status));
    }

    [Theory]
    [InlineData(LocalGatewaySetupStatus.FailedRetryable, true)]
    [InlineData(LocalGatewaySetupStatus.FailedTerminal, false)]
    [InlineData(LocalGatewaySetupStatus.Running, false)]
    [InlineData(LocalGatewaySetupStatus.Complete, false)]
    [InlineData(LocalGatewaySetupStatus.Pending, false)]
    public void ShouldShowRetryButton_OnlyOnFailedRetryable(LocalGatewaySetupStatus status, bool expected)
    {
        Assert.Equal(expected, LocalSetupProgressStageMap.ShouldShowRetryButton(status));
    }

    // ---------- IndexOfStageForPhase ----------

    [Fact]
    public void IndexOfStageForPhase_ReturnsMinusOne_ForUncoveredPhases()
    {
        Assert.Equal(-1, LocalSetupProgressStageMap.IndexOfStageForPhase(LocalGatewaySetupPhase.NotStarted));
        Assert.Equal(-1, LocalSetupProgressStageMap.IndexOfStageForPhase(LocalGatewaySetupPhase.Complete));
        Assert.Equal(-1, LocalSetupProgressStageMap.IndexOfStageForPhase(LocalGatewaySetupPhase.Failed));
        Assert.Equal(-1, LocalSetupProgressStageMap.IndexOfStageForPhase(LocalGatewaySetupPhase.Cancelled));
    }
}
