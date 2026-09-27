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

public sealed record MigrationStartupRecord(
    MigrationStartupRecordStatus Status,
    MigrationRecord? Record = null);

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
                return new(MigrationStartupRecordStatus.Completed, completed);

            var intent = ReadFile(MigrationRecordCodec.IntentFileName, "intent");
            return intent is null
                ? new(MigrationStartupRecordStatus.None)
                : new(MigrationStartupRecordStatus.Intent, intent);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or
                                   EndOfStreamException or ArgumentException or DecoderFallbackException)
        {
            logger.Warn($"Store migration record requires recovery ({ex.GetType().Name}).");
            return new(MigrationStartupRecordStatus.Invalid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.Error($"Store migration record inspection failed ({ex.GetType().Name}).");
            return new(MigrationStartupRecordStatus.Unavailable);
        }
    }

    private MigrationRecord? ReadFile(string fileName, string expectedKind)
    {
        var path = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName, fileName);
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
                throw new InvalidDataException("Invalid migration startup record size.");
            using var reader = new BinaryReader(stream);
            var record = MigrationRecordCodec.DecodeForRenewedConsent(
                reader.ReadBytes((int)stream.Length), binding,
                (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            if (record.Kind != expectedKind)
                throw new InvalidDataException("Unexpected migration startup record kind.");
            return record;
        }
    }
}
