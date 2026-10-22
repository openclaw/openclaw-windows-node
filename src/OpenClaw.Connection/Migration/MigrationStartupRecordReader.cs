using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum MigrationStartupRecordStatus
{
    None,
    Intent,
    Completed,
    Invalid,
    Unavailable
}

/// <summary>
/// <paramref name="CompletionPresent"/> reports that a completion receipt exists on disk even when
/// it could not be decoded. Data has already moved in that case, so admission must still protect
/// the handoff; <see cref="MigrationStartupRecordStatus.Invalid"/> alone cannot distinguish a
/// corrupt receipt from a corrupt intent.
/// </summary>
public sealed record MigrationStartupRecord(
    MigrationStartupRecordStatus Status,
    MigrationRecord? Record = null,
    bool CompletionPresent = false);

public interface IMigrationStartupRecordReader
{
    MigrationStartupRecord Read();
}

/// <summary>
/// Reads pending handoffs without preparing, renewing, or deleting any record.
/// An expired intent still requires an explicit migration decision, not fresh startup.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationStartupRecordReader(
    MigrationBinding binding,
    IOpenClawLogger logger,
    TimeProvider? timeProvider = null) : IMigrationStartupRecordReader
{
    public MigrationStartupRecord Read()
    {
        try
        {
            var completed = ReadFile(MigrationRecordCodec.CompletionFileName, "completed");
            if (completed is not null)
                return new(MigrationStartupRecordStatus.Completed, completed, true);

            var intent = ReadFile(MigrationRecordCodec.IntentFileName, "intent");
            return intent is null
                ? new(MigrationStartupRecordStatus.None)
                : new(MigrationStartupRecordStatus.Intent, intent);
        }
        catch (MigrationPathRejectedException ex)
        {
            // A reparse point above the record directory is an environment shape, not a corrupt
            // record. Recovery cannot repair it, so report it as an inspection problem instead.
            // The receipt is still probed: refusing to open the path is about not reading or
            // deleting through a redirection, and "the path is unsafe" is not evidence that
            // data has not already moved.
            logger.Error($"Store migration record path is unusable ({ex.Message}).");
            return new(MigrationStartupRecordStatus.Unavailable, null, CompletionFileExists());
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or
                                   EndOfStreamException or ArgumentException or FormatException or
                                   DecoderFallbackException)
        {
            logger.Warn($"Store migration record requires recovery ({ex.GetType().Name}).");
            return new(MigrationStartupRecordStatus.Invalid, null, CompletionFileExists());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.Error($"Store migration record inspection failed ({ex.GetType().Name}).");
            return new(MigrationStartupRecordStatus.Unavailable, null, CompletionFileExists());
        }
    }

    /// <summary>
    /// Existence only. A receipt that cannot be decoded still proves the handoff moved data, so
    /// this answers "is the receipt definitely absent?" and treats every other outcome as
    /// present. It runs from catch handlers and must never throw.
    /// <para>
    /// <see cref="File.Exists(string?)"/> is deliberately not used: it reports <c>false</c> for
    /// an inaccessible path, an invalid path, and a directory of the same name, so it cannot
    /// distinguish absence from failure and would fail open on exactly the paths that matter.
    /// Only the not-found results prove nothing moved.
    /// </para>
    /// </summary>
    private bool CompletionFileExists()
    {
        try
        {
            _ = File.GetAttributes(RecordPath(MigrationRecordCodec.CompletionFileName));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.Info($"No migration receipt is present ({ex.GetType().Name}).");
            return false;
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not confirm the migration receipt ({ex.GetType().Name}); assuming it exists.");
            return true;
        }
    }

    private string RecordPath(string fileName) =>
        Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName, fileName);

    private MigrationRecord? ReadFile(string fileName, string expectedKind)
    {
        var path = RecordPath(fileName);
        if (MigrationRecordCodec.HasReparsePointAncestor(path))
            throw new MigrationPathRejectedException("Migration paths must not contain reparse points.");
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
                throw new InvalidDataException("Invalid migration startup record size.");
            using var reader = new BinaryReader(stream);
            var bytes = reader.ReadBytes((int)stream.Length);
            MigrationRecord record;
            try
            {
                record = MigrationRecordCodec.DecodeForRenewedConsent(
                    bytes, binding, (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            }
            // Defensive only: nothing in the decode raises this today, but the type derives from
            // IOException and would otherwise be silently reclassified as a corrupt record.
            catch (MigrationPathRejectedException) { throw; }
            catch (IOException exception)
            {
                // Decode uses only memory, so this is corruption rather than an IO failure.
                // BinaryReader reports a malformed string length that way, and reporting it as an
                // inspection failure would route the record to InspectionFailed instead of
                // Recovery, which is the only screen that offers a way out.
                throw new InvalidDataException("Invalid migration startup record encoding.", exception);
            }
            if (record.Kind != expectedKind)
                throw new InvalidDataException("Unexpected migration startup record kind.");
            return record;
        }
    }
}
