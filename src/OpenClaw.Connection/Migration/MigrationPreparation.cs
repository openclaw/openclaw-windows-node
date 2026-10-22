using System.Runtime.Versioning;
using System.Text.Json;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Prepares a read-only state inventory after explicit migration consent.
/// This does not stop Inno, import data, or authorize migration-aware uninstall.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationPreparation(MigrationBinding binding, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public MigrationRecord Prepare(string sourceVersion)
    {
        using var migrationLock = AcquireLock();
        return PrepareUnderLock(sourceVersion);
    }

    internal FileStream AcquireLock() => MigrationOperationLock.AcquireExclusive(binding);

    // The coordinator owns the lock through source inspection and publication.
    internal MigrationRecord PrepareUnderLock(string sourceVersion)
    {
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var completedPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);
        // Even an invalid receipt needs explicit recovery, not an overwritten attempt.
        if (Path.Exists(completedPath))
            throw new InvalidOperationException("A completion receipt already exists. Resume or recover migration before preparing again.");

        var inventory = MigrationInventory.Capture(binding.RoamingDirectory, binding.LocalDirectory);
        var now = _clock.GetUtcNow().UtcDateTime;
        var intentPath = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        MigrationRecordCodec.RejectReparsePoints(intentPath);
        if (Path.Exists(intentPath))
        {
            using var stream = new FileStream(intentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MigrationRecordCodec.MaximumRecordBytes)
                throw new InvalidDataException("Invalid migration intent size.");
            using var reader = new BinaryReader(stream);
            var existing = MigrationRecordCodec.DecodeForRenewedConsent(reader.ReadBytes((int)stream.Length), binding, now);
            if (existing.Kind != "intent")
                throw new InvalidDataException("Expected a migration intent.");
            if (existing.ExpiresUtc > now && existing.Fingerprint == inventory.Fingerprint && existing.SourceVersion == sourceVersion)
                return existing;
        }

        var record = new MigrationRecord
        {
            Kind = "intent",
            MigrationId = Guid.NewGuid().ToString("D"),
            SourceVersion = sourceVersion,
            Binding = binding,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30),
            AutoStart = inventory.AutoStart,
            Fingerprint = inventory.Fingerprint,
            InventoryJson = JsonSerializer.Serialize(inventory)
        };
        var bytes = MigrationRecordCodec.Encode(record, now);
        MigrationRecordStorage.WriteAtomic(intentPath, bytes);
        return record;
    }
}
