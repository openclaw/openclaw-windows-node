using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenClaw.Shared.Telemetry;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

[CollectionDefinition("Chat telemetry", DisableParallelization = true)]
public sealed class ChatTelemetryCollection;

[Collection("Chat telemetry")]
public sealed class ChatTelemetryTrackerTests
{
    [Fact]
    public void ConstantsAndFiniteValues_AreStable()
    {
        Assert.Equal("openclaw.chat.turn", ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("openclaw.chat.send", ChatTelemetryTracker.SendSpanName);
        Assert.Equal("openclaw.chat.history.load", ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal("openclaw.chat.history.backfill", ChatTelemetryTracker.HistoryBackfillSpanName);
        Assert.Equal("openclaw.chat.turns", ChatTelemetryTracker.TurnsMetricName);
        Assert.Equal("openclaw.chat.remote_turns.dropped", ChatTelemetryTracker.DroppedRemoteTurnsMetricName);
        Assert.Equal("openclaw.chat.terminal_events.dropped", ChatTelemetryTracker.DroppedTerminalEventsMetricName);
        Assert.Equal("success", ChatTelemetryTracker.ToTelemetryValue(ChatTelemetryOutcome.Success));
        Assert.Equal("assistant_final", ChatTelemetryTracker.ToTelemetryValue(ChatTurnTelemetryReason.AssistantFinal));
        Assert.Equal("other", ChatTelemetryTracker.ToTelemetryValue((ChatTurnTelemetryReason)999));
        Assert.Equal("deferred", ChatTelemetryTracker.ToTelemetryValue(ChatAdmissionTelemetryStatus.Deferred));
        Assert.Equal("other", ChatTelemetryTracker.ToTelemetryValue((ChatAdmissionTelemetryStatus)999));
        Assert.Equal("forced", ChatTelemetryTracker.ToTelemetryValue(ChatHistoryTelemetrySource.Forced));
        Assert.Equal("reset_reconciliation", ChatTelemetryTracker.ToTelemetryValue(ChatBackfillTelemetryReason.ResetReconciliation));
        Assert.Equal("missing_run_id", ChatTelemetryTracker.ToTelemetryValue(ChatTerminalEventDropReason.MissingRunId));
        Assert.Equal("mismatched_run_id", ChatTelemetryTracker.ToTelemetryValue(ChatTerminalEventDropReason.MismatchedRunId));
    }

    [Fact]
    public void LocalTurn_ParentsSendAndCompletesExactlyOnce()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();
        using var ambient = new Activity("ambient").Start();

        tracker.StartLocalTurn("private-message", "private-thread", queued: false);
        tracker.DispatchLocalTurn("private-message", "private-provisional-run");
        var send = tracker.StartSendAttempt("private-message");
        tracker.FinishSendAttempt(
            send,
            ChatAdmissionTelemetryStatus.Accepted,
            ChatTelemetryOutcome.Success);
        tracker.BindAcceptedRun("private-message", "private-accepted-run");

        Assert.True(tracker.FinishByRunId(
            "private-accepted-run",
            ChatTelemetryOutcome.Success,
            ChatTurnTelemetryReason.AssistantFinal));
        Assert.False(tracker.FinishByRunId(
            "private-provisional-run",
            ChatTelemetryOutcome.Failure,
            ChatTurnTelemetryReason.LifecycleError));

        var turn = Assert.Single(activities.Stopped, activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        var sendSpan = Assert.Single(activities.Stopped, activity => activity.OperationName == ChatTelemetryTracker.SendSpanName);
        Assert.Equal(default, turn.ParentSpanId);
        Assert.Equal(turn.TraceId, sendSpan.TraceId);
        Assert.Equal(turn.SpanId, sendSpan.ParentSpanId);
        Assert.Equal("success", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("assistant_final", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        Assert.DoesNotContain(turn.Tags, tag => tag.Value?.Contains("private-", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(sendSpan.Tags, tag => tag.Value?.Contains("private-", StringComparison.Ordinal) == true);

        Assert.Single(metrics.For(ChatTelemetryTracker.TurnsMetricName));
        Assert.Single(metrics.For(ChatTelemetryTracker.TurnDurationMetricName));
        Assert.Single(metrics.For(ChatTelemetryTracker.SendAttemptsMetricName));
        Assert.Empty(metrics.For(ChatTelemetryTracker.QueueWaitDurationMetricName));
    }

    [Fact]
    public void DeferredSend_AccumulatesQueueSegmentsAndRecordsEachAttempt()
    {
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();

        tracker.StartLocalTurn("message", "thread", queued: true);
        tracker.DispatchLocalTurn("message", "attempt-1");
        var first = tracker.StartSendAttempt("message");
        tracker.FinishSendAttempt(first, ChatAdmissionTelemetryStatus.Deferred, ChatTelemetryOutcome.Success);
        tracker.RequeueLocalTurn("message");
        tracker.DispatchLocalTurn("message", "attempt-2");
        var second = tracker.StartSendAttempt("message");
        tracker.FinishSendAttempt(second, ChatAdmissionTelemetryStatus.Accepted, ChatTelemetryOutcome.Success);
        tracker.BindAcceptedRun("message", "accepted");
        tracker.FinishByRunId("accepted", ChatTelemetryOutcome.Success, ChatTurnTelemetryReason.LifecycleEnd);

        var attempts = metrics.For(ChatTelemetryTracker.SendAttemptsMetricName);
        Assert.Equal(2, attempts.Count);
        Assert.Contains(attempts, measurement => measurement.Tag(ChatTelemetryTracker.AdmissionStatusTag) == "deferred");
        Assert.Contains(attempts, measurement => measurement.Tag(ChatTelemetryTracker.AdmissionStatusTag) == "accepted");
        Assert.All(attempts, measurement =>
            Assert.Equal("success", measurement.Tag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName())));
        Assert.Single(metrics.For(ChatTelemetryTracker.QueueWaitDurationMetricName));
    }

    [Fact]
    public async Task ConcurrentTerminalSignals_RecordOneTurn()
    {
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();
        tracker.StartLocalTurn("message", "thread", queued: false);
        tracker.DispatchLocalTurn("message", "run");

        await Task.WhenAll(
            Task.Run(() => tracker.FinishByRunId(
                "run",
                ChatTelemetryOutcome.Success,
                ChatTurnTelemetryReason.AssistantFinal)),
            Task.Run(() => tracker.FinishByRunId(
                "run",
                ChatTelemetryOutcome.Failure,
                ChatTurnTelemetryReason.LifecycleError)));

        Assert.Single(metrics.For(ChatTelemetryTracker.TurnsMetricName));
        Assert.Single(metrics.For(ChatTelemetryTracker.TurnDurationMetricName));
    }

    [Fact]
    public void RemoteTurnWithoutRunId_RecordsDropButNoTurn()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();

        tracker.ObserveLifecycleStart("private-thread", runId: null);

        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        var dropped = Assert.Single(metrics.For(ChatTelemetryTracker.DroppedRemoteTurnsMetricName));
        Assert.Equal("missing_run_id", dropped.Tag(ChatTelemetryTracker.DroppedRemoteTurnReasonTag));
    }

    [Fact]
    public void LocalTurnWithoutLifecycleRunId_DoesNotRecordRemoteDrop()
    {
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();
        tracker.StartLocalTurn("message", "thread", queued: false);
        tracker.DispatchLocalTurn("message", "provisional-run");

        tracker.ObserveLifecycleStart("thread", runId: null);

        Assert.Empty(metrics.For(ChatTelemetryTracker.DroppedRemoteTurnsMetricName));
        tracker.FinishAll(ChatTelemetryOutcome.Canceled, ChatTurnTelemetryReason.Disposed);
    }

    [Fact]
    public void PreparedCompletion_ReservesUnderLockAndEmitsAfterward()
    {
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();
        tracker.StartLocalTurn("message", "thread", queued: false);

        var completion = tracker.PrepareFinishByMessageId(
            "message",
            ChatTelemetryOutcome.Failure,
            ChatTurnTelemetryReason.SendRejected);

        Assert.NotNull(completion);
        Assert.Null(tracker.PrepareFinishByMessageId(
            "message",
            ChatTelemetryOutcome.Canceled,
            ChatTurnTelemetryReason.Disconnected));
        Assert.Empty(metrics.For(ChatTelemetryTracker.TurnsMetricName));
        Assert.True(tracker.CompletePreparedTurn(completion));
        Assert.False(tracker.CompletePreparedTurn(completion));
        var turn = Assert.Single(metrics.For(ChatTelemetryTracker.TurnsMetricName));
        Assert.Equal("send_rejected", turn.Tag(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
    }

    [Fact]
    public void DroppedTerminalEvents_RecordOnlyFiniteReasons()
    {
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();

        tracker.RecordDroppedTerminalEvent(ChatTerminalEventDropReason.MissingRunId);
        tracker.RecordDroppedTerminalEvent(ChatTerminalEventDropReason.MismatchedRunId);

        var dropped = metrics.For(ChatTelemetryTracker.DroppedTerminalEventsMetricName);
        Assert.Equal(2, dropped.Count);
        Assert.Contains(
            dropped,
            measurement => measurement.Tag(ChatTelemetryTracker.DroppedTerminalEventReasonTag) == "missing_run_id");
        Assert.Contains(
            dropped,
            measurement => measurement.Tag(ChatTelemetryTracker.DroppedTerminalEventReasonTag) == "mismatched_run_id");
    }

    [Fact]
    public void HistoryOperations_RecordOnlyAllowlistedTags()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var tracker = new ChatTelemetryTracker();

        var load = tracker.StartHistoryLoad(ChatHistoryTelemetrySource.Forced);
        tracker.FinishHistoryLoad(load, ChatTelemetryOutcome.Success);
        var backfill = tracker.StartHistoryBackfill(ChatBackfillTelemetryReason.RemoteTurn);
        tracker.FinishHistoryBackfill(backfill, ChatTelemetryOutcome.Failure, new InvalidOperationException("private-error"));

        var loadSpan = Assert.Single(activities.Stopped, activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal(["openclaw.outcome", "openclaw.source"], loadSpan.Tags.Select(tag => tag.Key).Order().ToArray());
        var backfillSpan = Assert.Single(activities.Stopped, activity => activity.OperationName == ChatTelemetryTracker.HistoryBackfillSpanName);
        Assert.Equal(
            ["error.type", "openclaw.chat.backfill.reason", "openclaw.outcome", "openclaw.source"],
            backfillSpan.Tags.Select(tag => tag.Key).Order().ToArray());
        Assert.DoesNotContain(backfillSpan.Tags, tag => tag.Value?.Contains("private-error", StringComparison.Ordinal) == true);
        Assert.Single(metrics.For(ChatTelemetryTracker.HistoryLoadsMetricName));
        Assert.Single(metrics.For(ChatTelemetryTracker.HistoryBackfillsMetricName));
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == OpenClawActivitySourceName.OpenClaw.ToTelemetryName(),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Stopped.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<Measurement> _measurements = [];

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenClawMeterName.OpenClaw.ToTelemetryName() &&
                    instrument.Name.StartsWith("openclaw.chat.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public List<Measurement> For(string name) =>
            _measurements.Where(measurement => measurement.Name == name).ToList();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Measurement(
        string Name,
        object Value,
        KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string key) =>
            Tags.FirstOrDefault(tag => tag.Key == key).Value?.ToString();
    }
}
