using System.Runtime.Versioning;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Publishes an already-validated completion receipt without replacing an existing receipt.
/// The caller owns source validation and serialization with preparation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationCompletionReceiptWriter(MigrationBinding binding, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public void Write(MigrationRecord receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        MigrationRecordCodec.RejectReparsePoints(directory);
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("Migration preparation is required before completion.");

        var receiptPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);
        MigrationRecordCodec.RejectReparsePoints(receiptPath);
        if (File.Exists(receiptPath))
            throw new InvalidOperationException("A completion receipt already exists. Resume or recover migration before completing again.");

        var bytes = MigrationRecordCodec.Encode(receipt, _clock.GetUtcNow().UtcDateTime);
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, receiptPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
