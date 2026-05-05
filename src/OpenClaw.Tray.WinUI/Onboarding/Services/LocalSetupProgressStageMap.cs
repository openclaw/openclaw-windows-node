using System.Collections.Generic;
using System.Linq;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Pure helpers for <c>LocalSetupProgressPage</c>'s stage-list rendering
/// (Phase 5). Lives in the Services namespace (no WinUI / FunctionalUI
/// dependencies) so unit tests in <c>OpenClaw.Tray.Tests</c> can import
/// it directly via the project's selective <c>&lt;Compile Include&gt;</c> list.
///
/// Exists to fix Bug 2 from the e2e drive (2026-05-04) — the page render
/// previously inlined this logic AND took a reference-typed snapshot, which
/// hid two distinct defects:
///   1. The engine raises <see cref="LocalGatewaySetupEngine.StateChanged"/>
///      with the same mutating <see cref="LocalGatewaySetupState"/> instance,
///      so reference-equality in <c>UseState</c> suppressed re-renders.
///   2. The stage-state computation depended on <see cref="LocalGatewaySetupPhase.Failed"/>'s
///      ordinal, but on failure the engine pins <c>Phase = Failed</c> (the highest
///      ordinal), losing the position of the last running phase. This helper
///      threads <c>lastRunningPhase</c> explicitly so failure rendering is
///      stable across the engine's full phase set.
/// </summary>
public static class LocalSetupProgressStageMap
{
    public enum StageState
    {
        Pending,
        Active,
        Complete,
        Failed,
    }

    public sealed record VisibleStage(string LabelKey, LocalGatewaySetupPhase[] Phases);

    /// <summary>
    /// Whitelist of user-meaningful stages. Hidden phases (e.g. ElevationCheck,
    /// PairOperator, CheckWindowsNodeReadiness, PairWindowsTrayNode, VerifyEndToEnd)
    /// fold into a neighbouring visible stage or surface only as the subtitle line.
    /// </summary>
    public static readonly IReadOnlyList<VisibleStage> VisibleStages = new VisibleStage[]
    {
        new("Onboarding_LocalSetup_Phase_Preflight",      new[] { LocalGatewaySetupPhase.Preflight, LocalGatewaySetupPhase.EnsureWslEnabled, LocalGatewaySetupPhase.ElevationCheck }),
        new("Onboarding_LocalSetup_Phase_CreateInstance", new[] { LocalGatewaySetupPhase.CreateWslInstance }),
        new("Onboarding_LocalSetup_Phase_Configure",      new[] { LocalGatewaySetupPhase.ConfigureWslInstance }),
        new("Onboarding_LocalSetup_Phase_InstallCli",     new[] { LocalGatewaySetupPhase.InstallOpenClawCli }),
        new("Onboarding_LocalSetup_Phase_PrepareConfig",  new[] { LocalGatewaySetupPhase.PrepareGatewayConfig, LocalGatewaySetupPhase.InstallGatewayService }),
        new("Onboarding_LocalSetup_Phase_StartGateway",   new[] { LocalGatewaySetupPhase.StartGateway, LocalGatewaySetupPhase.WaitForGateway }),
        new("Onboarding_LocalSetup_Phase_MintToken",      new[] { LocalGatewaySetupPhase.MintBootstrapToken, LocalGatewaySetupPhase.PairOperator, LocalGatewaySetupPhase.CheckWindowsNodeReadiness, LocalGatewaySetupPhase.PairWindowsTrayNode, LocalGatewaySetupPhase.VerifyEndToEnd }),
    };

    /// <summary>
    /// Compute the visual state for a single visible stage given the current
    /// engine phase, status, and (when failed) the last running phase prior
    /// to failure (read from <see cref="LocalGatewaySetupState.History"/>).
    /// </summary>
    public static StageState ComputeStageState(
        LocalGatewaySetupPhase[] stagePhases,
        LocalGatewaySetupPhase currentPhase,
        LocalGatewaySetupStatus currentStatus,
        LocalGatewaySetupPhase lastRunningPhase)
    {
        if (currentStatus == LocalGatewaySetupStatus.Complete)
            return StageState.Complete;

        var stageOrdinals = stagePhases.Select(p => (int)p).ToArray();
        var minOrdinalInStage = stageOrdinals.Min();
        var maxOrdinalInStage = stageOrdinals.Max();

        if (currentStatus == LocalGatewaySetupStatus.FailedRetryable
            || currentStatus == LocalGatewaySetupStatus.FailedTerminal
            || currentPhase == LocalGatewaySetupPhase.Failed)
        {
            // Use the last running phase to pin the failure marker on the
            // stage where the engine actually broke.
            var lastOrdinal = (int)lastRunningPhase;
            if (lastOrdinal >= minOrdinalInStage && lastOrdinal <= maxOrdinalInStage)
                return StageState.Failed;
            if (lastOrdinal > maxOrdinalInStage)
                return StageState.Complete;
            return StageState.Pending;
        }

        if (currentStatus == LocalGatewaySetupStatus.Cancelled)
        {
            var lastOrdinal = (int)lastRunningPhase;
            if (lastOrdinal > maxOrdinalInStage) return StageState.Complete;
            if (lastOrdinal >= minOrdinalInStage && lastOrdinal <= maxOrdinalInStage) return StageState.Pending;
            return StageState.Pending;
        }

        var currentOrdinal = (int)currentPhase;
        if (currentOrdinal > maxOrdinalInStage)
            return StageState.Complete;
        if (currentOrdinal >= minOrdinalInStage && currentOrdinal <= maxOrdinalInStage)
            return StageState.Active;
        return StageState.Pending;
    }

    /// <summary>
    /// Find the index of the visible stage that should be highlighted Active
    /// (or Failed) for the given engine phase. Returns -1 when no visible
    /// stage covers the phase (e.g. <see cref="LocalGatewaySetupPhase.NotStarted"/>
    /// or <see cref="LocalGatewaySetupPhase.Complete"/>).
    /// </summary>
    public static int IndexOfStageForPhase(LocalGatewaySetupPhase phase)
    {
        for (int i = 0; i < VisibleStages.Count; i++)
        {
            if (VisibleStages[i].Phases.Contains(phase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// True when the page should render the inline error / retry row
    /// (FailedRetryable or FailedTerminal). All other statuses collapse it.
    /// </summary>
    public static bool ShouldShowErrorRow(LocalGatewaySetupStatus status)
        => status == LocalGatewaySetupStatus.FailedRetryable
        || status == LocalGatewaySetupStatus.FailedTerminal;

    /// <summary>
    /// True when the inline error row should expose a Try Again button —
    /// only on FailedRetryable. FailedTerminal forces Back-out.
    /// </summary>
    public static bool ShouldShowRetryButton(LocalGatewaySetupStatus status)
        => status == LocalGatewaySetupStatus.FailedRetryable;
}
