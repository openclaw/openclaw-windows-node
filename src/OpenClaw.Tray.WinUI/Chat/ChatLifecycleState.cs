using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>
/// Owns active-run identity/sequences, abort suppression and deferral,
/// bounded terminal-run deduplication, and bounded cancelled-run retirement.
/// The root serializes every operation.
/// </summary>
internal sealed class ChatLifecycleState
{
    private const int TerminalRunCapacity = 64;
    private const int RetiredCancelledRunCapacity = 64;

    private readonly Dictionary<string, string> _activeRunIds = new();
    private readonly Dictionary<string, long> _activeRunStartSequences = new();
    private readonly Dictionary<string, int> _pendingAbortCounts = new();
    private readonly HashSet<string> _abortedRunIds = new();
    private readonly HashSet<string> _abortedThreads = new();
    private readonly Dictionary<string, List<string>> _terminalRunIdsByThread =
        new();
    // Aborted or reset-ignored runs whose terminal already arrived. Their late
    // output must stay suppressed after the live abort/reset state is cleared.
    // Reasons are tracked separately so a failed abort cannot clear a reset fence.
    private readonly Dictionary<string, RetiredRun> _retiredCancelledRuns = new(StringComparer.Ordinal);
    private long _retiredCancelledRunOrder;

    [Flags]
    private enum RetireReason
    {
        None = 0,
        Abort = 1,
        Reset = 2,
    }

    private readonly record struct RetiredRun(RetireReason Reasons, long Order);

    private long _lifecycleStartSequence;

    internal bool IsResponseSuppressed => _abortedThreads.Count > 0;
    internal long LifecycleStartSequence => _lifecycleStartSequence;

    internal bool HasActiveRun(string threadId) =>
        _activeRunIds.ContainsKey(threadId);

    internal bool IsTerminalRun(string threadId, string? runId) =>
        runId is not null && _terminalRunIdsByThread.TryGetValue(threadId, out var runIds) &&
        runIds.Contains(runId, StringComparer.Ordinal);

    internal bool TryGetActiveRun(string threadId, out string? runId) =>
        _activeRunIds.TryGetValue(threadId, out runId);

    internal bool HasRunStartedAfter(
        string threadId,
        string runId,
        long sequence)
    {
        return _activeRunIds.TryGetValue(threadId, out var activeRunId) &&
               _activeRunStartSequences.TryGetValue(
                   threadId,
                   out var activeSequence) &&
               string.Equals(activeRunId, runId, StringComparison.Ordinal) &&
               activeSequence > sequence;
    }

    internal ChatAbortStart BeginAbort(
        string threadId,
        bool hadActiveTurn)
    {
        _activeRunIds.TryGetValue(threadId, out var runId);
        _abortedThreads.Add(threadId);
        if (!string.IsNullOrEmpty(runId))
        {
            _abortedRunIds.Add(runId);
        }
        else
        {
            _pendingAbortCounts.TryGetValue(threadId, out var count);
            _pendingAbortCounts[threadId] = count + 1;
        }
        return new(runId, hadActiveTurn);
    }

    internal void RollbackAbort(string threadId, string runId)
    {
        _abortedThreads.Remove(threadId);
        ReleaseFailedAbort(runId);
        RemoveActiveRun(threadId);
    }

    internal void ReleaseFailedAbort(string runId)
    {
        _abortedRunIds.Remove(runId);
        // The terminal may have retired the run before the abort RPC failed.
        // The run was not cancelled, so its trailing output is admissible again.
        ReleaseRetirement(runId, RetireReason.Abort);
    }

    internal void CompleteAbort(string threadId, string? runId)
    {
        if (!string.IsNullOrEmpty(runId))
            RemoveActiveRun(threadId);
        _abortedThreads.Remove(threadId);
    }

    internal bool ShouldSuppress(string threadId, string? runId) =>
        !string.IsNullOrEmpty(runId) && _abortedRunIds.Contains(runId) ||
        _abortedThreads.Contains(threadId);

    internal bool IsThreadSuppressed(string threadId) =>
        _abortedThreads.Contains(threadId);

    internal bool IsRunAborted(string? runId) =>
        !string.IsNullOrWhiteSpace(runId) && _abortedRunIds.Contains(runId);

    internal bool IsRetiredCancelledRun(string? runId) =>
        !string.IsNullOrWhiteSpace(runId) && _retiredCancelledRuns.ContainsKey(runId);

    /// <summary>
    /// True when a frame carries the run ID of a cancelled run, before or after
    /// that run's terminal. Frames without a run ID cannot be correlated.
    /// </summary>
    internal bool IsCancelledRun(string? runId) =>
        IsRunAborted(runId) || IsRetiredCancelledRun(runId);

    /// <summary>Fences a run whose terminal a session reset consumed.</summary>
    internal void RetireResetRun(string runId) =>
        Retire(runId, RetireReason.Reset);

