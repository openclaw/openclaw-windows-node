using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatResetMessageGate(
    bool Drop,
    string? ConsumeEchoText,
    bool RequestRemoteBackfill,
    AgentEventInfo? OpenedLifecycleStart);

internal sealed record ChatResetAgentGate(
    bool Drop,
    bool ReloadHistory,
    AgentEventInfo? OpenedLifecycleStart);

/// <summary>
/// Owns reset generations, timestamp cutoffs, ignored/accepted runs, buffered
/// lifecycle starts, submitted-echo gates, and remote-backfill state. The root
/// supplies queue/lifecycle facts and applies any returned lifecycle start.
/// </summary>
internal sealed class ChatResetState
{
    private const long TimestampToleranceMs = 1000;
    private static readonly TimeSpan LocalEchoWindow = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, long> _versions = new();
    private readonly Dictionary<string, long> _cutoffUtcMs = new();
    private readonly HashSet<string> _awaitingUserMessage = new();
    private readonly Dictionary<string, HashSet<string>> _ignoredRunIds = new();
    private readonly Dictionary<string, Dictionary<string, Queue<DateTimeOffset>>>
        _submittedLocalEchoTexts = new();
    private readonly Dictionary<string, HashSet<string>> _acceptedRunIds = new();
    private readonly Dictionary<string, long> _localSendWithoutRunVersions = new();
    private readonly Dictionary<string, long> _localSendWithoutRunStartSequences =
        new();
    private readonly Dictionary<string, long> _localEchoSequences = new();
    private readonly Dictionary<string, List<PendingLifecycleStart>>
        _pendingLifecycleStarts = new();
    private readonly HashSet<string> _remoteBackfillInFlight = new();
    private readonly HashSet<string> _remoteUserSeen = new();

    private long _lifecycleStartSequence;

    private readonly record struct PendingLifecycleStart(
        AgentEventInfo Event,
        long Sequence);

    internal long LifecycleStartSequence => _lifecycleStartSequence;
    internal bool IsAwaitingUserMessage(string threadId) =>
        _awaitingUserMessage.Contains(threadId);

    internal long GetVersion(string threadId) =>
        _versions.TryGetValue(threadId, out var version) ? version : 0;

    internal IReadOnlyDictionary<string, long> SnapshotVersions() =>
        new Dictionary<string, long>(_versions);

    internal long BeginReset(string threadId, long cutoffUtcMs)
    {
        var generation = GetVersion(threadId) + 1;
        _versions[threadId] = generation;
        _cutoffUtcMs[threadId] = cutoffUtcMs;
        _awaitingUserMessage.Add(threadId);
        _acceptedRunIds.Remove(threadId);
        _localSendWithoutRunVersions.Remove(threadId);
        _localSendWithoutRunStartSequences.Remove(threadId);
        _localEchoSequences.Remove(threadId);
        _pendingLifecycleStarts.Remove(threadId);
        _remoteBackfillInFlight.Remove(threadId);
        _remoteUserSeen.Remove(threadId);
        return generation;
    }

    internal void ClearSubmittedEchoesForReconnect() =>
        _submittedLocalEchoTexts.Clear();

    internal void AddIgnoredRun(string threadId, string runId)
    {
        if (!_ignoredRunIds.TryGetValue(threadId, out var runIds))
        {
            runIds = new HashSet<string>(StringComparer.Ordinal);
            _ignoredRunIds[threadId] = runIds;
        }
        runIds.Add(runId);
    }

