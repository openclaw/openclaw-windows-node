using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Outcome of binding a helper job to its checkpoint record.</summary>
internal enum ResumableJobCheckpointOutcome
{
    /// <summary>No record existed; a fresh checkpoint was created.</summary>
    Started,
    /// <summary>An existing record for the SAME job and source identity was loaded (resume).</summary>
    Resumed,
    /// <summary>A record for the job exists but belongs to a DIFFERENT source identity (preserved, not clobbered).</summary>
    IdentityCollision,
    /// <summary>The record is missing/mis-scoped/oversized/invalid and was PRESERVED (never deleted).</summary>
    Unreadable,
}

/// <summary>
/// Serializable, self-describing checkpoint record. Deliberately contains NO session history and NO
/// credentials: only the job id, the owning source identity, and the completed ordinals/step ids.
/// </summary>
internal sealed record ResumableJobCheckpointRecord(
    string JobId,
    string SourceIdentity,
    int LastCompletedOrdinal,
    IReadOnlyList<string> CompletedStepIds);

/// <summary>
/// Owned store for a long helper job's checkpoint record, scoped by (jobId, sourceIdentity) and persisted
/// through <see cref="IFileSystem"/>.
/// <para>
/// Record files are named by a stable SHA-256 hex of the job id, never a lossy sanitization; the STORED job
/// identity is verified on load, so the (negligible, not impossible) digest collision cannot silently resume
/// the wrong job.
/// </para>
/// <para>
/// The record is a tiny cursor, not a place for history, so reads are BOUNDED: a record whose size cannot be
/// established, or which exceeds the byte/char bound, is Unreadable and PRESERVED. Saves take the in-process
/// gate and do a check-and-set: the current receipt is READ, VALIDATED, and its source identity, ordinal, and
/// completed prefix verified BEFORE any replacement. The serialized output is bound-checked BEFORE the write,
/// and a unique temp is READ BACK and compared for EXACT equality before promotion; any failure PRESERVES the
/// previous record and the failed artifact.
/// </para>
/// <para>
/// HONEST LIMITS: this is not a power-loss (no fsync) or cross-process-exclusive guarantee. It does NOT make
/// the job exactly-once: the at-least-once window between applying an effect and recording it is closed by the
/// runner's IDEMPOTENT effects plus reconciliation, never by the checkpoint. SHA-256 filename collisions are
/// negligible, not impossible.
/// </para>
/// </summary>
internal sealed class ResumableJobCheckpointStore
{
    // Bounded record limits: a checkpoint is a tiny cursor. A record beyond these bounds is Unreadable and
    // PRESERVED, never parsed (and never written in the first place).
    internal const int MaxRecordBytes = 1024 * 1024;
    internal const int MaxRecordChars = 256 * 1024;

    // In-process serialization of saves. Bounded: ONE gate, no per-job map. Cross-process exclusivity is NOT
    // claimed here.
    private static readonly object SaveGate = new();

    private readonly IFileSystem _fileSystem;
    private readonly string _rootDirectory;

    public ResumableJobCheckpointStore(string rootDirectory, IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("rootDirectory required", nameof(rootDirectory));
        _rootDirectory = rootDirectory;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
    }

