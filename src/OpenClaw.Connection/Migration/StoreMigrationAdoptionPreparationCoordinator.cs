using OpenClaw.Shared;
using System.Runtime.Versioning;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationPreparationState
{
    InnoRunning,
    SourceChanged,
    ValidationFailed,
    Prepared
}

public sealed record StoreMigrationPreparationDecision(
    StoreMigrationPreparationState State,
    MigrationRecord? Intent = null);

public interface IMigrationSourceLease : IDisposable;

public interface IMigrationSourceLeaseProvider
{
    IMigrationSourceLease? TryAcquire();
}

/// <summary>
/// Acquires exclusive source ownership, rechecks exact installation evidence,
/// and prepares a protected inventory. It never adopts, deletes, or completes state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreMigrationAdoptionPreparationCoordinator(
    IMigrationSourceLeaseProvider leaseProvider,
    IInnoInstallationDetector detector,
    MigrationPreparation preparation,
    IOpenClawLogger logger,
    IInnoSourceActivityVerifier? sourceActivity = null)
{
    public StoreMigrationPreparationDecision Prepare(InnoInstallation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        using var lease = leaseProvider.TryAcquire();
        if (lease is null)
            return new(StoreMigrationPreparationState.InnoRunning);

        try
        {
            using var migrationLock = preparation.AcquireLock();
            var detected = detector.Detect();
            if (detected.Status != InnoInstallationStatus.Detected || detected.Installation != expected)
                return new(StoreMigrationPreparationState.SourceChanged);

            var activity = (sourceActivity ?? new InnoSourceActivityVerifier(expected.ExecutablePath)).VerifyStopped();
            if (activity == InnoSourceActivityStatus.Running)
                return new(StoreMigrationPreparationState.InnoRunning);
            if (activity != InnoSourceActivityStatus.Stopped)
            {
                logger.Error("Store migration preparation could not exclude source processes across sessions.");
                return new(StoreMigrationPreparationState.ValidationFailed);
            }

            var intent = preparation.PrepareUnderLock(expected.Version.ToString());
            logger.Info($"Store migration preparation completed: {intent.MigrationId}.");
            return new(StoreMigrationPreparationState.Prepared, intent);
        }
        catch (IOException exception) when (MigrationOperationLock.IsBusy(exception))
        {
            logger.Info("Store migration preparation is blocked by an active runtime or migration operation.");
            return new(StoreMigrationPreparationState.InnoRunning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            logger.Error($"Store migration preparation failed: {exception.Message}");
            return new(StoreMigrationPreparationState.ValidationFailed);
        }
    }
}
