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
    InnoInstallation? Installation = null)
{
    public bool AllowsNormalStartup =>
        State is StoreMigrationStartupState.Disabled or StoreMigrationStartupState.NotRequired;
}

/// <summary>
/// Read-only admission before instance forwarding or normal app services.
/// ConsentRequired is a handoff to a future consent workflow, not permission to migrate.
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

        if (!MigrationVersionPolicy.TryParseReleaseVersion(minimumSourceVersion, out var minimum) ||
            architecture is not ("x64" or "arm64"))
        {
            logger.Error("Store migration preview has no valid source-version or architecture policy.");
            return new(StoreMigrationStartupState.InspectionFailed);
        }

        var pending = records.Read();
        if (pending.Status == MigrationStartupRecordStatus.Unavailable)
            return Decide(StoreMigrationStartupState.InspectionFailed);
        if (pending.Status == MigrationStartupRecordStatus.Invalid)
            return Decide(StoreMigrationStartupState.RecoveryRequired);

        var detected = detector.Detect();
        if (detected.Status == InnoInstallationStatus.InspectionFailed)
            return Decide(StoreMigrationStartupState.InspectionFailed);
        if (detected.Status == InnoInstallationStatus.Unsupported)
            return Decide(StoreMigrationStartupState.UnsupportedInstallation);

        if (detected.Status == InnoInstallationStatus.NotInstalled)
        {
            return Decide(pending.Status switch
            {
                MigrationStartupRecordStatus.None => StoreMigrationStartupState.NotRequired,
                MigrationStartupRecordStatus.Completed => StoreMigrationStartupState.FinalizationRequired,
                _ => StoreMigrationStartupState.RecoveryRequired
            });
        }

        var installation = detected.Installation
            ?? throw new InvalidOperationException("Detected Inno installation has no installation evidence.");
        if (installation.Architecture != architecture)
            return Decide(StoreMigrationStartupState.UnsupportedInstallation, installation);

        if (pending.Status == MigrationStartupRecordStatus.Completed)
        {
            var completed = pending.Record
                ?? throw new InvalidOperationException("Completed migration has no receipt.");
            if (!MigrationVersionPolicy.TryParseReleaseVersion(completed.SourceVersion, out var sourceVersion) ||
                sourceVersion != installation.Version)
                return Decide(StoreMigrationStartupState.RecoveryRequired, installation);
            return Decide(StoreMigrationStartupState.AwaitingInnoRemoval, installation);
        }

        if (installation.Version < minimum)
            return Decide(StoreMigrationStartupState.UpdateInno, installation);

        // Even a valid, unexpired Inno intent does not replace Store-side consent.
        return Decide(StoreMigrationStartupState.ConsentRequired, installation);
    }

    private StoreMigrationStartupDecision Decide(StoreMigrationStartupState state, InnoInstallation? installation = null)
    {
        logger.Info($"Store migration startup admission: {state}.");
        return new(state, installation);
    }
}