    /// <summary>Stable hashed record path (collision-free in practice; stored identity still verified on load).</summary>
    private string RecordPath(string jobId) =>
        Path.Combine(_rootDirectory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId))).ToLowerInvariant()
            + ".checkpoint.json");

    /// <summary>
    /// Binds a job to its checkpoint BEFORE any work starts. Returns the checkpoint to resume from, or an explicit
    /// non-resumable outcome (identity collision / unreadable) that PRESERVES the existing record.
    /// </summary>
    public ResumableJobCheckpointOutcome TryBind(
        string jobId,
        string sourceIdentity,
        out ResumableJobCheckpoint? checkpoint,
        out string? failure)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("jobId required", nameof(jobId));
        if (sourceIdentity is null) throw new ArgumentNullException(nameof(sourceIdentity));
        checkpoint = null;
        failure = null;

        var path = RecordPath(jobId);
        if (!_fileSystem.FileExists(path))
        {
            checkpoint = new ResumableJobCheckpoint(jobId);
            return ResumableJobCheckpointOutcome.Started;
        }

        if (!TryReadRecord(path, jobId, out var record, out failure))
            return ResumableJobCheckpointOutcome.Unreadable;   // preserved, never deleted

        if (!string.Equals(record!.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
        {
            failure = $"checkpoint record for '{jobId}' belongs to a different source identity; preserved.";
            return ResumableJobCheckpointOutcome.IdentityCollision;   // preserved, never clobbered
        }

        if (!TryLoadRecord(record, out checkpoint, out failure))
            return ResumableJobCheckpointOutcome.Unreadable;   // preserved

        return ResumableJobCheckpointOutcome.Resumed;
    }

    private bool TryReadRecord(string path, string jobId, out ResumableJobCheckpointRecord? record, out string? failure)
    {
        record = null;
        failure = null;
        try
        {
            // BOUND BEFORE ALLOCATION: a record whose size cannot be established is NOT read at all.
            var length = _fileSystem.GetFileLength(path);
            if (length < 0)
            {
                failure = $"checkpoint record for '{jobId}' has no verifiable size; preserved.";
                return false;
            }
            if (length > MaxRecordBytes)
            {
                failure = $"checkpoint record for '{jobId}' exceeds {MaxRecordBytes} bytes; preserved.";
                return false;
            }

            var text = _fileSystem.ReadAllText(path);
            if (text.Length > MaxRecordChars)
            {
                failure = $"checkpoint record for '{jobId}' exceeds {MaxRecordChars} chars; preserved.";
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ResumableJobCheckpointRecord>(text);
            if (parsed is null || parsed.CompletedStepIds is null
                || !string.Equals(parsed.JobId, jobId, StringComparison.Ordinal))
            {
                failure = $"checkpoint record for '{jobId}' was empty or mis-scoped.";
                return false;
            }

            record = parsed;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"checkpoint record for '{jobId}' could not be read: {exception.Message}";
            return false;
        }
    }

    private static bool TryLoadRecord(
        ResumableJobCheckpointRecord record,
        out ResumableJobCheckpoint? checkpoint,
        out string? failure)
    {
        checkpoint = null;
        failure = null;
        try
        {
            checkpoint = ResumableJobCheckpoint.Load(record.JobId, record.LastCompletedOrdinal, record.CompletedStepIds);
            return true;
        }
        catch (Exception exception)
        {
            failure = $"checkpoint record for '{record.JobId}' has an invalid ordinal/step list: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Records the checkpoint with check-and-set: the current receipt is read, validated, and its source
    /// identity/ordinal/prefix verified BEFORE replacement; the serialized output is bound-checked BEFORE the
    /// write; a unique temp is read back and compared for EXACT equality before promotion.
    /// </summary>
    public bool TrySave(ResumableJobCheckpoint checkpoint, string sourceIdentity, out string? failure)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        if (sourceIdentity is null) throw new ArgumentNullException(nameof(sourceIdentity));
        failure = null;

        ResumableJobCheckpointRecord next;
        try
        {
            next = checkpoint.Record(sourceIdentity);
        }
        catch (Exception exception)
        {
            failure = $"checkpoint for '{checkpoint.JobId}' is not saveable: {exception.Message}";
            return false;
        }

        if (next.LastCompletedOrdinal < 0 || next.CompletedStepIds.Count != next.LastCompletedOrdinal)
        {
            failure = $"checkpoint for '{checkpoint.JobId}' is inconsistent; refused.";
            return false;
        }

        var path = RecordPath(checkpoint.JobId);
        lock (SaveGate)
        {
            if (_fileSystem.FileExists(path))
            {
                if (!TryReadRecord(path, checkpoint.JobId, out var existing, out var readFailure))
                {
                    failure = readFailure;
                    return false;   // preserved
                }

                // The EXISTING record must itself be valid before it is replaced.
                if (!TryLoadRecord(existing!, out _, out var existingFailure))
                {
                    failure = existingFailure;
                    return false;   // preserved, never silently overwritten
                }

                if (!string.Equals(existing!.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
                {
                    failure = $"checkpoint record for '{checkpoint.JobId}' belongs to a different source identity; refused.";
                    return false;
                }

                if (existing.LastCompletedOrdinal > next.LastCompletedOrdinal)
                {
                    failure = $"checkpoint for '{checkpoint.JobId}' would regress ordinal "
                        + $"{existing.LastCompletedOrdinal} to {next.LastCompletedOrdinal}; refused.";
                    return false;
                }

                if (!IsPrefix(existing.CompletedStepIds, next.CompletedStepIds))
                {
                    failure = $"checkpoint for '{checkpoint.JobId}' would drop previously completed steps; refused.";
                    return false;
                }
            }

            // SERIALIZED-OUTPUT BOUND: a save this store could not read back is not a valid save. Checked BEFORE
            // the write, so the previous record stands.
            var json = JsonSerializer.Serialize(next);
            if (json.Length > MaxRecordChars || Encoding.UTF8.GetByteCount(json) > MaxRecordBytes)
            {
                failure = $"checkpoint for '{checkpoint.JobId}' serializes beyond the readable bound "
                    + $"({MaxRecordChars} chars / {MaxRecordBytes} bytes); refused.";
                return false;   // previous record preserved
            }

            var unique = Guid.NewGuid().ToString("N");
            var temp = path + "." + unique + ".tmp";
            try
            {
                _fileSystem.CreateDirectory(_rootDirectory);
                _fileSystem.WriteAllText(temp, json);

                // READBACK: compare the COMPLETE exact serialized text (job id, source, ordinal, AND the full step
                // list). A partially written or identity-corrupted temp is never promoted.
                var readbackText = _fileSystem.ReadAllText(temp);
                if (!string.Equals(readbackText, json, StringComparison.Ordinal))
                    throw new InvalidDataException("temp checkpoint readback did not match the exact written text");

                _fileSystem.MoveFile(temp, path, overwrite: true);
                return true;
            }
            catch (Exception exception)
            {
                failure = $"checkpoint record for '{checkpoint.JobId}' could not be saved: {exception.Message}";
                PreserveFailedArtifact(temp, unique);   // PRESERVE the failed artifact; never delete it
                return false;
            }
        }
    }

    private void PreserveFailedArtifact(string temp, string unique)
    {
        try
        {
            if (!_fileSystem.FileExists(temp)) return;
            var failed = temp.Substring(0, temp.Length - ".tmp".Length) + ".failed." + unique + ".json";
            _fileSystem.MoveFile(temp, failed, overwrite: false);
        }
        catch
        {
            // Best effort: if the artifact cannot be renamed it is left in place, still preserved.
        }
    }

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> full)
    {
        if (prefix.Count > full.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(prefix[i], full[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}