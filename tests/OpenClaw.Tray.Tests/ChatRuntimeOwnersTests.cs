using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationStateTests
{
    [Fact]
    public void ResetThread_ClearsQueueAndAdvancesGenerationAtomically()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load(
            [new SessionInfo { Key = "main", IsMain = true }],
            context);
        state.AdmitMessage(
            "main",
            "first",
            "first",
            "nonce-1",
            attachments: null,
            DateTimeOffset.UnixEpoch,
            context);
        state.AdmitMessage(
            "main",
            "second",
            "second",
            "nonce-2",
            attachments: null,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            context);

        var reset = state.ResetThread(
            "main",
            context);

        Assert.Equal(1, reset.ResetGeneration);
        Assert.Empty(reset.Snapshot.Timelines["main"].Entries);
        Assert.True(reset.Snapshot.Timelines["main"].HistoryLoaded);
        Assert.Empty(reset.Snapshot.QueuedMessagesByThread!);
        Assert.Equal(1, reset.Snapshot.TimelineGenerations!["main"]);
    }

    [Fact]
    public async Task HistoryGeneration_WaitsForLoaderActivation()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);

        _ = state.ApplyStatus(
            ConnectionStatus.Disconnected,
            context);
        Assert.False(state.TryBeginHistory(
            "main",
            force: true,
            expectedToken: null,
            out _,
            out _,
            out var activation));
        Assert.NotNull(activation);
        Assert.False(activation.IsCompleted);

        var superseding = state.ApplyStatus(
            ConnectionStatus.Connected,
            context);
        await activation;
        Assert.False(state.TryBeginHistory(
            "main",
            force: true,
            expectedToken: null,
            out _,
            out _,
            out var supersedingActivation));
        Assert.NotNull(supersedingActivation);
        Assert.False(supersedingActivation.IsCompleted);

        state.ActivateHistoryGeneration(superseding.HistoryGeneration);
        await supersedingActivation;
        Assert.True(state.TryBeginHistory(
            "main",
            force: true,
            expectedToken: null,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void UsageContribution_FallsBackToInputPlusOutput()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        var metadata = new ChatEntryMetadata(
            DateTimeOffset.UnixEpoch,
            Model: null,
            InputTokens: 100,
            OutputTokens: 20,
            ResponseTokens: null);
        state.ApplyEvent(
            "main",
            new ChatMessageEvent("response"),
            metadata,
            context);

        state.SnapshotAssistantUsageContribution("main", metadata, context);

        Assert.Equal(120, state.GetEntryMetadata("main")["e1"].ResponseTokens);
    }

    [Fact]
    public void ProcessAgentEvent_ResetDropsTerminalWithoutReopeningTurn()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        using var start = JsonDocument.Parse("""{"phase":"start"}""");
        using var end = JsonDocument.Parse("""{"phase":"end"}""");

        var started = state.ProcessAgentEvent(
            new AgentEventInfo
            {
                Stream = "lifecycle",
                SessionKey = "main",
                RunId = "run-before-reset",
                Data = start.RootElement.Clone(),
            },
            "main",
            context);
        Assert.True(started.Process);
        Assert.True(started.Snapshots[^1].Timelines["main"].TurnActive);

        var reset = state.ResetThread("main", context);
        var terminal = state.ProcessAgentEvent(
            new AgentEventInfo
            {
                Stream = "lifecycle",
                SessionKey = "main",
                RunId = "run-before-reset",
                Data = end.RootElement.Clone(),
            },
            "main",
            context);

        Assert.False(terminal.Process);
        Assert.False(reset.Snapshot.Timelines["main"].TurnActive);
        Assert.Empty(reset.Snapshot.Timelines["main"].Entries);
    }

    [Fact]
    public void RollbackAbortAndEndTurn_StaleGenerationDoesNotEndReplacementTurn()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext(
            "main",
            HasHandshakeSnapshot: true);
        state.Load(
            [new SessionInfo { Key = "main", IsMain = true }],
            context);
        using var start = JsonDocument.Parse("""{"phase":"start"}""");
        var oldStart = state.ProcessAgentEvent(
            new AgentEventInfo
            {
                Stream = "lifecycle",
                SessionKey = "main",
                RunId = "old-run",
                Data = start.RootElement.Clone(),
            },
            "main",
            context);
        state.ApplyStatus(ConnectionStatus.Disconnected, context);
        state.ApplyStatus(ConnectionStatus.Connected, context);
        var replacement = state.ProcessAgentEvent(
            new AgentEventInfo
            {
                Stream = "lifecycle",
                SessionKey = "main",
                RunId = "replacement-run",
                Data = start.RootElement.Clone(),
            },
            "main",
            context);
        Assert.True(replacement.Process);

        var rollback = state.RollbackAbortAndEndTurnIfCurrent(
            "main",
            "old-run",
            oldStart.RuntimeGeneration,
            context);

        Assert.Null(rollback);
        Assert.True(state.Snapshot(context).Timelines["main"].TurnActive);
    }

    [Fact]
    public void HistoryMerge_DeduplicatesPreservedLiveTailEntries()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        var metadata = new ChatEntryMetadata(
            DateTimeOffset.UtcNow,
            Model: null,
            GatewayMessageId: "duplicate-live-id");
        state.ApplyEvent(
            "main",
            new ChatStatusEvent("duplicate", ChatTone.Dim),
            metadata,
            context);
        state.ApplyEvent(
            "main",
            new ChatStatusEvent("duplicate", ChatTone.Dim),
            metadata,
            context);
        Assert.True(state.TryBeginHistory(
            "main",
            force: true,
            expectedToken: null,
            out var token,
            out _,
            out _));

        Assert.True(state.CommitHistory(
            token,
            new ChatHistoryRebuildPlan(
                SessionId: null,
                ChatTimelineState.Initial() with { HistoryLoaded = true },
                new Dictionary<string, ChatEntryMetadata>(),
                MaxHistorySequence: 0),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            authoritative: false));

        var timeline = state.Snapshot(context).Timelines["main"];
        Assert.Single(timeline.Entries);
    }

    [Fact]
    public void HistoryReplacement_ClearsTimelineAndAdvancesOwnedTokenAtomically()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ApplyEvent(
            "main",
            new ChatUserMessageEvent("archived"),
            new ChatEntryMetadata(
                DateTimeOffset.UtcNow,
                Model: null,
                GatewayMessageId: "archived-id"),
            context);
        var oldToken = state.CaptureHistoryToken("main");

        var replacement = state.BeginHistoryReplacement("main", context);

        Assert.NotNull(replacement);
        Assert.Empty(replacement.Snapshot.Timelines["main"].Entries);
        Assert.Empty(state.GetEntryMetadata("main"));
        Assert.Equal(
            oldToken.ReplacementGeneration + 1,
            replacement.Token.ReplacementGeneration);
        Assert.False(state.IsHistoryRequestCurrent(oldToken));
        Assert.True(state.IsHistoryRequestCurrent(replacement.Token));
    }

    [Fact]
    public void AttachmentOnlyFallbackAfterSendConfirmation_SurfacesOpenedLifecycle()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ResetThread("main", context);
        var admission = state.AdmitMessage(
            "main",
            text: string.Empty,
            displayText: "\u200B📎 proof.txt",
            nonce: "attachment-only",
            attachments:
            [
                new ChatAttachment
                {
                    Type = "file",
                    MimeType = "text/plain",
                    FileName = "proof.txt",
                    Content = "cHJvb2Y=",
                    SizeBytes = 5,
                },
            ],
            DateTimeOffset.UtcNow,
            context);
        Assert.NotNull(admission.Dispatch);
        var commit = state.CommitSendResult(
            admission.Dispatch!,
            new ChatSendResult { Status = "started" },
            context);
        Assert.Null(commit.OpenedLifecycle);
        using var fallbackData =
            JsonDocument.Parse("""{"phase":"fallback_step"}""");

        var fallback = state.ProcessAgentEvent(
            new AgentEventInfo
            {
                Stream = "lifecycle",
                SessionKey = "main",
                RunId = "attachment-run",
                Ts = DateTimeOffset.UtcNow.AddMinutes(-5)
                    .ToUnixTimeMilliseconds(),
                Data = fallbackData.RootElement.Clone(),
            },
            "main",
            context);

        Assert.True(fallback.Process);
        Assert.Equal(
            "attachment-run",
            fallback.OpenedLifecycle?.Event.RunId);
    }
}

