using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationDiscardState
{
    Discarded,
    RecordsAreReadable,
    Busy,
    Failed
}

/// <summary>
/// The escape from <see cref="StoreMigrationStartupState.RecoveryRequired"/> when the records
/// cannot carry the migration any further. Without it a receipt that stops decoding, after a DPAPI
/// key loss or a profile move, blocks every launch with no in-app way back.
/// <para>
/// Records are classified one file at a time rather than through the aggregate startup status,
/// which stops at the first unreadable file. A corrupt receipt beside a perfectly good intent must
/// only cost the receipt.
/// </para>
/// <para>
/// Two things are discardable: a record that does not decode, and any record left behind once the
/// source app is gone and no readable receipt exists, because there is then nothing left to
/// migrate from and the record can never be acted on again. A receipt that decodes is never
/// discarded; it is the proof that data moved.
/// </para>
/// <para>
/// Discarding does not expose user data. While the Store package stays registered,
/// <c>scripts/Test-InnoMigration.ps1</c> answers a missing receipt with exit 11 and an undecodable
/// one with exit 2, and both preserve generated state and the gateway.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreMigrationRecoveryDiscard(
    MigrationBinding binding,
    IInnoInstallationDetector detector,
    IOpenClawLogger logger)
{
    private enum RecordState
    {
        Missing,
        Readable,
        Unreadable
    }

    /// <summary>
    /// Best-effort and unlocked, for deciding whether to offer the action at all. It must not
    /// throw: recovery is already on screen when this runs. <see cref="Discard"/> re-plans under
    /// the lock, so an offer made here is never trusted as authority to delete.
    /// </summary>
    public bool CanDiscard()
    {
        try
        {
            return Plan().Count > 0;
        }
        catch (Exception exception)
        {
            logger.Warn($"Could not classify the migration records ({exception.GetType().Name}).");
            return false;
        }
    }

    public StoreMigrationDiscardState Discard()
    {
        var directory = Directory;
        try
        {
            RejectPath(directory);
            var lockPath = Path.Combine(directory, "prepare.lock");
            RejectPath(lockPath);
            // Exclusive: uninstall reads these same records to decide whether to preserve data.
            // OpenOrCreate, because a missing lease file is not evidence that there is nothing to
            // discard. Opening it with Open would report success having deleted nothing, which is
            // the same dead end this class exists to remove.
            using var discardLock = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var targets = Plan();
            if (targets.Count == 0)
                return StoreMigrationDiscardState.RecordsAreReadable;

            // The consent writer lock is residue rather than a record, but it is only meaningful
            // alongside consent, and no grant can be in flight while this lease is held.
            targets.Insert(0, Path.Combine(directory, InnoMigrationConsentStore.WriterLockFileName));
            foreach (var path in targets)
            {
                RejectPath(path);
                File.Delete(path);
            }

            logger.Info($"Discarded {targets.Count} store migration record file(s).");
            return StoreMigrationDiscardState.Discarded;
        }
        catch (DirectoryNotFoundException exception)
        {
            // Nothing is left to discard, which is the outcome the user asked for.
            logger.Info($"No store migration records to discard ({exception.GetType().Name}).");
            return StoreMigrationDiscardState.Discarded;
        }
        catch (MigrationPathRejectedException exception)
        {
            // A rejected path shape is not lock contention. Telling the user to close the previous
            // app would send them after something that can never help.
            logger.Error($"Store migration record paths were rejected: {exception.Message}");
            return StoreMigrationDiscardState.Failed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Store migration records are in use ({exception.GetType().Name}).");
            return StoreMigrationDiscardState.Busy;
        }
        catch (Exception exception)
        {
            logger.Error($"Could not discard store migration records: {exception}");
            return StoreMigrationDiscardState.Failed;
        }
    }

    private string Directory => Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);

    /// <summary>
    /// A rejected path shape is an operational refusal, never evidence about a record's contents.
    /// It is raised as <see cref="MigrationPathRejectedException"/> so it stays distinguishable
    /// from lock contention all the way out to the caller.
    /// </summary>
    private static void RejectPath(string path)
    {
        if (MigrationRecordCodec.HasReparsePointAncestor(path))
            throw new MigrationPathRejectedException($"Migration path {path} contains a reparse point.");
    }

    /// <summary>
    /// The receipt is planned last so an interrupted discard still looks like a handoff that needs
    /// recovery rather than one that never happened. Operational failures propagate: a record that
    /// merely could not be opened is not evidence that it is unreadable.
    /// </summary>
    private List<string> Plan()
    {
        var directory = Directory;
        var completionPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);
        var completion = Classify(completionPath, "completed");
        if (completion == RecordState.Readable)
            return [];

        var sourceGone = detector.Detect().Status == InnoInstallationStatus.NotInstalled;
        var targets = new List<string>();
        Consider(Path.Combine(directory, MigrationRecordCodec.ConsentFileName), "consent");
        Consider(Path.Combine(directory, MigrationRecordCodec.IntentFileName), "intent");
        if (completion == RecordState.Unreadable)
            targets.Add(completionPath);
        return targets;

        void Consider(string path, string kind)
        {
            var state = Classify(path, kind);
            if (state == RecordState.Unreadable || (sourceGone && state == RecordState.Readable))
                targets.Add(path);
        }
    }

    private RecordState Classify(string path, string expectedKind)
    {
        byte[] bytes;
        try
        {
            // Mirrors the reader: a path shape is an operational refusal, not a corrupt record.
            RejectPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > MigrationRecordCodec.MaximumRecordBytes)
                return RecordState.Unreadable;
            using var reader = new BinaryReader(stream);
            bytes = reader.ReadBytes((int)stream.Length);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return RecordState.Missing;
        }

        // Decoding is pure memory work, so it is judged separately. Everything outside the
        // allowlist propagates: a record that merely could not be read is not evidence of
        // corruption, and only corruption may nominate a file for deletion. IOException is part
        // of the allowlist here precisely because there is no file IO left in this block:
        // BinaryReader reports a malformed string length that way, and treating it as an
        // operational failure would hide the discard offer and restore the lockout.
        try
        {
            var record = MigrationRecordCodec.DecodeForRenewedConsent(bytes, binding, DateTime.UtcNow);
            return record.Kind == expectedKind ? RecordState.Readable : RecordState.Unreadable;
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException
                                              or EndOfStreamException or IOException
                                              or ArgumentException or FormatException
                                              or DecoderFallbackException)
        {
            logger.Warn($"Migration record {expectedKind} does not decode ({exception.GetType().Name}).");
            return RecordState.Unreadable;
        }
    }
}
