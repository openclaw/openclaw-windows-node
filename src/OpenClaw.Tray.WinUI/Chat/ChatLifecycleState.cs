using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>
/// Owns active-run identity/sequences, abort suppression and deferral, and
/// bounded terminal-run deduplication. The root serializes every operation.
/// </summary>
internal sealed class ChatLifecycleState
{
    private const int TerminalRunCapacity = 64;

    private readonly Dictionary<string, string> _activeRunIds = new();
    private readonly Dictionary<string, long> _activeRunStartSequences = new();
    private readonly Dictionary<string, int> _pendingAbortCounts = new();
    private readonly HashSet<string> _abortedRunIds = new();
    private readonly HashSet<string> _abortedThreads = new();
    private readonly Dictionary<string, List<TerminalRun>> _terminalRunIdsByThread =
        new();

    private sealed record TerminalRun(
        string RunId,
        bool SuppressOutput,
        bool AssistantFinalReceived,
        bool RetryableError);

    private long _lifecycleStartSequence;

    internal bool IsResponseSuppressed => _abortedThreads.Count > 0;
    internal long LifecycleStartSequence => _lifecycleStartSequence;

    internal bool HasActiveRun(string threadId) =>
        _activeRunIds.ContainsKey(threadId);

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
        _abortedRunIds.Remove(runId);
        RemoveActiveRun(threadId);
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

    internal bool ShouldSuppressChatMessage(
        string threadId,
        string? runId,
        bool isFinal,
        IReadOnlyCollection<string> queuedRunIds,
        bool turnActive)
    {
        if (ShouldSuppress(threadId, runId))
            return true;
        // Older gateways omit runId. Keep their existing thread-level behavior.
        if (string.IsNullOrWhiteSpace(runId))
            return false;
        if (_activeRunIds.TryGetValue(threadId, out var activeRunId) &&
            !string.Equals(activeRunId, runId, StringComparison.Ordinal))
        {
            return true;
        }
        if (_terminalRunIdsByThread.TryGetValue(threadId, out var terminalRuns) &&
            terminalRuns.Find(run => run.RunId == runId) is { } terminal)
        {
            // A lifecycle end can precede the final text. Only the most recent,
            // non-aborted run may finish that text, before another turn starts.
            return !isFinal || terminal.SuppressOutput || terminal.AssistantFinalReceived ||
                   turnActive || terminal != terminalRuns.FindLast(run => !run.SuppressOutput);
        }
        return !_activeRunIds.ContainsKey(threadId) &&
               turnActive &&
               queuedRunIds.Count > 0 &&
               !queuedRunIds.Contains(runId, StringComparer.Ordinal);
    }

    internal bool IsRunAborted(string? runId) =>
        !string.IsNullOrWhiteSpace(runId) && _abortedRunIds.Contains(runId);

    internal bool IsCompletedRun(string threadId, string? runId) =>
        !string.IsNullOrWhiteSpace(runId) &&
        _terminalRunIdsByThread.TryGetValue(threadId, out var runs) &&
        runs.Exists(run => run.RunId == runId);

    internal bool TryRestartFailedRun(string threadId, string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || HasActiveRun(threadId) ||
            !_terminalRunIdsByThread.TryGetValue(threadId, out var runs) ||
            runs.FindLast(run => !run.SuppressOutput) is not { RetryableError: true, AssistantFinalReceived: false } failed ||
            !string.Equals(failed.RunId, runId, StringComparison.Ordinal) ||
            IsRunAborted(runId))
        {
            return false;
        }
        return runs.Remove(failed);
    }

    internal bool ShouldSuppressCompletedAgentEvent(
        string threadId,
        AgentEventInfo evt,
        bool canReconcile = false)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId) ||
            !_terminalRunIdsByThread.TryGetValue(threadId, out var terminalRuns) ||
            terminalRuns.Find(run => run.RunId == evt.RunId) is not { } terminal)
        {
            return false;
        }
        return terminal.SuppressOutput || !canReconcile;
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
        ClearRetryEligibility(threadId);
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
        if (!string.IsNullOrEmpty(runId))
            _abortedRunIds.Remove(runId);
    }

    internal void ClearThreadSuppression(string threadId) =>
        _abortedThreads.Remove(threadId);

    internal string? CompleteAssistantFinal(string threadId, string? runId = null)
    {
        ClearRetryEligibility(threadId);
        _activeRunIds.Remove(threadId, out var activeRunId);
        var completedRunId = string.IsNullOrWhiteSpace(runId) ? activeRunId : runId;
        if (!string.IsNullOrEmpty(completedRunId))
        {
            RememberTerminalRun(threadId, completedRunId, assistantFinalReceived: true);
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

    internal void RememberSuppressedTerminal(string threadId, string runId) =>
        RememberTerminalRun(threadId, runId, suppressOutput: true);

    internal void ClearRetryEligibility(string threadId)
    {
        if (!_terminalRunIdsByThread.TryGetValue(threadId, out var runs))
            return;
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].RetryableError)
                runs[i] = runs[i] with { RetryableError = false };
        }
    }

    internal bool ShouldDropTerminal(
        string threadId,
        string runId,
        IReadOnlyCollection<string> queuedRunIds,
        bool turnActive,
        out ChatTerminalEventDropReason? droppedReason,
        bool retryableError = false)
    {
        droppedReason = null;
        if (string.IsNullOrWhiteSpace(runId))
        {
            droppedReason = ChatTerminalEventDropReason.MissingRunId;
            return true;
        }
        if (_terminalRunIdsByThread.TryGetValue(threadId, out var terminalRunIds) &&
            terminalRunIds.Exists(run => run.RunId == runId))
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
        RememberTerminalRun(threadId, runId, retryableError: retryableError);
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

    private void RememberTerminalRun(
        string threadId,
        string runId,
        bool assistantFinalReceived = false,
        bool suppressOutput = false,
        bool retryableError = false)
    {
        if (!_terminalRunIdsByThread.TryGetValue(threadId, out var runIds))
        {
            runIds = [];
            _terminalRunIdsByThread[threadId] = runIds;
        }
        var previous = runIds.Find(run => run.RunId == runId);
        runIds.RemoveAll(run => run.RunId == runId);
        if (runIds.Count >= TerminalRunCapacity)
            runIds.RemoveAt(0);
        var terminal = new TerminalRun(
            runId,
            suppressOutput || _abortedRunIds.Contains(runId) || previous?.SuppressOutput == true,
            assistantFinalReceived || previous?.AssistantFinalReceived == true,
            retryableError);
        // Retention follows arrival order. Trailing-final eligibility separately
        // ignores suppressed terminals, including ones learned from abort state.
        runIds.Add(terminal);
    }
}