public sealed class ChatResetStateTests
{
    [Fact]
    public void SubmittedEchoWithoutPendingQueue_OpensBufferedLifecycle()
    {
        const string threadId = "main";
        const string marker = "controlled marker";
        var now = DateTimeOffset.UtcNow;
        var state = new ChatResetState();
        var version = state.BeginReset(
            threadId,
            now.ToUnixTimeMilliseconds());
        state.AddSubmittedLocalEcho(threadId, marker, now);
        Assert.Null(state.RecordLocalSendWithoutRun(
            threadId,
            version,
            state.LifecycleStartSequence));
        var start = Lifecycle("start", "new-run", now.AddSeconds(1));

        var buffered = state.EvaluateAgentEvent(start, threadId);
        var earlyAssistant = state.EvaluateAgentEvent(
            Assistant("early", "new-run", now.AddSeconds(1)),
            threadId);
        var echo = state.EvaluateChatMessage(
            threadId,
            role: "user",
            rawText: marker,
            timestampMs: now.AddSeconds(1).ToUnixTimeMilliseconds(),
            hasPendingLocalEcho: false);
        var terminal = state.EvaluateAgentEvent(
            Lifecycle("end", "new-run", now.AddSeconds(1)),
            threadId);

        Assert.True(buffered.Drop);
        Assert.Null(buffered.OpenedLifecycleStart);
        Assert.True(earlyAssistant.Drop);
        Assert.True(echo.Drop);
        Assert.Equal(marker, echo.ConsumeEchoText);
        Assert.Same(start, echo.OpenedLifecycleStart);
        Assert.False(state.IsAwaitingUserMessage(threadId));
        Assert.False(terminal.Drop);
    }

