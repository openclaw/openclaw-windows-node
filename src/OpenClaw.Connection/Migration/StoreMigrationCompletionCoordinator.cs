using System.Runtime.Versioning;
using System.Security.Cryptography;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationCompletionState
{
    InnoRunning,
    SourceChanged,
    IntentUnavailable,
    NoActiveGateway,
    CredentialUnavailable,
    ValidationFailed,
    Completed
}

public sealed record StoreMigrationCompletionDecision(
    StoreMigrationCompletionState State,
    MigrationRecord? Receipt = null);

/// <summary>
/// Revalidates prepared source state under exclusive ownership and records completion.
/// It only proves a canonical active-gateway credential can resolve. It never starts,
/// repairs, or provisions gateway, node, or MCP services.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreMigrationCompletionCoordinator(
    IMigrationSourceLeaseProvider leaseProvider,
    IInnoInstallationDetector detector,
    MigrationBinding binding,
    ICredentialResolver credentialResolver,
    IOpenClawLogger logger,
    TimeProvider? timeProvider = null,
    IInnoSourceActivityVerifier? sourceActivity = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public StoreMigrationCompletionDecision Complete(InnoInstallation expected, string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        using var lease = leaseProvider.TryAcquire();
        if (lease is null)
            return new(StoreMigrationCompletionState.InnoRunning);

        try
        {
            var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
            MigrationRecordCodec.RejectReparsePoints(directory);
            var lockPath = Path.Combine(directory, "prepare.lock");
            MigrationRecordCodec.RejectReparsePoints(lockPath);
            using var migrationLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Uninstall owns this same file through source removal. Evidence from
            // before acquiring the lock cannot authorize completion afterward.
            var detected = detector.Detect();
            if (detected.Status != InnoInstallationStatus.Detected || detected.Installation != expected)
                return new(StoreMigrationCompletionState.SourceChanged);

            var activity = (sourceActivity ?? new InnoSourceActivityVerifier(expected.ExecutablePath)).VerifyStopped();
            if (activity == InnoSourceActivityStatus.Running)
                return new(StoreMigrationCompletionState.InnoRunning);
            if (activity != InnoSourceActivityStatus.Stopped)
            {
                logger.Error("Store migration completion could not exclude source processes across sessions.");
                return new(StoreMigrationCompletionState.ValidationFailed);
            }

            var intent = ReadIntent(directory);
            var inventory = MigrationInventory.Capture(binding.RoamingDirectory, binding.LocalDirectory);
            if (intent.SourceVersion != expected.Version.ToString() ||
                !string.Equals(intent.Fingerprint, inventory.Fingerprint, StringComparison.Ordinal))
            {
                return new(StoreMigrationCompletionState.SourceChanged);
            }
            var registry = new GatewayRegistry(binding.RoamingDirectory, logger: logger);
            registry.Load();
            var active = registry.GetActive();
            if (active is null)
                return new(StoreMigrationCompletionState.NoActiveGateway);

            var resolution = credentialResolver.ResolveOperatorDetailed(
                active, registry.GetIdentityDirectory(active.Id));
            if (resolution.Credential is null)
                return new(StoreMigrationCompletionState.CredentialUnavailable);

            var receipt = new MigrationRecord
            {
                Kind = "completed",
                MigrationId = intent.MigrationId,
                SourceVersion = intent.SourceVersion,
                TargetVersion = targetVersion,
                Fingerprint = inventory.Fingerprint,
                AutoStart = inventory.AutoStart,
                CreatedUtc = _clock.GetUtcNow().UtcDateTime,
                ExpiresUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
                Binding = binding
            };
            new MigrationCompletionReceiptWriter(binding, _clock).Write(receipt);
            logger.Info($"Store migration completion recorded: {receipt.MigrationId}.");
            return new(StoreMigrationCompletionState.Completed, receipt);
        }
        catch (IOException exception) when (MigrationOperationLock.IsBusy(exception))
        {
            logger.Info("Store migration completion is blocked by an active runtime or migration operation.");
            return new(StoreMigrationCompletionState.InnoRunning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException)
        {
            logger.Error($"Store migration completion failed: {exception.Message}");
            return new(StoreMigrationCompletionState.ValidationFailed);
        }
    }

    private MigrationRecord ReadIntent(string directory)
    {
        var intentPath = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        MigrationRecordCodec.RejectReparsePoints(intentPath);
        using var stream = new FileStream(intentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length > MigrationRecordCodec.MaximumRecordBytes)
            throw new InvalidDataException("Invalid migration intent size.");
        using var reader = new BinaryReader(stream);
        var intent = MigrationRecordCodec.Decode(
            reader.ReadBytes((int)stream.Length), binding, _clock.GetUtcNow().UtcDateTime);
        if (intent.Kind != "intent")
            throw new InvalidDataException("Expected a migration intent.");
        return intent;
    }
}
