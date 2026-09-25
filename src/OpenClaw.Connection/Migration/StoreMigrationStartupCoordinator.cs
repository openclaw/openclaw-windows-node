using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationStartupState
{
    Disabled,
    NotRequired,
    UpdateInno,
    UnsupportedInstallation,
    InspectionFailed,
    ConsentRequired,
    AwaitingInnoRemoval,
    FinalizationRequired,
    RecoveryRequired
}

public sealed record StoreMigrationStartupDecision(
    StoreMigrationStartupState State,
    InnoInstallation? Installation = null,
    bool HoldsCompletionReceipt = false,
    bool SourcePayloadPresent = false)
{
    public bool AllowsNormalStartup =>
        State is StoreMigrationStartupState.Disabled or StoreMigrationStartupState.NotRequired;

    /// <summary>
    /// A handoff that has already moved data, or an installed source the Store app must not run
    /// beside, refuses launch. The remaining states inform the user and then get out of the way:
    /// refusing to start cannot repair a failed inspection or a corrupt record, and the records
    /// stay on disk either way. Blocking there would only deny the user the app.
    /// <para>
    /// This is driven by receipt presence rather than by the state name. A receipt can coexist
    /// with a state that reads as informational (an unsupported source, a version mismatch, an
    /// undecodable record), and starting normally in those cases would leave both installations
    /// live against the same data.
    /// </para>
    /// <para>
    /// An unsupported or out-of-date source blocks only when <paramref name="SourcePayloadPresent"/>
    /// confirms the app is really installed. Issue #1374 permits one active production client, and
    /// an installed source the user must update or replace is still a client. The presence check is
    /// what keeps that from becoming a lockout: an interrupted uninstall can leave a registration
    /// with no payload, and blocking on that alone would leave no working app at all.
    /// </para>
    /// </summary>
    public bool BlocksStartup => HoldsDurableBlock || SourceBlocksStartup;

    /// <summary>
    /// The half of <see cref="BlocksStartup"/> that outlives the pass that observed it. Data has
    /// moved, or is about to, so a later inspection that fails or throws must not drop the block:
    /// the evidence is a record on disk, and not being able to re-read it does not unmake it.
    /// </summary>
    public bool HoldsDurableBlock =>
        HoldsCompletionReceipt ||
        State is StoreMigrationStartupState.FinalizationRequired or
                 StoreMigrationStartupState.AwaitingInnoRemoval;

    /// <summary>
    /// The half of <see cref="BlocksStartup"/> that is only ever as good as the pass that measured
    /// it. It asserts a source is installed right now, so it must be recomputed every pass and
    /// never latched: the user removing the source is exactly how this block is meant to end, and
    /// a stale copy would keep the Store app closed after the previous app was already gone.
    /// </summary>
    public bool SourceBlocksStartup =>
        SourcePayloadPresent &&
        State is StoreMigrationStartupState.UnsupportedInstallation or
                 StoreMigrationStartupState.UpdateInno;
}

/// <summary>
/// Read-only admission before instance forwarding or normal app services.
/// ConsentRequired is a handoff to the consent workflow, not permission to migrate.
/// </summary>
public sealed class StoreMigrationStartupCoordinator(
    IInnoInstallationDetector detector,
    IMigrationStartupRecordReader records,
    IOpenClawLogger logger)
{
    public StoreMigrationStartupDecision Evaluate(bool enabled, string? minimumSourceVersion, string architecture)
    {
        if (!enabled)
            return new(StoreMigrationStartupState.Disabled);

        // Read before the policy check so a malformed version or architecture policy cannot
        // return a non-blocking decision while a receipt sits on disk.
        var pending = records.Read();
        // Captured once so every downstream state carries it. A receipt outranks the state name.
        // Status is checked alongside the flag so a caller that reports Completed without it
        // still cannot produce a non-blocking decision.
        var receipt = pending.CompletionPresent || pending.Status == MigrationStartupRecordStatus.Completed;

        if (!MigrationVersionPolicy.TryParseReleaseVersion(minimumSourceVersion, out var minimum) ||
            architecture is not ("x64" or "arm64"))
        {
            logger.Error("Store migration has no valid source-version or architecture policy.");
            return Decide(StoreMigrationStartupState.InspectionFailed, receipt);
        }

        if (pending.Status == MigrationStartupRecordStatus.Unavailable)
            return Decide(StoreMigrationStartupState.InspectionFailed, receipt);
        if (pending.Status == MigrationStartupRecordStatus.Invalid)
            return Decide(StoreMigrationStartupState.RecoveryRequired, receipt);

        var detected = detector.Detect();
        var sourcePayloadPresent = detected.SourcePayloadPresent;
        if (detected.Status == InnoInstallationStatus.InspectionFailed)
            return Decide(StoreMigrationStartupState.InspectionFailed, receipt);
        if (detected.Status == InnoInstallationStatus.Unsupported)
        {
            // An installation that predates the migration payload is not unsupportable; it just
            // needs the update that ships migration. Asking for that is actionable guidance.
            return Decide(detected.RegisteredVersion is { } registered && registered < minimum
                ? StoreMigrationStartupState.UpdateInno
                : StoreMigrationStartupState.UnsupportedInstallation, receipt,
                sourcePayloadPresent: sourcePayloadPresent);
        }

        if (detected.Status == InnoInstallationStatus.NotInstalled)
        {
            return Decide(pending.Status switch
            {
                MigrationStartupRecordStatus.None => StoreMigrationStartupState.NotRequired,
                MigrationStartupRecordStatus.Completed => StoreMigrationStartupState.FinalizationRequired,
                _ => StoreMigrationStartupState.RecoveryRequired
            }, receipt);
        }

        var installation = detected.Installation
            ?? throw new InvalidOperationException("Detected Inno installation has no installation evidence.");
        if (installation.Architecture != architecture)
            return Decide(StoreMigrationStartupState.UnsupportedInstallation, receipt, installation,
                sourcePayloadPresent);

        if (pending.Status == MigrationStartupRecordStatus.Completed)
        {
            // Status without a decoded record should be unreachable, but throwing here would
            // escape into the UI error boundary and be reported as a plain inspection failure,
            // dropping the very receipt this branch proves exists. Recover instead.
            if (pending.Record is not { } completed)
            {
                logger.Error("Completed migration has no decoded receipt; recovery is required.");
                return Decide(StoreMigrationStartupState.RecoveryRequired, receipt, installation);
            }
            if (!MigrationVersionPolicy.TryParseReleaseVersion(completed.SourceVersion, out var sourceVersion) ||
                sourceVersion != installation.Version)
                return Decide(StoreMigrationStartupState.RecoveryRequired, receipt, installation);
            return Decide(StoreMigrationStartupState.AwaitingInnoRemoval, receipt, installation);
        }

        if (installation.Version < minimum)
            return Decide(StoreMigrationStartupState.UpdateInno, receipt, installation, sourcePayloadPresent);

        // Even a valid, unexpired Inno intent does not replace Store-side consent.
        return Decide(StoreMigrationStartupState.ConsentRequired, receipt, installation);
    }

    private StoreMigrationStartupDecision Decide(
        StoreMigrationStartupState state, bool holdsCompletionReceipt,
        InnoInstallation? installation = null, bool sourcePayloadPresent = false)
    {
        logger.Info(
            $"Store migration startup admission: {state} (receipt: {holdsCompletionReceipt}, " +
            $"source payload: {sourcePayloadPresent}).");
        return new(state, installation, holdsCompletionReceipt, sourcePayloadPresent);
    }
}