    [Fact]
    public void SubmittedEcho_NonmatchingTextDoesNotOpenBufferedLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var state = PendingSubmittedEcho(now, submittedText: "expected");
        var start = Lifecycle("start", "new-run", now.AddSeconds(1));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);

        var gate = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: "different",
            timestampMs: 0,
            hasPendingLocalEcho: false);

        Assert.True(gate.Drop);
        Assert.True(gate.RequestRemoteBackfill);
        Assert.Null(gate.OpenedLifecycleStart);
        Assert.True(state.IsAwaitingUserMessage("main"));
    }

    [Fact]
    public void SubmittedEcho_ExpiredCorrelationDoesNotOpenBufferedLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new ChatResetState();
        var version = state.BeginReset("main", now.ToUnixTimeMilliseconds());
        state.AddSubmittedLocalEcho(
            "main",
            "expected",
            now.AddSeconds(-31));
        state.RecordLocalSendWithoutRun(
            "main",
            version,
            state.LifecycleStartSequence);
        var start = Lifecycle("start", "new-run", now.AddSeconds(1));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);

        var gate = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: "expected",
            timestampMs: 0,
            hasPendingLocalEcho: false);

        Assert.True(gate.Drop);
        Assert.True(gate.RequestRemoteBackfill);
        Assert.Null(gate.OpenedLifecycleStart);
        Assert.True(state.IsAwaitingUserMessage("main"));
    }

    [Fact]
    public void SubmittedEcho_PreResetTimestampIsConsumedWithoutOpeningLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var state = PendingSubmittedEcho(now, submittedText: "expected");
        var start = Lifecycle("start", "new-run", now.AddSeconds(1));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);

        var gate = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: "expected",
            timestampMs: now.AddSeconds(-5).ToUnixTimeMilliseconds(),
            hasPendingLocalEcho: false);

        Assert.True(gate.Drop);
        Assert.Equal("expected", gate.ConsumeEchoText);
        Assert.False(gate.RequestRemoteBackfill);
        Assert.Null(gate.OpenedLifecycleStart);
        Assert.True(state.IsAwaitingUserMessage("main"));
    }

    [Fact]
    public void ProductionSubmission_SkewedEchoOpensOnlyLifecycleAfterItsStartSequence()
    {
        const string text = "same text";
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        var oldStart = Lifecycle(
            "start",
            "old-run",
            now.AddSeconds(1));
        Assert.True(state.EvaluateAgentEvent(oldStart, "main").Drop);
        var submissionSequence = state.LifecycleStartSequence;
        state.RegisterPendingLocalSubmission(
            "main",
            "submission-1",
            text,
            generation,
            submissionSequence,
            now);

        var echo = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: text,
            timestampMs: cutoff - 5_000,
            hasPendingLocalEcho: false);
        Assert.True(echo.Drop);
        Assert.Null(echo.OpenedLifecycleStart);
        Assert.True(state.IsAwaitingUserMessage("main"));

        var eligibleStart = Lifecycle(
            "start",
            "new-run",
            now.AddSeconds(-5));
        var opened = state.EvaluateAgentEvent(
            eligibleStart,
            "main");

        Assert.False(opened.Drop);
        Assert.Same(eligibleStart, opened.OpenedLifecycleStart);
        Assert.False(state.IsAwaitingUserMessage("main"));
    }

    [Fact]
    public void ProductionSubmission_ExactEchoSelectsNewestEligibleLifecycle()
    {
        const string text = "current submission";
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "submission",
            text,
            generation,
            state.LifecycleStartSequence,
            now);
        var stale = Lifecycle(
            "start",
            "stale-run",
            now.AddSeconds(-6));
        var current = Lifecycle(
            "start",
            "current-run",
            now.AddSeconds(-5));
        Assert.True(state.EvaluateAgentEvent(stale, "main").Drop);
        Assert.True(state.EvaluateAgentEvent(current, "main").Drop);

        var echo = state.EvaluateChatMessage(
            "main",
            "user",
            text,
            now.AddMilliseconds(-5_500).ToUnixTimeMilliseconds(),
            hasPendingLocalEcho: false);
        var staleFrame = state.EvaluateAgentEvent(
            Assistant(
                "stale output",
                "stale-run",
                now.AddMilliseconds(-5_400)),
            "main");
        var currentFrame = state.EvaluateAgentEvent(
            Assistant(
                "current output",
                "current-run",
                now.AddMilliseconds(-5_400)),
            "main");

        Assert.True(echo.Drop);
        Assert.Same(current, echo.OpenedLifecycleStart);
        Assert.True(staleFrame.Drop);
        Assert.False(currentFrame.Drop);
    }

    [Fact]
    public void ProductionSubmission_WrongExpiredAndWrongGenerationCannotOpen()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "wrong-generation",
            "expected",
            generation - 1,
            state.LifecycleStartSequence,
            now);
        state.RegisterPendingLocalSubmission(
            "main",
            "expired",
            "expired",
            generation,
            state.LifecycleStartSequence,
            now.AddSeconds(-31));
        state.RegisterPendingLocalSubmission(
            "main",
            "current",
            "expected",
            generation,
            state.LifecycleStartSequence,
            now);
        var start = Lifecycle(
            "start",
            "new-run",
            now.AddSeconds(-5));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);

        var wrong = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: "different",
            timestampMs: cutoff - 5_000,
            hasPendingLocalEcho: false);
        var expired = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: "expired",
            timestampMs: cutoff - 5_000,
            hasPendingLocalEcho: false);

        Assert.True(wrong.Drop);
        Assert.Null(wrong.OpenedLifecycleStart);
        Assert.True(expired.Drop);
        Assert.Null(expired.OpenedLifecycleStart);
        Assert.True(state.IsAwaitingUserMessage("main"));
    }

    [Fact]
    public void AcceptedLifecycleFloor_AllowsSameRunAndRejectsOlderOrCompletedRun()
    {
        const string text = "exact echo";
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var lifecycleTimestamp = cutoff - 5_000;
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "submission",
            text,
            generation,
            state.LifecycleStartSequence,
            now);
        var start = Lifecycle(
            "start",
            "new-run",
            DateTimeOffset.FromUnixTimeMilliseconds(
                lifecycleTimestamp));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);
        var echo = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: text,
            timestampMs: lifecycleTimestamp + 1,
            hasPendingLocalEcho: true);
        Assert.Same(start, echo.OpenedLifecycleStart);

        var accepted = state.EvaluateChatMessage(
            "main",
            role: "assistant",
            rawText: "current",
            timestampMs: lifecycleTimestamp + 2,
            hasPendingLocalEcho: false,
            activeRunId: "new-run");
        var older = state.EvaluateChatMessage(
            "main",
            role: "assistant",
            rawText: "older",
            timestampMs: lifecycleTimestamp - 1,
            hasPendingLocalEcho: false,
            activeRunId: "new-run");
        var wrongRun = state.EvaluateChatMessage(
            "main",
            role: "assistant",
            rawText: "wrong run",
            timestampMs: lifecycleTimestamp + 2,
            hasPendingLocalEcho: false,
            activeRunId: "other-run");
        state.CompleteRun("main", "new-run");
        var completed = state.EvaluateChatMessage(
            "main",
            role: "assistant",
            rawText: "completed",
            timestampMs: lifecycleTimestamp + 2,
            hasPendingLocalEcho: false,
            activeRunId: "new-run");

        Assert.False(accepted.Drop);
        Assert.True(older.Drop);
        Assert.True(wrongRun.Drop);
        Assert.True(completed.Drop);
    }

    [Fact]
    public void AcceptedLifecycleFloor_NeverAuthorizesUserRoleFrames()
    {
        const string marker = "exact local echo";
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var lifecycleTimestamp = cutoff - 5_000;
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "submission",
            marker,
            generation,
            state.LifecycleStartSequence,
            now);
        var start = Lifecycle(
            "start",
            "current-run",
            DateTimeOffset.FromUnixTimeMilliseconds(
                lifecycleTimestamp));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);
        var echo = state.EvaluateChatMessage(
            "main",
            "user",
            marker,
            lifecycleTimestamp + 1,
            hasPendingLocalEcho: false);
        Assert.Same(start, echo.OpenedLifecycleStart);

        var delayedUser = state.EvaluateChatMessage(
            "main",
            "user",
            "delayed unrelated user",
            lifecycleTimestamp + 2,
            hasPendingLocalEcho: false,
            activeRunId: "current-run");
        var delayedApproval = state.EvaluateChatMessage(
            "main",
            "user",
            "/approve abcdef allow-once",
            lifecycleTimestamp + 3,
            hasPendingLocalEcho: false,
            activeRunId: "current-run");
        var delayedControl = state.EvaluateChatMessage(
            "main",
            "user",
            "System: Reset session",
            lifecycleTimestamp + 4,
            hasPendingLocalEcho: false,
            activeRunId: "current-run");
        var sameRunAssistant = state.EvaluateChatMessage(
            "main",
            "assistant",
            "current response",
            lifecycleTimestamp + 5,
            hasPendingLocalEcho: false,
            activeRunId: "current-run");
        var postCutoffUser = state.EvaluateChatMessage(
            "main",
            "user",
            "fresh remote user",
            cutoff + 1,
            hasPendingLocalEcho: false,
            activeRunId: "current-run");

        Assert.True(delayedUser.Drop);
        Assert.True(delayedApproval.Drop);
        Assert.True(delayedControl.Drop);
        Assert.False(sameRunAssistant.Drop);
        Assert.False(postCutoffUser.Drop);
    }

    [Fact]
    public void AcceptedLifecycleFloor_ResetAndReconnectClearState()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var lifecycleTimestamp = cutoff - 5_000;
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "submission",
            "echo",
            generation,
            state.LifecycleStartSequence,
            now);
        var start = Lifecycle(
            "start",
            "new-run",
            DateTimeOffset.FromUnixTimeMilliseconds(
                lifecycleTimestamp));
        state.EvaluateAgentEvent(start, "main");
        state.EvaluateChatMessage(
            "main",
            "user",
            "echo",
            lifecycleTimestamp + 1,
            hasPendingLocalEcho: true);
        state.BeginReset("main", cutoff + 10_000);
        var afterReset = state.EvaluateChatMessage(
            "main",
            "assistant",
            "after reset",
            lifecycleTimestamp + 2,
            hasPendingLocalEcho: false,
            activeRunId: "new-run");
        state.ClearSubmittedEchoesForReconnect();
        var afterReconnect = state.EvaluateChatMessage(
            "main",
            "assistant",
            "after reconnect",
            lifecycleTimestamp + 2,
            hasPendingLocalEcho: false,
            activeRunId: "new-run");

        Assert.True(afterReset.Drop);
        Assert.True(afterReconnect.Drop);
    }

    [Fact]
    public void RemovePendingSubmission_RemovesOnlyMatchingIdentityAndGeneration()
    {
        const string text = "repeated";
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "first",
            text,
            generation,
            state.LifecycleStartSequence,
            now);
        state.RegisterPendingLocalSubmission(
            "main",
            "second",
            text,
            generation,
            state.LifecycleStartSequence,
            now.AddMilliseconds(1));
        state.RemovePendingLocalSubmission(
            "main",
            "first",
            generation);
        state.RemovePendingLocalSubmission(
            "main",
            "second",
            generation - 1);
        var start = Lifecycle(
            "start",
            "new-run",
            now.AddSeconds(-5));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);

        var echo = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: text,
            timestampMs: cutoff - 5_000,
            hasPendingLocalEcho: false);

        Assert.Same(start, echo.OpenedLifecycleStart);
        Assert.False(state.IsAwaitingUserMessage("main"));
    }

    [Fact]
    public void MatchedSubmissionEcho_DoesNotConsumeLaterIdenticalPostCutoffMessage()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "submission",
            "same text",
            generation,
            state.LifecycleStartSequence,
            now);
        var start = Lifecycle(
            "start",
            "new-run",
            now.AddSeconds(-5));
        Assert.True(state.EvaluateAgentEvent(start, "main").Drop);

        var echo = state.EvaluateChatMessage(
            "main",
            "user",
            "same text",
            cutoff - 5_000,
            hasPendingLocalEcho: false);
        var laterRemote = state.EvaluateChatMessage(
            "main",
            "user",
            "same text",
            cutoff + 1,
            hasPendingLocalEcho: false,
            activeRunId: "new-run");

        Assert.True(echo.Drop);
        Assert.Same(start, echo.OpenedLifecycleStart);
        Assert.False(laterRemote.Drop);
    }

    [Fact]
    public void AttachmentOnlySubmission_ConfirmedSendOpensBufferedFallback()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.ToUnixTimeMilliseconds();
        var fallbackTimestamp = cutoff - 5_000;
        var state = new ChatResetState();
        var generation = state.BeginReset("main", cutoff);
        state.RegisterPendingLocalSubmission(
            "main",
            "attachment-only",
            string.Empty,
            generation,
            state.LifecycleStartSequence,
            now,
            requiresEcho: false);
        var fallback = Lifecycle(
            "fallback_step",
            "attachment-run",
            DateTimeOffset.FromUnixTimeMilliseconds(
                fallbackTimestamp));
        Assert.True(state.EvaluateAgentEvent(fallback, "main").Drop);

        var opened = state.RecordLocalSendWithoutRun(
            "main",
            generation,
            lifecycleStartSequence: 0,
            submissionId: "attachment-only");
        var final = state.EvaluateChatMessage(
            "main",
            "assistant",
            "attachment terminal",
            fallbackTimestamp + 1,
            hasPendingLocalEcho: false,
            activeRunId: "attachment-run");

        Assert.Same(fallback, opened);
        Assert.False(final.Drop);
    }

    [Fact]
    public void IgnoredOldRunAndPreResetMessageRemainDropped()
    {
        var now = DateTimeOffset.UtcNow;
        var state = PendingSubmittedEcho(now, submittedText: "expected");
        state.AddIgnoredRun("main", "old-run");

        var ignoredStart = state.EvaluateAgentEvent(
            Lifecycle("start", "old-run", now.AddSeconds(1)),
            "main");
        var ignoredTerminal = state.EvaluateAgentEvent(
            Lifecycle("end", "old-run", now.AddSeconds(1)),
            "main");
        var preReset = state.EvaluateChatMessage(
            "main",
            role: "user",
            rawText: "unrelated old message",
            timestampMs: now.AddSeconds(-5).ToUnixTimeMilliseconds(),
            hasPendingLocalEcho: false);

        Assert.True(ignoredStart.Drop);
        Assert.Null(ignoredStart.OpenedLifecycleStart);
        Assert.True(ignoredTerminal.Drop);
        Assert.True(ignoredTerminal.ReloadHistory);
        Assert.True(preReset.Drop);
        Assert.False(preReset.RequestRemoteBackfill);
        Assert.Null(preReset.OpenedLifecycleStart);
        Assert.True(state.IsAwaitingUserMessage("main"));
    }

    private static ChatResetState PendingSubmittedEcho(
        DateTimeOffset now,
        string submittedText)
    {
        var state = new ChatResetState();
        var version = state.BeginReset("main", now.ToUnixTimeMilliseconds());
        state.AddSubmittedLocalEcho("main", submittedText, now);
        state.RecordLocalSendWithoutRun(
            "main",
            version,
            state.LifecycleStartSequence);
        return state;
    }

    private static AgentEventInfo Lifecycle(
        string phase,
        string runId,
        DateTimeOffset timestamp)
    {
        using var document = JsonDocument.Parse(
            $$"""{"phase":"{{phase}}"}""");
        return new AgentEventInfo
        {
            Stream = "lifecycle",
            SessionKey = "main",
            RunId = runId,
            Ts = timestamp.ToUnixTimeMilliseconds(),
            Data = document.RootElement.Clone(),
        };
    }

    private static AgentEventInfo Assistant(
        string text,
        string runId,
        DateTimeOffset timestamp)
    {
        using var document = JsonDocument.Parse(
            $$"""{"delta":"{{text}}"}""");
        return new AgentEventInfo
        {
            Stream = "assistant",
            SessionKey = "main",
            RunId = runId,
            Ts = timestamp.ToUnixTimeMilliseconds(),
            Data = document.RootElement.Clone(),
        };
    }
}

public sealed class ChatSendQueuePolicyTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void CanSendDirectly_UsesAtomicQueueInputs(
        bool hasActiveRun,
        bool turnActive,
        bool hasPendingMessages,
        bool expected)
    {
        Assert.Equal(
            expected,
            ChatSendQueuePolicy.CanSendDirectly(
                hasActiveRun,
                turnActive,
                hasPendingMessages));
    }
}

public sealed class ChatEventMapperTests
{
    [Fact]
    public void Map_ApprovalRequestPreservesIdentityAndActions()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "phase": "requested",
              "approvalSlug": "approve-1",
              "approvalId": "approval-uuid",
              "title": "Run command",
              "host": "node",
              "command": "echo ok"
            }
            """);
        var mapping = ChatEventMapper.Map(new AgentEventInfo
        {
            Stream = "approval",
            SessionKey = "main",
            Data = document.RootElement.Clone(),
        });

        var request = Assert.IsType<ChatPermissionRequestEvent>(mapping.Event);
        Assert.Equal("approve-1", request.RequestId);
        Assert.Equal("approval-uuid", mapping.Approval?.AlternateId);
        Assert.Equal(ChatPermissionActionKeys.ExecApprovalDefaults, request.Actions);
    }

    [Fact]
    public void MapTerminalApproval_ReturnsTypedIdentityAndDecision()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "phase": "resolved",
              "approvalSlug": "approve-1",
              "approvalId": "approval-uuid",
              "decision": "allow-always"
            }
            """);

        var terminal = ChatEventMapper.MapTerminalApproval(new AgentEventInfo
        {
            Stream = "approval",
            SessionKey = "main",
            Data = document.RootElement.Clone(),
        });

        Assert.NotNull(terminal);
        Assert.Equal("approve-1", terminal.ApprovalSlug);
        Assert.Equal("approval-uuid", terminal.ApprovalId);
        Assert.Equal(ChatPermissionActionKeys.AllowAlways, terminal.Decision);
    }
}

