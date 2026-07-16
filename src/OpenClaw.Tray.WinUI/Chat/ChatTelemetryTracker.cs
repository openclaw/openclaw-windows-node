using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenClaw.Shared.Telemetry;

namespace OpenClawTray.Chat;

internal enum ChatTelemetryOutcome
{
    Success,
    Failure,
    Canceled,
}

internal enum ChatTurnTelemetryReason
{
    AssistantFinal,
    LifecycleEnd,
    LifecycleError,
    SendRejected,
    QueuedCanceled,
    AbortRequested,
    Reset,
    Superseded,
    Disconnected,
    Disposed,
    Other,
}

internal enum ChatAdmissionTelemetryStatus
{
    Accepted,
    Deferred,
    Rejected,
    Canceled,
    Exception,
    Other,
}

internal enum ChatHistoryTelemetrySource
{
    Initial,
    Forced,
}

internal enum ChatBackfillTelemetryReason
{
    RemoteTurn,
    ResetReconciliation,
}

internal enum ChatTerminalEventDropReason
{
    MissingRunId,
    MismatchedRunId,
}

internal enum ChatResponseOutputKind
{
    None,
    Assistant,
    Reasoning,
    Tool,
    Other,
}

internal sealed class ChatTelemetryTracker
{
    internal const string TurnSpanName = "openclaw.chat.turn";
    internal const string SendSpanName = "openclaw.chat.send";
    internal const string ResponseWaitSpanName = "openclaw.chat.response.wait";
    internal const string ResponseReceiveSpanName = "openclaw.chat.response.receive";
    internal const string HistoryLoadSpanName = "openclaw.chat.history.load";
    internal const string HistoryBackfillSpanName = "openclaw.chat.history.backfill";

    internal const string TurnsMetricName = "openclaw.chat.turns";
    internal const string TurnDurationMetricName = "openclaw.chat.turn.duration";
    internal const string QueueWaitDurationMetricName = "openclaw.chat.queue.wait.duration";
    internal const string SendAttemptsMetricName = "openclaw.chat.send.attempts";
    internal const string SendDurationMetricName = "openclaw.chat.send.duration";
    internal const string ResponseWaitDurationMetricName = "openclaw.chat.response.wait.duration";
    internal const string ResponseReceiveDurationMetricName = "openclaw.chat.response.receive.duration";
    internal const string HistoryLoadsMetricName = "openclaw.chat.history.loads";
    internal const string HistoryLoadDurationMetricName = "openclaw.chat.history.load.duration";
    internal const string HistoryBackfillsMetricName = "openclaw.chat.history.backfills";
    internal const string HistoryBackfillDurationMetricName = "openclaw.chat.history.backfill.duration";
    internal const string DroppedRemoteTurnsMetricName = "openclaw.chat.remote_turns.dropped";
    internal const string DroppedTerminalEventsMetricName = "openclaw.chat.terminal_events.dropped";

    internal const string AdmissionStatusTag = "openclaw.chat.admission.status";
    internal const string BackfillReasonTag = "openclaw.chat.backfill.reason";
    internal const string DroppedRemoteTurnReasonTag = "openclaw.chat.remote_turn.drop.reason";
    internal const string DroppedTerminalEventReasonTag = "openclaw.chat.terminal_event.drop.reason";
    internal const string FirstOutputKindTag = "openclaw.chat.response.first_output";

    private const string SourceLocal = "local";
    private const string SourceRemote = "remote";
    private const string MissingRunId = "missing_run_id";
    private const string MismatchedRunId = "mismatched_run_id";

