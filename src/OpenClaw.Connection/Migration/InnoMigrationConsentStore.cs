using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Persists explicit same-user Inno consent independently of a preparation inventory.
/// Call Grant only after UI confirmation and exact same-user Inno installation detection.
/// No source settings, services, or identities are inspected or changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InnoMigrationConsentStore(
    MigrationBinding binding,
    IOpenClawLogger logger,
    TimeProvider? timeProvider = null)
{
    internal const string WriterLockFileName = "consent.lock";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Writes a fresh 30-day grant under a shared prepare.lock lease and an exclusive
    /// consent writer lock, so consent can be granted while Inno is running.
    /// Explicit confirmation may renew an
    /// expired, otherwise valid grant or change its source version. Corrupt, wrong-kind,
    /// wrong-binding, and future-dated records require recovery and are never overwritten.
    /// Invalid-record failures surface as InvalidDataException with the original exception retained.
    /// Any completion receipt, even an invalid one, blocks a new grant.
    /// </summary>
    public void Grant(string sourceVersion)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (binding.UserSid != identity.User?.Value)
            throw new InvalidOperationException("Migration consent must use the current Windows user.");
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        try
        {
            MigrationRecordStorage.CreateProtectedDirectory(directory);
            using var migrationLock = AcquireReadLease(directory, FileMode.OpenOrCreate);
            var writerPath = Path.Combine(directory, WriterLockFileName);
            MigrationRecordCodec.RejectReparsePoints(writerPath);
            using var writerLock = new FileStream(writerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var completionPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);
            MigrationRecordCodec.RejectReparsePoints(completionPath);
            if (Exists(completionPath))
                throw new InvalidOperationException("A completion receipt already exists. Resume or recover migration before granting consent again.");

            var now = _clock.GetUtcNow().UtcDateTime;
            _ = ReadConsent(directory, now, allowExpired: true);
            var record = new MigrationRecord
            {
                Kind = "consent",
                MigrationId = Guid.NewGuid().ToString("D"),
                SourceVersion = sourceVersion,
                Binding = binding,
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(30),
                Fingerprint = MigrationRecordCodec.ConsentFingerprint
            };
            MigrationRecordStorage.WriteAtomic(
                Path.Combine(directory, MigrationRecordCodec.ConsentFileName),
                MigrationRecordCodec.Encode(record, now));
        }
        catch (Exception exception) when (IsInvalidRecord(exception))
        {
            logger.Warn($"Inno migration consent requires recovery ({exception.GetType().Name}).");
            throw new InvalidDataException("Inno migration consent requires recovery.", exception);
        }
    }

    /// <summary>
    /// Missing, expired, or invalid consent returns false and requires fresh Store confirmation.
    /// Inspection creates no files or directories. Operational IO/access failures, including
    /// a missing or contended prepare.lock when consent exists, propagate to the caller.
    /// This read never renews consent; inventory intents and completion receipts are not consent.
    /// </summary>
    public bool HasValidConsent(string sourceVersion)
    {
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        try
        {
            var consentPath = Path.Combine(directory, MigrationRecordCodec.ConsentFileName);
            MigrationRecordCodec.RejectReparsePoints(consentPath);
            // A grant appearing after this check can safely require fresh confirmation.
            if (!Exists(consentPath))
                return false;
            using var migrationLock = AcquireReadLease(directory, FileMode.Open);
            var record = ReadConsent(directory, _clock.GetUtcNow().UtcDateTime, allowExpired: false);
            if (record is null)
                return false;
            if (!string.Equals(record.SourceVersion, sourceVersion, StringComparison.Ordinal))
            {
                logger.Warn("Inno migration consent source version changed. Confirm migration again.");
                return false;
            }
            return true;
        }
        catch (Exception exception) when (IsInvalidRecord(exception))
        {
            logger.Warn($"Inno migration consent is invalid. Confirm migration again ({exception.GetType().Name}).");
            return false;
        }
    }

    private static FileStream AcquireReadLease(string directory, FileMode mode)
    {
        var lockPath = Path.Combine(directory, "prepare.lock");
        MigrationRecordCodec.RejectReparsePoints(lockPath);
        return new FileStream(lockPath, mode, FileAccess.Read, FileShare.Read);
    }

    private MigrationRecord? ReadConsent(string directory, DateTime now, bool allowExpired)
    {
        var path = Path.Combine(directory, MigrationRecordCodec.ConsentFileName);
        MigrationRecordCodec.RejectReparsePoints(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        using (stream)
        {
            if (stream.Length == 0 || stream.Length > MigrationRecordCodec.MaximumRecordBytes)
                throw new InvalidDataException("Invalid migration consent size.");
            using var reader = new BinaryReader(stream);
            var bytes = reader.ReadBytes((int)stream.Length);
            MigrationRecord record;
            try
            {
                record = allowExpired
                    ? MigrationRecordCodec.DecodeForRenewedConsent(bytes, binding, now)
                    : MigrationRecordCodec.Decode(bytes, binding, now);
            }
            catch (IOException exception)
            {
                // Decode uses only memory. BinaryReader reports malformed string lengths
                // as IO errors; file IO above must still propagate as an operational failure.
                throw new InvalidDataException("Invalid migration consent encoding.", exception);
            }
            if (record.Kind != "consent")
                throw new InvalidDataException("Expected explicit migration consent.");
            return record;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsInvalidRecord(Exception exception) =>
        exception is InvalidDataException or CryptographicException or EndOfStreamException or
            ArgumentException or DecoderFallbackException or FormatException;
}