public sealed class ChatStatePersistenceTests
{
    [Fact]
    public void LoadLastChatState_CorruptedJsonReturnsNull()
    {
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        var path = directory.Combine("last-chat-state.json");
        File.WriteAllText(path, "{broken");

        Assert.Null(ChatStatePersistence.LoadLastChatState(path));
    }

    [Fact]
    public void ResetFence_RejectsStaleAbortedIds()
    {
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        using var persistence = new ChatStatePersistence(
            directory.Combine("last-chat-state.json"));
        var threadId = "reset-fence-" + Guid.NewGuid().ToString("N");

        persistence.ApplyReset(threadId, resetGeneration: 2);

        Assert.False(persistence.TryAddAbortedIds(
            threadId,
            resetGeneration: 1,
            ["stale-message"]));
        Assert.False(persistence.IsMessageAborted(threadId, "stale-message"));
    }

    [Fact]
    public void ResetFence_PreservesCurrentGenerationIdsAddedBeforeResetApplies()
    {
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        using var persistence = new ChatStatePersistence(
            directory.Combine("last-chat-state.json"));
        const string threadId = "current-generation";

        Assert.True(persistence.TryAddAbortedIds(
            threadId,
            resetGeneration: 2,
            ["current-message"]));

        Assert.False(persistence.ApplyReset(threadId, resetGeneration: 2));
        Assert.True(persistence.IsMessageAborted(
            threadId,
            "current-message",
            resetGeneration: 2));
    }