    internal void AddSubmittedLocalEcho(
        string threadId,
        string text,
        DateTimeOffset submittedAt)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (!_submittedLocalEchoTexts.TryGetValue(threadId, out var texts))
        {
            texts = new Dictionary<string, Queue<DateTimeOffset>>(
                StringComparer.Ordinal);
            _submittedLocalEchoTexts[threadId] = texts;
        }
        var normalized = text.Trim();
        if (!texts.TryGetValue(normalized, out var timestamps))
        {
            timestamps = new Queue<DateTimeOffset>();
            texts[normalized] = timestamps;
        }
        timestamps.Enqueue(submittedAt);
    }

    internal ChatResetMessageGate EvaluateChatMessage(
        string threadId,
        string role,
        string rawText,
        long timestampMs,
        bool hasPendingLocalEcho)
    {
        var isNormalUserText = role == "user" &&
            !ChatContentFormatting.LooksLikeApprovalSlashCommand(rawText) &&
            !NativeToolProjector.LooksLikeSystemControlNote(rawText);

        if (isNormalUserText &&
            !hasPendingLocalEcho &&
            TryConsumeSubmittedLocalEcho(threadId, rawText))
        {
            AgentEventInfo? opened = null;
            if (_awaitingUserMessage.Contains(threadId) &&
                !IsPreResetTimestamp(threadId, timestampMs))
            {
                _localEchoSequences[threadId] = _lifecycleStartSequence;
                opened = TryOpenPendingLifecycle(
                    threadId,
                    acceptedRunId: null);
            }
            return new(
                Drop: true,
                ConsumeEchoText: rawText.Trim(),
                RequestRemoteBackfill: false,
                OpenedLifecycleStart: opened);
        }
        if (!_awaitingUserMessage.Contains(threadId))
        {
            return new(
                IsPreResetTimestamp(threadId, timestampMs),
                null,
                false,
                null);
        }

        var isFreshUser = isNormalUserText &&
            !IsPreResetTimestamp(threadId, timestampMs);
        if (isFreshUser && hasPendingLocalEcho)
        {
            _localEchoSequences[threadId] = _lifecycleStartSequence;
            var opened = TryOpenPendingLifecycle(threadId, acceptedRunId: null);
            return new(
                Drop: opened is null,
                ConsumeEchoText: rawText.Trim(),
                RequestRemoteBackfill: false,
                OpenedLifecycleStart: opened);
        }
        if (isFreshUser && timestampMs > 0)
        {
            _remoteUserSeen.Add(threadId);
            var opened = TryOpenPendingLifecycle(threadId, acceptedRunId: null);
            return new(
                Drop: opened is null,
                ConsumeEchoText: null,
                RequestRemoteBackfill: false,
                OpenedLifecycleStart: opened);
        }
        if (isFreshUser && _remoteBackfillInFlight.Add(threadId))
        {
            return new(true, null, true, null);
        }
        return new(true, null, false, null);
    }

    internal ChatResetAgentGate EvaluateAgentEvent(
        AgentEventInfo evt,
        string threadId)
    {
        if (IsIgnoredRun(
                threadId,
                evt.RunId,
                evt,
                out var reloadHistory))
        {
            return new(true, reloadHistory, null);
        }

        var eventTimestamp = evt.Ts > 0 ? (long)evt.Ts : 0;
        if (!_awaitingUserMessage.Contains(threadId))
        {
            return new(
                IsPreResetTimestamp(threadId, eventTimestamp),
                false,
                null);
        }
        if (IsAcceptedPostResetLifecycleStart(
                threadId,
                evt,
                _lifecycleStartSequence + 1))
        {
            OpenGate(threadId, evt);
            return new(false, false, evt);
        }
        if (IsPreResetTimestamp(threadId, eventTimestamp))
            return new(true, false, null);
        if (ChatEventMapper.IsLifecycleStart(evt))
            BufferLifecycleStart(threadId, evt);
        return new(true, false, null);
    }

    internal AgentEventInfo? AddAcceptedRun(string threadId, string runId)
    {
        if (!_awaitingUserMessage.Contains(threadId))
            return null;
        if (!_acceptedRunIds.TryGetValue(threadId, out var runIds))
        {
            runIds = new HashSet<string>(StringComparer.Ordinal);
            _acceptedRunIds[threadId] = runIds;
        }
        runIds.Add(runId);
        return TryOpenPendingLifecycle(threadId, runId);
    }

    internal AgentEventInfo? RecordLocalSendWithoutRun(
        string threadId,
        long resetVersion,
        long lifecycleStartSequence)
    {
        _localSendWithoutRunVersions[threadId] = resetVersion;
        _localSendWithoutRunStartSequences[threadId] = lifecycleStartSequence;
        return TryOpenPendingLifecycle(threadId, acceptedRunId: null);
    }

    internal void CompleteRemoteBackfill(string threadId) =>
        _remoteBackfillInFlight.Remove(threadId);

    internal AgentEventInfo? RecordRemoteUser(string threadId)
    {
        _remoteUserSeen.Add(threadId);
        return TryOpenPendingLifecycle(threadId, acceptedRunId: null);
    }

    internal bool IsPreResetTimestamp(string threadId, long eventTimestampMs)
    {
        if (eventTimestampMs <= 0 ||
            !_cutoffUtcMs.TryGetValue(threadId, out var cutoff) ||
            cutoff <= 0)
        {
            return false;
        }
        return _versions.ContainsKey(threadId) &&
               eventTimestampMs + TimestampToleranceMs <= cutoff;
    }

    private bool TryConsumeSubmittedLocalEcho(string threadId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !_submittedLocalEchoTexts.TryGetValue(threadId, out var texts))
        {
            return false;
        }
        var normalized = text.Trim();
        if (!texts.TryGetValue(normalized, out var timestamps))
            return false;
        var now = DateTimeOffset.UtcNow;
        while (timestamps.Count > 0 &&
               now - timestamps.Peek() > LocalEchoWindow)
        {
            timestamps.Dequeue();
        }
        if (timestamps.Count == 0)
        {
            texts.Remove(normalized);
            if (texts.Count == 0)
                _submittedLocalEchoTexts.Remove(threadId);
            return false;
        }
        timestamps.Dequeue();
        if (timestamps.Count == 0)
            texts.Remove(normalized);
        if (texts.Count == 0)
            _submittedLocalEchoTexts.Remove(threadId);
        return true;
    }

    private bool IsIgnoredRun(
        string threadId,
        string? runId,
        AgentEventInfo evt,
        out bool reloadHistory)
    {
        reloadHistory = false;
        if (string.IsNullOrEmpty(runId) ||
            !_ignoredRunIds.TryGetValue(threadId, out var runIds) ||
            !runIds.Contains(runId))
        {
            return false;
        }
        if (ChatEventMapper.IsTerminalRunEvent(evt))
        {
            runIds.Remove(runId);
            if (runIds.Count == 0)
            {
                _ignoredRunIds.Remove(threadId);
                _submittedLocalEchoTexts.Remove(threadId);
            }
            reloadHistory = true;
        }
        return true;
    }

    private void BufferLifecycleStart(string threadId, AgentEventInfo evt)
    {
        if (!_pendingLifecycleStarts.TryGetValue(threadId, out var pending))
        {
            pending = [];
            _pendingLifecycleStarts[threadId] = pending;
        }
        if (!string.IsNullOrEmpty(evt.RunId) &&
            pending.Exists(item =>
                string.Equals(
                    item.Event.RunId,
                    evt.RunId,
                    StringComparison.Ordinal)))
        {
            return;
        }
        pending.Add(new PendingLifecycleStart(
            evt,
            ++_lifecycleStartSequence));
        if (pending.Count > 8)
            pending.RemoveRange(0, pending.Count - 8);
    }

    private AgentEventInfo? TryOpenPendingLifecycle(
        string threadId,
        string? acceptedRunId)
    {
        if (!_awaitingUserMessage.Contains(threadId) ||
            !_pendingLifecycleStarts.TryGetValue(threadId, out var pending))
        {
            return null;
        }
        for (var index = 0; index < pending.Count; index++)
        {
            var start = pending[index];
            if (acceptedRunId is not null)
            {
                if (!string.Equals(
                        start.Event.RunId,
                        acceptedRunId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else if (!IsAcceptedPostResetLifecycleStart(
                         threadId,
                         start.Event,
                         start.Sequence))
            {
                continue;
            }
            pending.RemoveAt(index);
            OpenGate(threadId, start.Event);
            return start.Event;
        }
        return null;
    }

    private bool IsAcceptedPostResetLifecycleStart(
        string threadId,
        AgentEventInfo evt,
        long lifecycleStartSequence)
    {
        if (!ChatEventMapper.IsLifecycleStart(evt))
            return false;
        if (!string.IsNullOrEmpty(evt.RunId) &&
            _acceptedRunIds.TryGetValue(threadId, out var accepted) &&
            accepted.Contains(evt.RunId))
        {
            return true;
        }
        if (_localSendWithoutRunVersions.TryGetValue(threadId, out var version) &&
            version == GetVersion(threadId) &&
            _localSendWithoutRunStartSequences.TryGetValue(
                threadId,
                out var startSequence) &&
            _localEchoSequences.TryGetValue(threadId, out var echoSequence) &&
            echoSequence >= startSequence &&
            lifecycleStartSequence > startSequence &&
            evt.Ts > 0 &&
            !IsPreResetTimestamp(threadId, (long)evt.Ts))
        {
            return true;
        }
        return _remoteUserSeen.Contains(threadId) &&
               !IsPreResetTimestamp(
                   threadId,
                   evt.Ts > 0 ? (long)evt.Ts : 0);
    }

    private void OpenGate(string threadId, AgentEventInfo evt)
    {
        _awaitingUserMessage.Remove(threadId);
        _remoteUserSeen.Remove(threadId);
        _localSendWithoutRunVersions.Remove(threadId);
        _localSendWithoutRunStartSequences.Remove(threadId);
        _localEchoSequences.Remove(threadId);
        _pendingLifecycleStarts.Remove(threadId);
        if (!string.IsNullOrEmpty(evt.RunId) &&
            _acceptedRunIds.TryGetValue(threadId, out var accepted))
        {
            accepted.Remove(evt.RunId);
            if (accepted.Count == 0)
                _acceptedRunIds.Remove(threadId);
        }
    }
}