    private void Retire(string runId, RetireReason reason)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;
        if (_retiredCancelledRuns.TryGetValue(runId, out var existing))
        {
            _retiredCancelledRuns[runId] = existing with { Reasons = existing.Reasons | reason };
            return;
        }
        _retiredCancelledRuns[runId] = new(reason, ++_retiredCancelledRunOrder);
        if (_retiredCancelledRuns.Count > RetiredCancelledRunCapacity)
        {
            var oldest = _retiredCancelledRuns.MinBy(pair => pair.Value.Order).Key;
            _retiredCancelledRuns.Remove(oldest);
        }
    }

    private void ReleaseRetirement(string runId, RetireReason reason)
    {
        if (string.IsNullOrWhiteSpace(runId) ||
            !_retiredCancelledRuns.TryGetValue(runId, out var existing))
        {
            return;
        }
        var remaining = existing.Reasons & ~reason;
        if (remaining == RetireReason.None)
            _retiredCancelledRuns.Remove(runId);
        else
            _retiredCancelledRuns[runId] = existing with { Reasons = remaining };
    }

    internal bool HasPendingAbort(string threadId) =>
        _pendingAbortCounts.ContainsKey(threadId);

    internal int TakePendingAbortCount(string threadId)
    {
        return _pendingAbortCounts.Remove(threadId, out var count)
            ? count
            : 0;
    }

    internal long StartRun(string threadId, string runId)
    {
        _activeRunIds[threadId] = runId;
        var sequence = ++_lifecycleStartSequence;
        _activeRunStartSequences[threadId] = sequence;
        return sequence;
    }

    internal void MarkDeferredAbort(string threadId, string runId)
    {
        _abortedThreads.Add(threadId);
        _abortedRunIds.Add(runId);
    }

    internal void RemoveAbortedRun(string? runId)
    {
        if (!string.IsNullOrEmpty(runId) && _abortedRunIds.Remove(runId))
            Retire(runId, RetireReason.Abort);
    }

    internal void ClearThreadSuppression(string threadId) =>
        _abortedThreads.Remove(threadId);

    internal string? CompleteAssistantFinal(string threadId)
    {
        _activeRunIds.Remove(threadId, out var completedRunId);
        if (!string.IsNullOrEmpty(completedRunId))
        {
            RememberTerminalRun(threadId, completedRunId);
            _abortedRunIds.Remove(completedRunId);
        }
        _activeRunStartSequences.Remove(threadId);
        _abortedThreads.Remove(threadId);
        return completedRunId;
    }

    internal void RemoveActiveRun(string threadId)
    {
        _activeRunIds.Remove(threadId);
        _activeRunStartSequences.Remove(threadId);
    }

    internal bool ShouldDropTerminal(
        string threadId,
        string runId,
        IReadOnlyCollection<string> queuedRunIds,
        bool turnActive,
        out ChatTerminalEventDropReason? droppedReason)
    {
        droppedReason = null;
        if (string.IsNullOrWhiteSpace(runId))
        {
            droppedReason = ChatTerminalEventDropReason.MissingRunId;
            return true;
        }
        if (_terminalRunIdsByThread.TryGetValue(threadId, out var terminalRunIds) &&
            terminalRunIds.Contains(runId, StringComparer.Ordinal))
        {
            return true;
        }
        if (_activeRunIds.TryGetValue(threadId, out var activeRunId) &&
            !string.Equals(activeRunId, runId, StringComparison.Ordinal))
        {
            droppedReason = ChatTerminalEventDropReason.MismatchedRunId;
            return true;
        }
        if (!_activeRunIds.ContainsKey(threadId) &&
            queuedRunIds.Count > 0 &&
            !queuedRunIds.Contains(runId, StringComparer.Ordinal) &&
            turnActive)
        {
            droppedReason = ChatTerminalEventDropReason.MismatchedRunId;
            return true;
        }
        RememberTerminalRun(threadId, runId);
        return false;
    }

    internal string? ActiveRunForReset(string threadId) =>
        _activeRunIds.TryGetValue(threadId, out var runId)
            ? runId
            : null;

    internal void ClearThreadForReset(string threadId)
    {
        RemoveActiveRun(threadId);
        _pendingAbortCounts.Remove(threadId);
        _abortedThreads.Remove(threadId);
        _terminalRunIdsByThread.Remove(threadId);
    }

    internal void ClearForReconnect()
    {
        _terminalRunIdsByThread.Clear();
        _activeRunIds.Clear();
        _activeRunStartSequences.Clear();
    }

    internal void ClearForDispose() =>
        _terminalRunIdsByThread.Clear();

    internal void ClearActiveRuns(IEnumerable<string> threadIds)
    {
        foreach (var threadId in threadIds)
            RemoveActiveRun(threadId);
    }

    private void RememberTerminalRun(string threadId, string runId)
    {
        if (!_terminalRunIdsByThread.TryGetValue(threadId, out var runIds))
        {
            runIds = [];
            _terminalRunIdsByThread[threadId] = runIds;
        }
        runIds.Remove(runId);
        runIds.Add(runId);
        if (runIds.Count > TerminalRunCapacity)
            runIds.RemoveRange(0, runIds.Count - TerminalRunCapacity);
    }
}