    [Fact]
    public async Task ConcurrentSaves_PersistCompleteAbortedIdSet()
    {
        using var directory = new OpenClaw.TestSupport.TempDirectory();
        var abortedPath = directory.Combine("aborted-messages.json");
        using var persistence = new ChatStatePersistence(
            directory.Combine("last-chat-state.json"),
            abortedIdsPath: abortedPath);
        var threadId = "concurrent-save";

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
        {
            persistence.TryAddAbortedIds(
                threadId,
                resetGeneration: 0,
                [$"message-{index}"]);
            persistence.SaveAbortedIds();
        })));

        var persisted = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            File.ReadAllText(abortedPath));
        Assert.Equal(20, persisted![threadId].Distinct(StringComparer.Ordinal).Count());
    }
}

public sealed class ChatRuntimeOwnershipContractTests
{
    [Fact]
    public void Provider_DelegatesRuntimeStateWithoutPrivateGate()
    {
        var provider = Read("OpenClawChatDataProvider.cs");
        var state = Read("ChatConversationState.cs");
        var queue = Read("ChatSendQueue.cs");
        var history = Read("ChatHistoryLoader.cs");
        var projector = Read("ChatSnapshotProjector.cs");

        Assert.DoesNotContain("private readonly object _gate", provider);
        Assert.DoesNotContain("Dictionary<string, ChatTimelineState> _timelines", provider);
        Assert.DoesNotContain("Dictionary<string, List<ChatQueuedMessage>> _queuedMessages", provider);
        Assert.DoesNotContain("_historyReplacementVersions", provider);
        Assert.Contains("private readonly ChatConversationState _state", provider);
        Assert.Contains("private readonly ChatHistoryLoader _historyLoader", provider);
        Assert.Contains("private readonly ChatMetadataStore _metadataStore", provider);
        Assert.Contains("private readonly ChatStatePersistence _persistence", provider);
        Assert.Contains("private readonly object _gate", state);
        Assert.Contains("internal ChatResetTransition ResetThread(", state);
        Assert.Contains("internal ChatHistoryReplacementTransition? BeginHistoryReplacement(", state);
        Assert.Contains("private readonly Dictionary<string, ChatHistoryCommitToken> _replacementPending", history);
        Assert.DoesNotContain("Telemetry", state, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Telemetry", queue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TryGetProperty", provider);
        Assert.DoesNotContain("SessionDisplayResolver", provider);
        Assert.Contains("SessionDisplayResolver.Resolve(", projector);
        Assert.Contains(
            "transition.HistoryGeneration > _appliedStateGeneration",
            history);
        var historyAdmission = Slice(
            history,
            "private async Task LoadCoreAsync(",
            "if (!canBegin)");
        Assert.Contains("lock (_gate)", historyAdmission);
        Assert.Contains("generationToken = _generationCancellation.Token", historyAdmission);
        Assert.Contains("canBegin = _state.TryBeginHistory(", historyAdmission);
    }