    private static readonly Counter<long> Turns = OpenClawTelemetry.CreateCounter(
        TurnsMetricName,
        unit: "{turn}",
        description: "Number of observed OpenClaw chat turns.");
    private static readonly Histogram<double> TurnDuration = OpenClawTelemetry.CreateHistogram(
        TurnDurationMetricName,
        unit: "ms",
        description: "Duration of observed OpenClaw chat turns.");
    private static readonly Histogram<double> QueueWaitDuration = OpenClawTelemetry.CreateHistogram(
        QueueWaitDurationMetricName,
        unit: "ms",
        description: "Cumulative local queue dwell for completed OpenClaw chat turns.");
    private static readonly Counter<long> SendAttempts = OpenClawTelemetry.CreateCounter(
        SendAttemptsMetricName,
        unit: "{attempt}",
        description: "Number of OpenClaw chat.send RPC attempts.");
    private static readonly Histogram<double> SendDuration = OpenClawTelemetry.CreateHistogram(
        SendDurationMetricName,
        unit: "ms",
        description: "Duration of OpenClaw chat.send RPC attempts.");
    private static readonly Histogram<double> ResponseWaitDuration = OpenClawTelemetry.CreateHistogram(
        ResponseWaitDurationMetricName,
        unit: "ms",
        description: "Duration from chat admission or lifecycle start to the first accepted inbound output.");
    private static readonly Histogram<double> ResponseReceiveDuration = OpenClawTelemetry.CreateHistogram(
        ResponseReceiveDurationMetricName,
        unit: "ms",
        description: "Duration from the first accepted inbound output to terminal chat completion.");
    private static readonly Counter<long> HistoryLoads = OpenClawTelemetry.CreateCounter(
        HistoryLoadsMetricName,
        unit: "{load}",
        description: "Number of full OpenClaw chat history loads.");
    private static readonly Histogram<double> HistoryLoadDuration = OpenClawTelemetry.CreateHistogram(
        HistoryLoadDurationMetricName,
        unit: "ms",
        description: "Duration of full OpenClaw chat history loads.");
    private static readonly Counter<long> HistoryBackfills = OpenClawTelemetry.CreateCounter(
        HistoryBackfillsMetricName,
        unit: "{backfill}",
        description: "Number of targeted OpenClaw chat history backfills.");
    private static readonly Histogram<double> HistoryBackfillDuration = OpenClawTelemetry.CreateHistogram(
        HistoryBackfillDurationMetricName,
        unit: "ms",
        description: "Duration of targeted OpenClaw chat history backfills.");
    private static readonly Counter<long> DroppedRemoteTurns = OpenClawTelemetry.CreateCounter(
        DroppedRemoteTurnsMetricName,
        unit: "{turn}",
        description: "Number of remote OpenClaw chat turns not traced because safe correlation was unavailable.");
    private static readonly Counter<long> DroppedTerminalEvents = OpenClawTelemetry.CreateCounter(
        DroppedTerminalEventsMetricName,
        unit: "{event}",
        description: "Number of terminal OpenClaw chat events not applied because safe correlation was unavailable.");

    private readonly object _gate = new();
    private readonly Dictionary<string, TurnState> _turnsByMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnState> _turnsByRunId = new(StringComparer.Ordinal);

