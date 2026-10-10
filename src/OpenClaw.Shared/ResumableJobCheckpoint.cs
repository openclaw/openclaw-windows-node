using System;
using System.Collections.Generic;

namespace OpenClaw.Shared;

/// <summary>
/// U3 companion reliability: MONOTONIC, in-memory checkpoint for a long helper job.
/// Steps complete in increasing ordinal order; a resume starts at the next ordinal
/// and a repeated ordinal/step id is rejected.
/// <para>
/// This type alone is NOT durable and does NOT establish exactly-once: no persistence or load lives here.
/// Durability comes from <see cref="ResumableJobCheckpointStore"/>; the at-least-once window between
/// applying an effect and recording it is closed by the runner's IDEMPOTENT effects plus reconciliation
/// (adopt an already-applied ordinal), never by the checkpoint alone.
/// </para>
/// </summary>
internal sealed class ResumableJobCheckpoint
{
    /// <summary>Bound on a resumable ordinal: a checkpoint is a small cursor, not an unbounded counter.</summary>
internal const int MaxOrdinal = 1_000_000;
    internal const int MaxStepIdChars = 4096;
    internal const int MaxSourceIdentityChars = 4096;

    private readonly object _gate = new();
    private readonly List<string> _completedStepIds = new();
    // O(1) duplicate/replay membership so resume does not reload with a quadratic Contains scan.
    private readonly HashSet<string> _completedStepIdSet = new(StringComparer.Ordinal);
    private int _lastCompletedOrdinal;

    public ResumableJobCheckpoint(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("jobId required", nameof(jobId));
        JobId = jobId;
    }

    public string JobId { get; }

    public int LastCompletedOrdinal
    {
        get { lock (_gate) { return _lastCompletedOrdinal; } }
    }

    public IReadOnlyList<string> CompletedStepIds
    {
        get { lock (_gate) { return _completedStepIds.ToArray(); } }
    }

    /// <summary>Next ordinal to run (1-based).</summary>
    public int NextOrdinal
    {
        get { lock (_gate) { return _lastCompletedOrdinal + 1; } }
    }

    /// <summary>
    /// Marks a step complete. Rejects a non-monotonic ordinal or a repeated step id so a
    /// resumed job cannot replay or reorder work.
    /// </summary>
    public bool TryMarkCompleted(string stepId, int ordinal)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return false;
        if (stepId.Length > MaxStepIdChars) return false;
        lock (_gate)
        {
            if (ordinal != _lastCompletedOrdinal + 1) return false;
            if (ordinal > MaxOrdinal) return false;
            if (!_completedStepIdSet.Add(stepId)) return false;   // O(1) replay guard
            _completedStepIds.Add(stepId);
            _lastCompletedOrdinal = ordinal;
            return true;
        }
    }

    /// <summary>Validates a persisted ordinal obtained from a checkpoint store.</summary>
    public static bool IsResumable(int persistedLastCompletedOrdinal, int totalSteps)
        => persistedLastCompletedOrdinal >= 0 && totalSteps > 0 && persistedLastCompletedOrdinal < totalSteps;

    /// <summary>
    /// REBUILDS a checkpoint from a durable record (resume). The step list must be monotonic and must match
    /// the recorded ordinal exactly, so a truncated or hand-edited record cannot resume silently.
    /// </summary>
    public static ResumableJobCheckpoint Load(
        string jobId, int lastCompletedOrdinal, IReadOnlyList<string>? completedStepIds)
    {
        if (lastCompletedOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(lastCompletedOrdinal));
        if (lastCompletedOrdinal > MaxOrdinal) throw new ArgumentOutOfRangeException(nameof(lastCompletedOrdinal));
        var steps = completedStepIds ?? Array.Empty<string>();
        if (steps.Count != lastCompletedOrdinal)
            throw new ArgumentException("checkpoint step list does not match the recorded ordinal", nameof(completedStepIds));
        var checkpoint = new ResumableJobCheckpoint(jobId);
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is null || steps[i].Length > MaxStepIdChars)
                throw new ArgumentException("checkpoint step id is null or over-long", nameof(completedStepIds));
            if (!checkpoint.TryMarkCompleted(steps[i], i + 1))
                throw new ArgumentException("checkpoint step list is not monotonic", nameof(completedStepIds));
        }
        return checkpoint;
    }

    /// <summary>Snapshot for durable persistence (no session history, no credentials).</summary>
    public ResumableJobCheckpointRecord Record(string sourceIdentity)
    {
        if (sourceIdentity is null) throw new ArgumentNullException(nameof(sourceIdentity));
        if (sourceIdentity.Length > MaxSourceIdentityChars) throw new ArgumentException("source identity is over-long", nameof(sourceIdentity));
        lock (_gate)
        {
            return new ResumableJobCheckpointRecord(
                JobId, sourceIdentity, _lastCompletedOrdinal, _completedStepIds.ToArray());
        }
    }
}
