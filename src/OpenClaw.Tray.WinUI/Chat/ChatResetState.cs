using OpenClaw.Shared;
using OpenClawTray.Services;

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
    private readonly Dictionary<string, List<PendingLocalSubmission>>
        _pendingLocalSubmissions = new();
    private readonly Dictionary<string, List<PendingLifecycleStart>>
        _pendingLifecycleStarts = new();
    private readonly Dictionary<string, AcceptedLifecycleFloor>
        _acceptedLifecycleFloors = new();
    private readonly HashSet<string> _remoteBackfillInFlight = new();
    private readonly HashSet<string> _remoteUserSeen = new();

    private long _lifecycleStartSequence;

    private readonly record struct PendingLifecycleStart(
        AgentEventInfo Event,
        long Sequence);

    private sealed record PendingLocalSubmission(
        string Id,
        string Text,
        long Generation,
        DateTimeOffset SubmittedAt,
        long StartSequence,
        bool RequiresEcho)
    {
        internal bool EchoObserved { get; set; }
        internal long EchoTimestampMs { get; set; }
        internal bool ConfirmedWithoutRun { get; set; }
    }

    private readonly record struct AcceptedLifecycleFloor(
        string RunId,
        long Generation,
        long TimestampMs);

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
        _pendingLocalSubmissions.Remove(threadId);
        _pendingLifecycleStarts.Remove(threadId);
        _acceptedLifecycleFloors.Remove(threadId);
        _remoteBackfillInFlight.Remove(threadId);
        _remoteUserSeen.Remove(threadId);
        return generation;
    }

    internal void ClearSubmittedEchoesForReconnect()
    {
        _submittedLocalEchoTexts.Clear();
        _pendingLocalSubmissions.Clear();
        _acceptedLifecycleFloors.Clear();
    }

    internal void AddIgnoredRun(string threadId, string runId)
    {
        if (!_ignoredRunIds.TryGetValue(threadId, out var runIds))
        {
            runIds = new HashSet<string>(StringComparer.Ordinal);
            _ignoredRunIds[threadId] = runIds;
        }
        runIds.Add(runId);
        if (_acceptedLifecycleFloors.TryGetValue(
                threadId,
                out var floor) &&
            string.Equals(floor.RunId, runId, StringComparison.Ordinal))
        {
            _acceptedLifecycleFloors.Remove(threadId);
        }
    }

    internal void RegisterPendingLocalSubmission(
        string threadId,
        string submissionId,
        string text,
        long resetGeneration,
        long startSequence,
        DateTimeOffset submittedAt,
        bool requiresEcho = true)
    {
        if (!_awaitingUserMessage.Contains(threadId) ||
            resetGeneration != GetVersion(threadId) ||
            requiresEcho && string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (!_pendingLocalSubmissions.TryGetValue(
                threadId,
                out var submissions))
        {
            submissions = [];
            _pendingLocalSubmissions[threadId] = submissions;
        }
        submissions.RemoveAll(submission =>
            submission.Generation == resetGeneration &&
            string.Equals(
                submission.Id,
                submissionId,
                StringComparison.Ordinal));
        submissions.Add(new PendingLocalSubmission(
            submissionId,
            text.Trim(),
            resetGeneration,
            submittedAt,
            startSequence,
            requiresEcho));
        Logger.Debug(
            $"[ResetGate] Registered local submission thread='{threadId}' generation={resetGeneration} startSequence={startSequence} pending={submissions.Count}");
        PruneExpiredLocalSubmissions(threadId, submissions);
        if (submissions.Count > 32)
            submissions.RemoveRange(0, submissions.Count - 32);
    }

    internal void RemovePendingLocalSubmission(
        string threadId,
        string submissionId,
        long resetGeneration)
    {
        if (!_pendingLocalSubmissions.TryGetValue(
                threadId,
                out var submissions))
        {
            return;
        }
        submissions.RemoveAll(submission =>
            submission.Generation == resetGeneration &&
            string.Equals(
                submission.Id,
                submissionId,
                StringComparison.Ordinal));
        if (submissions.Count == 0)
            _pendingLocalSubmissions.Remove(threadId);
    }

    internal void CompleteRun(string threadId, string? runId)
    {
        _pendingLocalSubmissions.Remove(threadId);
        if (!_acceptedLifecycleFloors.TryGetValue(
                threadId,
                out var floor))
        {
            return;
        }
        if (string.IsNullOrEmpty(runId) ||
            string.Equals(floor.RunId, runId, StringComparison.Ordinal))
        {
            _acceptedLifecycleFloors.Remove(threadId);
        }
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
        bool hasPendingLocalEcho,
        string? activeRunId = null)
    {
        var isNormalUserText = role == "user" &&
            !ChatContentFormatting.LooksLikeApprovalSlashCommand(rawText) &&
            !NativeToolProjector.LooksLikeSystemControlNote(rawText);

        if (isNormalUserText &&
            TryMatchPendingLocalSubmission(
                threadId,
                rawText,
                out var localSubmission))
        {
            localSubmission.EchoObserved = true;
            localSubmission.EchoTimestampMs = timestampMs;
            _localEchoSequences[threadId] = _lifecycleStartSequence;
            var opened = _awaitingUserMessage.Contains(threadId)
                ? TryOpenPendingLifecycle(
                    threadId,
                    acceptedRunId: null,
                    localSubmission)
                : null;
            if (opened is null &&
                !_awaitingUserMessage.Contains(threadId))
            {
                LowerAcceptedLifecycleFloor(
                    threadId,
                    activeRunId,
                    timestampMs);
            }
            Logger.Debug(
                $"[ResetGate] Matched local echo thread='{threadId}' generation={GetVersion(threadId)} awaiting={_awaitingUserMessage.Contains(threadId)} hasPending={hasPendingLocalEcho} opened={opened is not null} pendingStarts={PendingLifecycleCount(threadId)} timestampDeltaMs={TimestampDeltaFromCutoff(threadId, timestampMs)}");
            return new(
                Drop: true,
                ConsumeEchoText: rawText.Trim(),
                RequestRemoteBackfill: false,
                OpenedLifecycleStart: opened);
        }

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
            var timestampAccepted = role == "user"
                ? !IsPreResetTimestamp(threadId, timestampMs)
                : IsTimestampAcceptedForRun(
                    threadId,
                    activeRunId,
                    timestampMs);
            return new(
                !timestampAccepted,
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
        if (isNormalUserText)
        {
            Logger.Debug(
                $"[ResetGate] User echo did not open thread='{threadId}' generation={GetVersion(threadId)} hasPending={hasPendingLocalEcho} isFresh={isFreshUser} localProof={HasRecentCurrentLocalSubmission(threadId)} pendingStarts={PendingLifecycleCount(threadId)} timestampDeltaMs={TimestampDeltaFromCutoff(threadId, timestampMs)}");
        }
        return new(true, null, false, null);
    }

    internal ChatResetAgentGate EvaluateAgentEvent(
        AgentEventInfo evt,
        string threadId)
    {
        if (string.Equals(
                evt.Stream,
                "lifecycle",
                StringComparison.OrdinalIgnoreCase))
        {
            var phase = evt.Data.ValueKind ==
                        System.Text.Json.JsonValueKind.Object &&
                        evt.Data.TryGetProperty("phase", out var phaseProperty)
                ? phaseProperty.GetString()
                : null;
            Logger.Debug(
                $"[ResetGate] Lifecycle event thread='{threadId}' phase='{phase ?? "(none)"}' runPresent={!string.IsNullOrEmpty(evt.RunId)} awaiting={_awaitingUserMessage.Contains(threadId)} timestampDeltaMs={TimestampDeltaFromCutoff(threadId, evt.Ts > 0 ? (long)evt.Ts : 0)}");
        }
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
                !IsTimestampAcceptedForRun(
                    threadId,
                    evt.RunId,
                    eventTimestamp),
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
        {
            var hasLocalProof =
                HasRecentCurrentLocalSubmission(threadId);
            if (IsResetLifecycleCandidate(evt) &&
                hasLocalProof)
            {
                BufferLifecycleStart(threadId, evt);
            }
            Logger.Debug(
                $"[ResetGate] Pre-cutoff agent event thread='{threadId}' stream='{evt.Stream}' runPresent={!string.IsNullOrEmpty(evt.RunId)} localProof={hasLocalProof} buffered={IsResetLifecycleCandidate(evt) && hasLocalProof} timestampDeltaMs={TimestampDeltaFromCutoff(threadId, eventTimestamp)}");
            return new(true, false, null);
        }
        if (IsResetLifecycleCandidate(evt))
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
        long lifecycleStartSequence,
        string? submissionId = null)
    {
        _localSendWithoutRunVersions[threadId] = resetVersion;
        _localSendWithoutRunStartSequences[threadId] = lifecycleStartSequence;
        PendingLocalSubmission? submission = null;
        if (!string.IsNullOrEmpty(submissionId) &&
            _pendingLocalSubmissions.TryGetValue(
                threadId,
                out var submissions))
        {
            submission = submissions.FirstOrDefault(candidate =>
                candidate.Generation == resetVersion &&
                string.Equals(
                    candidate.Id,
                    submissionId,
                    StringComparison.Ordinal));
            if (submission is not null)
                submission.ConfirmedWithoutRun = true;
        }
        if (submission is { RequiresEcho: false })
        {
            return TryOpenPendingLifecycle(
                threadId,
                acceptedRunId: null,
                submission);
        }
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

    private bool IsTimestampAcceptedForRun(
        string threadId,
        string? runId,
        long eventTimestampMs)
    {
        if (!IsPreResetTimestamp(threadId, eventTimestampMs))
            return true;
        return !string.IsNullOrEmpty(runId) &&
            _acceptedLifecycleFloors.TryGetValue(
                threadId,
                out var floor) &&
            floor.Generation == GetVersion(threadId) &&
            string.Equals(floor.RunId, runId, StringComparison.Ordinal) &&
            eventTimestampMs >= floor.TimestampMs;
    }

    private void LowerAcceptedLifecycleFloor(
        string threadId,
        string? runId,
        long timestampMs)
    {
        if (string.IsNullOrEmpty(runId) ||
            timestampMs <= 0 ||
            !_acceptedLifecycleFloors.TryGetValue(
                threadId,
                out var floor) ||
            floor.Generation != GetVersion(threadId) ||
            !string.Equals(
                floor.RunId,
                runId,
                StringComparison.Ordinal) ||
            timestampMs >= floor.TimestampMs)
        {
            return;
        }
        _acceptedLifecycleFloors[threadId] =
            floor with { TimestampMs = timestampMs };
    }

    private long? TimestampDeltaFromCutoff(
        string threadId,
        long eventTimestampMs) =>
        eventTimestampMs > 0 &&
        _cutoffUtcMs.TryGetValue(threadId, out var cutoff)
            ? eventTimestampMs - cutoff
            : null;

    private int PendingLifecycleCount(string threadId) =>
        _pendingLifecycleStarts.TryGetValue(
            threadId,
            out var pending)
            ? pending.Count
            : 0;

    private bool TryMatchPendingLocalSubmission(
        string threadId,
        string text,
        out PendingLocalSubmission submission)
    {
        submission = null!;
        if (string.IsNullOrWhiteSpace(text) ||
            !_pendingLocalSubmissions.TryGetValue(
                threadId,
                out var submissions))
        {
            return false;
        }
        PruneExpiredLocalSubmissions(threadId, submissions);
        var generation = GetVersion(threadId);
        var normalized = text.Trim();
        var cutoff = _cutoffUtcMs.TryGetValue(threadId, out var value)
            ? value
            : 0;
        submission = submissions.FirstOrDefault(candidate =>
            candidate.Generation == generation &&
            candidate.SubmittedAt.ToUnixTimeMilliseconds() >= cutoff &&
            !candidate.EchoObserved &&
            string.Equals(
                candidate.Text,
                normalized,
                StringComparison.Ordinal))!;
        return submission is not null;
    }

    private bool HasRecentCurrentLocalSubmission(string threadId)
    {
        if (!_pendingLocalSubmissions.TryGetValue(
                threadId,
                out var submissions))
        {
            return false;
        }
        PruneExpiredLocalSubmissions(threadId, submissions);
        var generation = GetVersion(threadId);
        var cutoff = _cutoffUtcMs.TryGetValue(threadId, out var value)
            ? value
            : 0;
        return submissions.Any(submission =>
            submission.Generation == generation &&
            submission.SubmittedAt.ToUnixTimeMilliseconds() >= cutoff);
    }

    private PendingLocalSubmission? FindAcceptedLocalSubmission(
        string threadId,
        long lifecycleStartSequence)
    {
        if (!_pendingLocalSubmissions.TryGetValue(
                threadId,
                out var submissions))
        {
            return null;
        }
        PruneExpiredLocalSubmissions(threadId, submissions);
        var generation = GetVersion(threadId);
        return submissions.FirstOrDefault(submission =>
            submission.Generation == generation &&
            (submission.EchoObserved ||
             !submission.RequiresEcho &&
             submission.ConfirmedWithoutRun) &&
            lifecycleStartSequence > submission.StartSequence);
    }

    private void PruneExpiredLocalSubmissions(
        string threadId,
        List<PendingLocalSubmission> submissions)
    {
        var now = DateTimeOffset.UtcNow;
        submissions.RemoveAll(submission =>
            now - submission.SubmittedAt > LocalEchoWindow);
        if (submissions.Count == 0)
            _pendingLocalSubmissions.Remove(threadId);
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
        string? acceptedRunId,
        PendingLocalSubmission? localSubmission = null)
    {
        if (!_awaitingUserMessage.Contains(threadId) ||
            !_pendingLifecycleStarts.TryGetValue(threadId, out var pending))
        {
            return null;
        }

        if (localSubmission is not null)
        {
            var selectedIndex = -1;
            if (_acceptedRunIds.TryGetValue(
                    threadId,
                    out var acceptedRuns))
            {
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    var candidate = pending[index];
                    if (candidate.Sequence > localSubmission.StartSequence &&
                        !string.IsNullOrEmpty(candidate.Event.RunId) &&
                        acceptedRuns.Contains(candidate.Event.RunId))
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }

            if (selectedIndex < 0)
            {
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    if (pending[index].Sequence >
                        localSubmission.StartSequence)
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }

            if (selectedIndex < 0)
                return null;

            var selected = pending[selectedIndex];
            pending.RemoveAt(selectedIndex);
            OpenGate(
                threadId,
                selected.Event,
                localSubmission.EchoTimestampMs);
            return selected.Event;
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
            OpenGate(
                threadId,
                start.Event,
                acceptedEchoTimestampMs: null);
            return start.Event;
        }
        return null;
    }

    private bool IsAcceptedPostResetLifecycleStart(
        string threadId,
        AgentEventInfo evt,
        long lifecycleStartSequence)
    {
        if (!IsResetLifecycleCandidate(evt))
            return false;
        if (!string.IsNullOrEmpty(evt.RunId) &&
            _acceptedRunIds.TryGetValue(threadId, out var accepted) &&
            accepted.Contains(evt.RunId))
        {
            return true;
        }
        if (FindAcceptedLocalSubmission(
                threadId,
                lifecycleStartSequence) is not null)
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

    private void OpenGate(
        string threadId,
        AgentEventInfo evt,
        long? acceptedEchoTimestampMs = null)
    {
        _awaitingUserMessage.Remove(threadId);
        _remoteUserSeen.Remove(threadId);
        _localSendWithoutRunVersions.Remove(threadId);
        _localSendWithoutRunStartSequences.Remove(threadId);
        _localEchoSequences.Remove(threadId);
        _pendingLifecycleStarts.Remove(threadId);
        if (acceptedEchoTimestampMs is null)
        {
            acceptedEchoTimestampMs =
                FindAcceptedLocalSubmission(
                    threadId,
                    _lifecycleStartSequence + 1)?
                .EchoTimestampMs;
        }
        var floorTimestamp = evt.Ts > 0 ? (long)evt.Ts : 0;
        if (acceptedEchoTimestampMs is > 0 &&
            (floorTimestamp <= 0 ||
             acceptedEchoTimestampMs.Value < floorTimestamp))
        {
            floorTimestamp = acceptedEchoTimestampMs.Value;
        }
        if (!string.IsNullOrEmpty(evt.RunId) && floorTimestamp > 0)
        {
            _acceptedLifecycleFloors[threadId] =
                new AcceptedLifecycleFloor(
                    evt.RunId,
                    GetVersion(threadId),
                    floorTimestamp);
        }
        else
        {
            _acceptedLifecycleFloors.Remove(threadId);
        }
        if (!string.IsNullOrEmpty(evt.RunId) &&
            _acceptedRunIds.TryGetValue(threadId, out var accepted))
        {
            accepted.Remove(evt.RunId);
            if (accepted.Count == 0)
                _acceptedRunIds.Remove(threadId);
        }
    }

    private static bool IsResetLifecycleCandidate(
        AgentEventInfo evt)
    {
        if (ChatEventMapper.IsLifecycleStart(evt))
            return true;
        return string.Equals(
                   evt.Stream,
                   "lifecycle",
                   StringComparison.OrdinalIgnoreCase) &&
               evt.Data.ValueKind ==
                   System.Text.Json.JsonValueKind.Object &&
               evt.Data.TryGetProperty(
                   "phase",
                   out var phaseProperty) &&
               string.Equals(
                   phaseProperty.GetString(),
                   "fallback_step",
                   StringComparison.OrdinalIgnoreCase);
    }
}