    [Fact]
    public void RuntimeSubstates_AreLockFreeAndVersionOwnershipIsUnique()
    {
        var state = Read("ChatConversationState.cs");
        var substateNames = new[]
        {
            "ChatApprovalState.cs",
            "ChatHistoryState.cs",
            "ChatPresentationState.cs",
            "ChatQueueState.cs",
            "ChatLifecycleState.cs",
            "ChatResetState.cs",
        };
        var substates = substateNames.ToDictionary(name => name, Read);

        Assert.Contains("private readonly object _gate", state);
        foreach (var (name, source) in substates)
        {
            Assert.DoesNotContain("private readonly object _gate", source);
            Assert.DoesNotContain("lock (", source);
            Assert.DoesNotContain("SemaphoreSlim", source);
            Assert.DoesNotContain("ReaderWriterLock", source);
            Assert.DoesNotContain("Monitor.", source);
            Assert.DoesNotContain("Telemetry", source, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("private readonly Dictionary<string, long> _versions", substates["ChatResetState.cs"]);
        Assert.Contains("private long _connectionGeneration", substates["ChatHistoryState.cs"]);
        Assert.Contains("private readonly Dictionary<string, long> _revisions", substates["ChatHistoryState.cs"]);
        Assert.Contains("private readonly Dictionary<string, string> _sessionIds", substates["ChatHistoryState.cs"]);
        Assert.DoesNotContain("_resetVersions", state);
        Assert.DoesNotContain("_historyRevisions", state);
        Assert.DoesNotContain("_connectionGeneration", state);
        Assert.DoesNotContain("_sessionIds", state);
    }

    [Fact]
    public void Root_CoordinatesCrossDomainCommitsUnderSoleGate()
    {
        var state = Read("ChatConversationState.cs");
        var reconnect = Slice(
            state,
            "internal ChatStatusTransition ApplyStatus(",
            "internal ChatSessionsTransition ApplySessions(");
        var dispose = Slice(
            state,
            "internal ChatDisposeTransition DisposeState()",
            "internal bool TryRaiseKeylessDiagnostic()");
        var reset = Slice(
            state,
            "internal ChatResetTransition ResetThread(",
            "internal ChatIncomingMessageGate GateIncomingChatMessage(");
        var history = Slice(
            state,
            "internal bool CommitHistory(",
            "internal ChatDataSnapshot? SnapshotIfHistoryTokenCurrent(");
        var queue = Slice(
            state,
            "internal ChatQueuedAdmission AdmitMessage(",
            "internal ChatDataSnapshot EnqueueCompact(");
        var agentEvent = Slice(
            state,
            "internal ChatAgentEventTransition ProcessAgentEvent(",
            "private ChatAgentEventGate GateAgentEventLocked(");

        Assert.All(new[] { reconnect, dispose, reset, history, queue, agentEvent },
            transition => Assert.Contains("lock (_gate)", transition));
        Assert.All(new[] { "_history.AdvanceConnectionGeneration", "_queue.ClearForReconnect", "_reset.ClearSubmittedEchoesForReconnect", "_lifecycle.ClearForReconnect" },
            operation => Assert.Contains(operation, reconnect));
        Assert.All(new[] { "_history.AdvanceConnectionGeneration", "_queue.ClearForDispose", "_reset.ClearSubmittedEchoesForReconnect", "_lifecycle.ClearForDispose" },
            operation => Assert.Contains(operation, dispose));
        Assert.All(new[] { "_history.ClearSessionForReset", "_reset.BeginReset", "_lifecycle.ClearThreadForReset", "_queue.ClearThreadForReset", "_timelines[threadId]" },
            operation => Assert.Contains(operation, reset));
        Assert.All(new[] { "_history.IsCurrent", "ChatHistoryState.MergeWithLiveEntries", "_history.MarkCommitted" },
            operation => Assert.Contains(operation, history));
        Assert.All(new[] { "_queue.NextMessageId", "_lifecycle.ClearThreadSuppression", "CanSendDirectlyLocked", "StartDirectSendLocked", "BuildSnapshotLocked" },
            operation => Assert.Contains(operation, queue));
        Assert.All(new[] { "GateAgentEventLocked", "UpdateRunTrackingLocked", "_lifecycle.ShouldSuppress", "ChatEventMapper.Map", "_approval.MarkSeen", "ApplyEventLocked" },
            operation => Assert.Contains(operation, agentEvent));
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker: {start}");
        Assert.True(endIndex > startIndex, $"Missing end marker: {end}");
        return source[startIndex..endIndex];
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            fileName));
}