    public void StartLocalTurn(string messageId, string threadId, bool queued)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_gate)
        {
            if (_turnsByMessageId.ContainsKey(messageId))
                return;

            var state = new TurnState(
                messageId,
                threadId,
                SourceLocal,
                OpenClawTelemetry.StartDetachedActivity(
                    TurnSpanName,
                    default(ActivityContext),
                    [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, SourceLocal)]),
                Stopwatch.GetTimestamp());
            if (queued)
                state.StartQueueSegment();

            _turnsByMessageId.Add(messageId, state);
        }
    }

    public void DispatchLocalTurn(string messageId, string provisionalRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionalRunId);

        lock (_gate)
        {
            if (!_turnsByMessageId.TryGetValue(messageId, out var state))
                return;

            state.EndQueueSegment();
            state.IsDispatched = true;
            BindRunLocked(state, provisionalRunId);
        }
    }

    public void RequeueLocalTurn(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_gate)
        {
            if (!_turnsByMessageId.TryGetValue(messageId, out var state))
                return;

            RemoveRunMappingsLocked(state);
            state.IsDispatched = false;
            state.StartQueueSegment();
        }
    }

    public void BindAcceptedRun(string messageId, string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        lock (_gate)
        {
            if (_turnsByMessageId.TryGetValue(messageId, out var state))
                BindRunLocked(state, runId);
        }
    }

    public void ObserveAdmissionAccepted(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (_gate)
        {
            if (_turnsByMessageId.TryGetValue(messageId, out var state))
                StartResponseWaitLocked(state);
        }
    }

    public void ObserveLifecycleStart(string threadId, string? runId, bool allowRemoteTurn = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (string.IsNullOrWhiteSpace(runId))
        {
            if (!allowRemoteTurn)
                return;

            lock (_gate)
            {
                if (_turnsByMessageId.Values.Any(
                    state => state.Source == SourceLocal &&
                        state.ThreadId == threadId &&
                        state.IsDispatched))
                {
                    return;
                }
            }

            OpenClawTelemetry.Add(
                DroppedRemoteTurns,
                tags: [OpenClawTelemetryTag.String(DroppedRemoteTurnReasonTag, MissingRunId)]);
            return;
        }

        lock (_gate)
        {
            if (_turnsByRunId.TryGetValue(runId, out var existing))
            {
                StartResponseWaitLocked(existing);
                return;
            }

            var pendingLocal = _turnsByMessageId.Values.FirstOrDefault(
                state => state.Source == SourceLocal &&
                    state.ThreadId == threadId &&
                    state.IsDispatched);
            if (pendingLocal is not null)
            {
                BindRunLocked(pendingLocal, runId);
                StartResponseWaitLocked(pendingLocal);
                return;
            }
            if (!allowRemoteTurn)
                return;

            var remote = new TurnState(
                messageId: null,
                threadId,
                SourceRemote,
                OpenClawTelemetry.StartDetachedActivity(
                    TurnSpanName,
                    default(ActivityContext),
                    [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, SourceRemote)]),
                Stopwatch.GetTimestamp());
            BindRunLocked(remote, runId);
            StartResponseWaitLocked(remote);
        }
    }

    public bool ObserveInboundOutput(
        string threadId,
        string? runId,
        ChatResponseOutputKind outputKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ResponsePhaseCompletion? waitCompletion;
        lock (_gate)
        {
            var state = ResolveTurnForOutputLocked(threadId, runId);
            if (state is null || state.ReceivePhase is not null)
                return false;

            if (state.WaitPhase is not { } waitPhase)
                return false;

            var now = Stopwatch.GetTimestamp();
            state.FirstOutputKind = outputKind;
            state.ResponseWaitDurationMilliseconds =
                Stopwatch.GetElapsedTime(waitPhase.StartTimestamp, now).TotalMilliseconds;
            waitCompletion = new ResponsePhaseCompletion(
                waitPhase.Activity,
                state.Source,
                outputKind,
                ChatTelemetryOutcome.Success);
            state.PendingWaitCompletion = waitCompletion;
            state.WaitPhase = null;

            state.ReceivePhase = StartResponsePhase(state, ResponseReceiveSpanName, now);
        }

        CompleteResponsePhase(waitCompletion);
        return true;
    }

    public ChatTelemetryOperation? StartSendAttempt(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_gate)
        {
            if (!_turnsByMessageId.TryGetValue(messageId, out var state))
                return null;

            var activity = state.Activity is not null
                ? OpenClawTelemetry.StartDetachedActivity(
                    SendSpanName,
                    state.Activity.Context,
                    [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, SourceLocal)])
                : OpenClawTelemetry.StartDetachedActivity(
                    SendSpanName,
                    default(ActivityContext),
                    [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, SourceLocal)]);
            return new ChatTelemetryOperation(activity, Stopwatch.GetTimestamp());
        }
    }

    public void FinishSendAttempt(
        ChatTelemetryOperation? operation,
        ChatAdmissionTelemetryStatus status,
        ChatTelemetryOutcome outcome,
        Exception? exception = null)
    {
        if (operation is null || !operation.TryFinish())
            return;

        var tags = new[]
        {
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(outcome)),
            OpenClawTelemetryTag.String(AdmissionStatusTag, ToTelemetryValue(status)),
        };
        FinishActivity(operation.Activity, outcome, tags, exception);
        OpenClawTelemetry.Add(SendAttempts, tags: tags);
        OpenClawTelemetry.Record(
            SendDuration,
            Stopwatch.GetElapsedTime(operation.StartTimestamp).TotalMilliseconds,
            tags);
    }

    public bool FinishByMessageId(
        string messageId,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        var completion = PrepareFinishByMessageId(messageId, outcome, reason);
        return CompletePreparedTurn(completion);
    }

    public PreparedTurnCompletion? PrepareFinishByMessageId(
        string messageId,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (_gate)
        {
            if (!_turnsByMessageId.TryGetValue(messageId, out var state))
                return null;

            var completion = PrepareTurnCompletion(state, outcome, reason);
            RemoveTurnLocked(state);
            return completion;
        }
    }

    public bool CompletePreparedTurn(PreparedTurnCompletion? completion)
    {
        if (completion is null || !completion.TryComplete())
            return false;

        FinishTurn(completion);
        return true;
    }

    public bool FinishByRunId(
        string? runId,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        var completion = PrepareFinishByRunId(runId, outcome, reason);
        return CompletePreparedTurn(completion);
    }

    public PreparedTurnCompletion? PrepareFinishByRunId(
        string? runId,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        lock (_gate)
        {
            if (!_turnsByRunId.TryGetValue(runId, out var state))
                return null;

            var completion = PrepareTurnCompletion(state, outcome, reason);
            RemoveTurnLocked(state);
            return completion;
        }
    }

    public bool FinishActiveTurn(
        string threadId,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        TurnState? state;
        lock (_gate)
        {
            var active = _turnsByRunId.Values
                .Concat(_turnsByMessageId.Values)
                .Distinct()
                .Where(candidate => candidate.ThreadId == threadId && candidate.IsDispatched)
                .Take(2)
                .ToArray();
            if (active.Length != 1)
                return false;

            state = active[0];
            RemoveTurnLocked(state);
        }

        FinishTurn(state, outcome, reason);
        return true;
    }

    public void FinishThread(
        string threadId,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        FinishStates(
            RemoveWhere(state => state.ThreadId == threadId),
            outcome,
            reason);
    }

    public void FinishAll(ChatTelemetryOutcome outcome, ChatTurnTelemetryReason reason) =>
        FinishStates(RemoveWhere(static _ => true), outcome, reason);

    public ChatTelemetryOperation StartHistoryLoad(ChatHistoryTelemetrySource source)
    {
        var tags = new[]
        {
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, ToTelemetryValue(source)),
        };
        return new ChatTelemetryOperation(
            OpenClawTelemetry.StartDetachedActivity(
                HistoryLoadSpanName,
                default(ActivityContext),
                tags),
            Stopwatch.GetTimestamp(),
            tags);
    }

    public void FinishHistoryLoad(
        ChatTelemetryOperation operation,
        ChatTelemetryOutcome outcome,
        Exception? exception = null)
    {
        if (!operation.TryFinish())
            return;

        var tags = AppendOutcome(operation.Tags, outcome);
        FinishActivity(operation.Activity, outcome, tags, exception);
        OpenClawTelemetry.Add(HistoryLoads, tags: tags);
        OpenClawTelemetry.Record(
            HistoryLoadDuration,
            Stopwatch.GetElapsedTime(operation.StartTimestamp).TotalMilliseconds,
            tags);
    }

    public ChatTelemetryOperation StartHistoryBackfill(ChatBackfillTelemetryReason reason)
    {
        var tags = new[]
        {
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, SourceRemote),
            OpenClawTelemetryTag.String(BackfillReasonTag, ToTelemetryValue(reason)),
        };
        return new ChatTelemetryOperation(
            OpenClawTelemetry.StartDetachedActivity(
                HistoryBackfillSpanName,
                default(ActivityContext),
                tags),
            Stopwatch.GetTimestamp(),
            tags);
    }

    public void FinishHistoryBackfill(
        ChatTelemetryOperation operation,
        ChatTelemetryOutcome outcome,
        Exception? exception = null)
    {
        if (!operation.TryFinish())
            return;

        var tags = AppendOutcome(operation.Tags, outcome);
        FinishActivity(operation.Activity, outcome, tags, exception);
        OpenClawTelemetry.Add(HistoryBackfills, tags: tags);
        OpenClawTelemetry.Record(
            HistoryBackfillDuration,
            Stopwatch.GetElapsedTime(operation.StartTimestamp).TotalMilliseconds,
            tags);
    }

    public void RecordDroppedTerminalEvent(ChatTerminalEventDropReason reason)
    {
        OpenClawTelemetry.Add(
            DroppedTerminalEvents,
            tags:
            [
                OpenClawTelemetryTag.String(
                    DroppedTerminalEventReasonTag,
                    ToTelemetryValue(reason)),
            ]);
    }

    private TurnState[] RemoveWhere(Func<TurnState, bool> predicate)
    {
        lock (_gate)
        {
            var states = _turnsByMessageId.Values
                .Concat(_turnsByRunId.Values)
                .Distinct()
                .Where(predicate)
                .ToArray();
            foreach (var state in states)
                RemoveTurnLocked(state);
            return states;
        }
    }

    private static void FinishStates(
        IEnumerable<TurnState> states,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        foreach (var state in states)
            FinishTurn(state, outcome, reason);
    }

    private static void FinishTurn(
        TurnState state,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        FinishTurn(PrepareTurnCompletion(state, outcome, reason));
    }

    private static PreparedTurnCompletion PrepareTurnCompletion(
        TurnState state,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        var endTimestamp = Stopwatch.GetTimestamp();
        state.EndQueueSegment(endTimestamp);
        ResponsePhaseCompletion? pendingWaitCompletion = state.PendingWaitCompletion;
        if (state.WaitPhase is { } waitPhase)
        {
            state.ResponseWaitDurationMilliseconds =
                Stopwatch.GetElapsedTime(waitPhase.StartTimestamp, endTimestamp).TotalMilliseconds;
            pendingWaitCompletion = new ResponsePhaseCompletion(
                waitPhase.Activity,
                state.Source,
                ChatResponseOutputKind.None,
                outcome);
        }
        ResponsePhaseCompletion? receiveCompletion = null;
        if (state.ReceivePhase is { } receivePhase)
        {
            state.ResponseReceiveDurationMilliseconds =
                Stopwatch.GetElapsedTime(receivePhase.StartTimestamp, endTimestamp).TotalMilliseconds;
            receiveCompletion = new ResponsePhaseCompletion(
                receivePhase.Activity,
                state.Source,
                state.FirstOutputKind,
                outcome);
        }
        return new PreparedTurnCompletion(
            state.Activity,
            state.StartTimestamp,
            endTimestamp,
            state.Source,
            state.WasQueued,
            state.QueuedDurationMilliseconds,
            state.ResponseWaitStarted,
            state.ResponseWaitDurationMilliseconds,
            state.ReceivePhase is not null,
            state.ResponseReceiveDurationMilliseconds,
            state.FirstOutputKind,
            pendingWaitCompletion,
            receiveCompletion,
            outcome,
            reason);
    }

    private static void FinishTurn(PreparedTurnCompletion completion)
    {
        CompleteResponsePhase(completion.WaitCompletion);
        CompleteResponsePhase(completion.ReceiveCompletion);
        var tags = new[]
        {
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, completion.Source),
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(completion.Outcome)),
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Reason, ToTelemetryValue(completion.Reason)),
        };
        FinishActivity(completion.Activity, completion.Outcome, tags);
        OpenClawTelemetry.Add(Turns, tags: tags);
        OpenClawTelemetry.Record(
            TurnDuration,
            Stopwatch.GetElapsedTime(completion.StartTimestamp, completion.EndTimestamp).TotalMilliseconds,
            [
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, completion.Source),
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(completion.Outcome)),
            ]);
        if (completion.WasQueued)
        {
            OpenClawTelemetry.Record(
                QueueWaitDuration,
                completion.QueuedDurationMilliseconds,
                [OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(completion.Outcome))]);
        }
        if (completion.ResponseWaitStarted)
        {
            OpenClawTelemetry.Record(
                ResponseWaitDuration,
                completion.ResponseWaitDurationMilliseconds,
                ResponseMetricTags(completion));
        }
        if (completion.ResponseReceiveStarted)
        {
            OpenClawTelemetry.Record(
                ResponseReceiveDuration,
                completion.ResponseReceiveDurationMilliseconds,
                ResponseMetricTags(completion));
        }
    }

    private static OpenClawTelemetryTag[] ResponseMetricTags(PreparedTurnCompletion completion) =>
    [
        OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, completion.Source),
        OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(completion.Outcome)),
        OpenClawTelemetryTag.String(FirstOutputKindTag, ToTelemetryValue(completion.FirstOutputKind)),
    ];

    private static void CompleteResponsePhase(ResponsePhaseCompletion? completion)
    {
        if (completion is null || !completion.TryComplete())
            return;

        FinishActivity(
            completion.Activity,
            completion.Outcome,
            [
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, completion.Source),
                OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(completion.Outcome)),
                OpenClawTelemetryTag.String(FirstOutputKindTag, ToTelemetryValue(completion.FirstOutputKind)),
            ]);
    }

    private static void FinishActivity(
        Activity? activity,
        ChatTelemetryOutcome outcome,
        IEnumerable<OpenClawTelemetryTag> tags,
        Exception? exception = null)
    {
        if (activity is null)
            return;

        foreach (var tag in tags)
            activity.SetTag(tag.Key, tag.Value);

        switch (outcome)
        {
            case ChatTelemetryOutcome.Success:
                activity.SetStatus(ActivityStatusCode.Ok);
                break;
            case ChatTelemetryOutcome.Failure:
                activity.SetStatus(ActivityStatusCode.Error, exception?.GetType().Name);
                if (exception is not null)
                {
                    activity.SetTag(
                        OpenClawTelemetryTagKey.ErrorType.ToTelemetryName(),
                        exception.GetType().FullName);
                }
                break;
            case ChatTelemetryOutcome.Canceled:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown chat telemetry outcome.");
        }

        OpenClawTelemetry.StopDetachedActivity(activity);
    }

    private static OpenClawTelemetryTag[] AppendOutcome(
        IReadOnlyList<OpenClawTelemetryTag> tags,
        ChatTelemetryOutcome outcome) =>
        [.. tags, OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, ToTelemetryValue(outcome))];

    private void BindRunLocked(TurnState state, string runId)
    {
        state.RunIds.Add(runId);
        _turnsByRunId[runId] = state;
    }

    private static void StartResponseWaitLocked(TurnState state)
    {
        if (state.ResponseWaitStarted)
            return;

        state.ResponseWaitStarted = true;
        state.WaitPhase = StartResponsePhase(
            state,
            ResponseWaitSpanName,
            Stopwatch.GetTimestamp());
    }

    private static ResponsePhase StartResponsePhase(
        TurnState state,
        string spanName,
        long startTimestamp)
    {
        var tags = new[]
        {
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Source, state.Source),
        };
        var activity = state.Activity is not null
            ? OpenClawTelemetry.StartDetachedActivity(spanName, state.Activity.Context, tags)
            : OpenClawTelemetry.StartDetachedActivity(spanName, default(ActivityContext), tags);
        return new ResponsePhase(activity, startTimestamp);
    }

    private TurnState? ResolveTurnForOutputLocked(string threadId, string? runId)
    {
        if (!string.IsNullOrWhiteSpace(runId))
            return _turnsByRunId.GetValueOrDefault(runId);

        var active = _turnsByRunId.Values
            .Concat(_turnsByMessageId.Values)
            .Distinct()
            .Where(candidate => candidate.ThreadId == threadId && candidate.IsDispatched)
            .Take(2)
            .ToArray();
        return active.Length == 1 ? active[0] : null;
    }

    private void RemoveTurnLocked(TurnState state)
    {
        if (state.MessageId is not null)
            _turnsByMessageId.Remove(state.MessageId);
        RemoveRunMappingsLocked(state);
    }

    private void RemoveRunMappingsLocked(TurnState state)
    {
        foreach (var runId in state.RunIds)
        {
            if (_turnsByRunId.TryGetValue(runId, out var mapped) && ReferenceEquals(mapped, state))
                _turnsByRunId.Remove(runId);
        }
        state.RunIds.Clear();
    }

    internal static string ToTelemetryValue(ChatTelemetryOutcome outcome) =>
        outcome switch
        {
            ChatTelemetryOutcome.Success => "success",
            ChatTelemetryOutcome.Failure => "failure",
            ChatTelemetryOutcome.Canceled => "canceled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown chat telemetry outcome."),
        };

    internal static string ToTelemetryValue(ChatTurnTelemetryReason reason) =>
        reason switch
        {
            ChatTurnTelemetryReason.AssistantFinal => "assistant_final",
            ChatTurnTelemetryReason.LifecycleEnd => "lifecycle_end",
            ChatTurnTelemetryReason.LifecycleError => "lifecycle_error",
            ChatTurnTelemetryReason.SendRejected => "send_rejected",
            ChatTurnTelemetryReason.QueuedCanceled => "queued_canceled",
            ChatTurnTelemetryReason.AbortRequested => "abort_requested",
            ChatTurnTelemetryReason.Reset => "reset",
            ChatTurnTelemetryReason.Superseded => "superseded",
            ChatTurnTelemetryReason.Disconnected => "disconnected",
            ChatTurnTelemetryReason.Disposed => "disposed",
            ChatTurnTelemetryReason.Other => "other",
            _ => "other",
        };

    internal static string ToTelemetryValue(ChatAdmissionTelemetryStatus status) =>
        status switch
        {
            ChatAdmissionTelemetryStatus.Accepted => "accepted",
            ChatAdmissionTelemetryStatus.Deferred => "deferred",
            ChatAdmissionTelemetryStatus.Rejected => "rejected",
            ChatAdmissionTelemetryStatus.Canceled => "canceled",
            ChatAdmissionTelemetryStatus.Exception => "exception",
            ChatAdmissionTelemetryStatus.Other => "other",
            _ => "other",
        };

    internal static string ToTelemetryValue(ChatHistoryTelemetrySource source) =>
        source switch
        {
            ChatHistoryTelemetrySource.Initial => "initial",
            ChatHistoryTelemetrySource.Forced => "forced",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown chat history telemetry source."),
        };

    internal static string ToTelemetryValue(ChatBackfillTelemetryReason reason) =>
        reason switch
        {
            ChatBackfillTelemetryReason.RemoteTurn => "remote_turn",
            ChatBackfillTelemetryReason.ResetReconciliation => "reset_reconciliation",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown chat backfill telemetry reason."),
        };

    internal static string ToTelemetryValue(ChatTerminalEventDropReason reason) =>
        reason switch
        {
            ChatTerminalEventDropReason.MissingRunId => MissingRunId,
            ChatTerminalEventDropReason.MismatchedRunId => MismatchedRunId,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown terminal event drop reason."),
        };

    internal static string ToTelemetryValue(ChatResponseOutputKind kind) =>
        kind switch
        {
            ChatResponseOutputKind.None => "none",
            ChatResponseOutputKind.Assistant => "assistant",
            ChatResponseOutputKind.Reasoning => "reasoning",
            ChatResponseOutputKind.Tool => "tool",
            ChatResponseOutputKind.Other => "other",
            _ => "other",
        };

    private sealed class TurnState(
        string? messageId,
        string threadId,
        string source,
        Activity? activity,
        long startTimestamp)
    {
        private long? _queueSegmentStart;

        public string? MessageId { get; } = messageId;
        public string ThreadId { get; } = threadId;
        public string Source { get; } = source;
        public Activity? Activity { get; } = activity;
        public long StartTimestamp { get; } = startTimestamp;
        public HashSet<string> RunIds { get; } = new(StringComparer.Ordinal);
        public bool IsDispatched { get; set; }
        public bool WasQueued { get; private set; }
        public double QueuedDurationMilliseconds { get; private set; }
        public bool ResponseWaitStarted { get; set; }
        public double ResponseWaitDurationMilliseconds { get; set; }
        public double ResponseReceiveDurationMilliseconds { get; set; }
        public ChatResponseOutputKind FirstOutputKind { get; set; }
        public ResponsePhase? WaitPhase { get; set; }
        public ResponsePhase? ReceivePhase { get; set; }
        public ResponsePhaseCompletion? PendingWaitCompletion { get; set; }

        public void StartQueueSegment()
        {
            if (_queueSegmentStart.HasValue)
                return;
            WasQueued = true;
            _queueSegmentStart = Stopwatch.GetTimestamp();
        }

        public void EndQueueSegment(long? endTimestamp = null)
        {
            if (_queueSegmentStart is not { } started)
                return;
            QueuedDurationMilliseconds += endTimestamp is { } ended
                ? Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds
                : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _queueSegmentStart = null;
        }
    }

    private sealed record ResponsePhase(Activity? Activity, long StartTimestamp);

    internal sealed class ResponsePhaseCompletion(
        Activity? activity,
        string source,
        ChatResponseOutputKind firstOutputKind,
        ChatTelemetryOutcome outcome)
    {
        private int _completed;

        public Activity? Activity { get; } = activity;
        public string Source { get; } = source;
        public ChatResponseOutputKind FirstOutputKind { get; } = firstOutputKind;
        public ChatTelemetryOutcome Outcome { get; } = outcome;

        public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
    }

    internal sealed class PreparedTurnCompletion(
        Activity? activity,
        long startTimestamp,
        long endTimestamp,
        string source,
        bool wasQueued,
        double queuedDurationMilliseconds,
        bool responseWaitStarted,
        double responseWaitDurationMilliseconds,
        bool responseReceiveStarted,
        double responseReceiveDurationMilliseconds,
        ChatResponseOutputKind firstOutputKind,
        ResponsePhaseCompletion? waitCompletion,
        ResponsePhaseCompletion? receiveCompletion,
        ChatTelemetryOutcome outcome,
        ChatTurnTelemetryReason reason)
    {
        private int _completed;

        public Activity? Activity { get; } = activity;
        public long StartTimestamp { get; } = startTimestamp;
        public long EndTimestamp { get; } = endTimestamp;
        public string Source { get; } = source;
        public bool WasQueued { get; } = wasQueued;
        public double QueuedDurationMilliseconds { get; } = queuedDurationMilliseconds;
        public bool ResponseWaitStarted { get; } = responseWaitStarted;
        public double ResponseWaitDurationMilliseconds { get; } = responseWaitDurationMilliseconds;
        public bool ResponseReceiveStarted { get; } = responseReceiveStarted;
        public double ResponseReceiveDurationMilliseconds { get; } = responseReceiveDurationMilliseconds;
        public ChatResponseOutputKind FirstOutputKind { get; } = firstOutputKind;
        internal ResponsePhaseCompletion? WaitCompletion { get; } = waitCompletion;
        internal ResponsePhaseCompletion? ReceiveCompletion { get; } = receiveCompletion;
        public ChatTelemetryOutcome Outcome { get; } = outcome;
        public ChatTurnTelemetryReason Reason { get; } = reason;

        public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
    }
}

internal sealed class ChatTelemetryOperation(
    Activity? activity,
    long startTimestamp,
    IReadOnlyList<OpenClawTelemetryTag>? tags = null)
{
    private int _finished;

    public Activity? Activity { get; } = activity;
    public long StartTimestamp { get; } = startTimestamp;
    public IReadOnlyList<OpenClawTelemetryTag> Tags { get; } = tags ?? [];

    public bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;
}
