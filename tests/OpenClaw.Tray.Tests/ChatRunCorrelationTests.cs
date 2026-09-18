using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatRunCorrelationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifecycle_AbortedTerminalPreservesNewerTrailingFinal(bool legacyJob)
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.BeginAbort("main");
        state.CompleteAbort("main", "old");
        state.ProcessAgentEvent(Lifecycle("new", "start"), "main", context);
        state.ProcessAgentEvent(Lifecycle("new", "end"), "main", context);
        var oldTerminal = legacyJob
            ? new AgentEventInfo
            {
                SessionKey = "main", RunId = "old", Stream = "job",
                Data = JsonSerializer.SerializeToElement(new { state = "done" }),
            }
            : Lifecycle("old", "end");
        state.ProcessAgentEvent(oldTerminal, "main", context);
        var current = state.GateIncomingChatMessage(new ChatMessageInfo
        {
            SessionKey = "main", RunId = "new", Role = "assistant",
            Text = "Complete newer answer", State = "final",
        }, context);
        Assert.False(current.Suppressed || current.Drop,
            "A cancelled terminal displaced the newer run's trailing final.");
    }

    [Fact]
    public void Lifecycle_TerminalCacheEvictsOldestNotMostRecentSuppressedRun()
    {
        var state = new ChatLifecycleState();
        for (var i = 0; i < 63; i++)
            state.CompleteAssistantFinal("main", $"normal-{i}");
        state.RememberSuppressedTerminal("main", "reset-recent");
        state.RememberSuppressedTerminal("main", "reset-next");

        Assert.True(state.ShouldSuppressChatMessage("main", "reset-recent", true, [], false),
            "The next terminal evicted the most recent suppressed run.");
        Assert.True(state.ShouldSuppressChatMessage("main", "reset-next", true, [], false));
        Assert.False(state.ShouldSuppressChatMessage("main", "normal-0", true, [], false));
        Assert.True(state.ShouldSuppressChatMessage("main", "normal-1", true, [], false));
        Assert.False(state.ShouldSuppressChatMessage("other", "reset-recent", true, [], false));
    }

    [Fact]
    public void Lifecycle_AbortedStartReplayCannotReplaceCurrentRun()
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.BeginAbort("main");
        state.CompleteAbort("main", "old");
        state.ProcessAgentEvent(Lifecycle("new", "start"), "main", context);
        state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context);
        Assert.False(state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context).Process);
        var current = state.GateIncomingChatMessage(new ChatMessageInfo
        {
            SessionKey = "main", RunId = "new", Role = "assistant",
            Text = "Current answer", State = "final",
        }, context);
        Assert.False(current.Suppressed || current.Drop);
        Assert.True(state.ProcessAgentEvent(Lifecycle("new", "end"), "main", context).Process);
    }

    [Theory]
    [InlineData("""{}""", true)]
    [InlineData("""{"executionSettled":false,"fallbackExhaustedFailure":false}""", true)]
    [InlineData("""{"executionSettled":"true","fallbackExhaustedFailure":1}""", true)]
    [InlineData("""{"executionSettled":true}""", false)]
    [InlineData("""{"fallbackExhaustedFailure":true}""", false)]
    [InlineData("""{"stopReason":"timeout"}""", false)]
    [InlineData("""{"timeoutPhase":"queue"}""", false)]
    [InlineData("""{"timeoutPhase":"provider"}""", false)]
    [InlineData("""{"timeoutPhase":" preflight "}""", false)]
    [InlineData("""{"timeoutPhase":"post_turn"}""", false)]
    [InlineData("""{"timeoutPhase":"gateway_draining"}""", false)]
    [InlineData("""{"timeoutPhase":"unknown"}""", true)]
    [InlineData("""{"status":" TIMEOUT "}""", true)]
    [InlineData("""{"stopReason":" timeout "}""", true)]
    [InlineData("""{"stopReason":" stop "}""", true)]
    [InlineData("""{"aborted":true,"stopReason":"   "}""", false)]
    [InlineData("""{"status":" CANCELLED "}""", true)]
    [InlineData("""{"status":"timed_out"}""", false)]
    [InlineData("""{"status":"cancelled"}""", false)]
    [InlineData("""{"status":"canceled","stopReason":"custom"}""", false)]
    [InlineData("""{"status":"aborted"}""", false)]
    [InlineData("""{"status":"superseded"}""", false)]
    [InlineData("""{"status":"failed"}""", true)]
    [InlineData("""{"aborted":true}""", false)]
    [InlineData("""{"aborted":true,"stopReason":"custom"}""", true)]
    [InlineData("""{"aborted":"true"}""", true)]
    [InlineData("""{"stopReason":"rpc"}""", false)]
    [InlineData("""{"stopReason":"stop"}""", false)]
    [InlineData("""{"stopReason":"restart"}""", false)]
    [InlineData("""{"stopReason":"aborted"}""", false)]
    [InlineData("""{"stopReason":"superseded"}""", false)]
    [InlineData("""{"stopReason":"error"}""", true)]
    [InlineData("""{"stopReason":"TIMEOUT"}""", true)]
    [InlineData("""{"livenessState":" Blocked "}""", false)]
    [InlineData("""{"livenessState":"abandoned"}""", false)]
    [InlineData("""{"providerStarted":true}""", true)]
    public void Lifecycle_ErrorRetryMatchesDefinitiveUpstreamFacts(string json, bool retryable)
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context);
        var error = Lifecycle("run", "error");
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        data["phase"] = "error";
        error.Data = JsonSerializer.SerializeToElement(data);
        state.ProcessAgentEvent(error, "main", context);
        Assert.Equal(retryable, state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context).Process);
    }

    [Fact]
    public void Lifecycle_ErrorCanRetrySameRunWithoutWeakeningCompletedRunFence()
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context);
        state.ProcessAgentEvent(Lifecycle("run", "error"), "main", context);
        Assert.True(state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context).Process,
            "A retryable lifecycle error prevented the same run from restarting.");
        Assert.False(state.GateIncomingChatMessage(new ChatMessageInfo
        {
            SessionKey = "main", RunId = "run", Role = "assistant", Text = "Retry result", State = "final",
        }, context).Suppressed);
        state.CompleteAssistantFinal("main", "run");
        Assert.False(state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context).Process);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("abort")]
    public void Lifecycle_ErrorRetryCannotDisplaceNewerRun(string newerPhase)
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.ProcessAgentEvent(Lifecycle("old", "error"), "main", context);
        state.ProcessAgentEvent(Lifecycle("new", "start"), "main", context);
        if (newerPhase == "end")
            state.ProcessAgentEvent(Lifecycle("new", "end"), "main", context);
        if (newerPhase == "abort")
        {
            state.BeginAbort("main");
            state.CompleteAbort("main", "new");
            state.ProcessAgentEvent(Lifecycle("new", "end"), "main", context);
        }
        Assert.False(state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context).Process);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_TerminalDoesNotForgetSuppressionBeforeLateFinal(bool terminalAfterNewRun)
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.ResetThread("main", context);
        if (!terminalAfterNewRun)
            Assert.True(state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context).ReloadHistory);

        var timestamp = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        state.GateIncomingChatMessage(new ChatMessageInfo
        {
            SessionKey = "main", Role = "user", Text = "Fresh question", Ts = timestamp,
        }, context);
        Assert.True(state.ProcessAgentEvent(Lifecycle("new", "start", timestamp), "main", context).Process);
        Assert.True(state.ProcessAgentEvent(Lifecycle("new", "end", timestamp + 1), "main", context).Process);
        if (terminalAfterNewRun)
            Assert.True(state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context).ReloadHistory);

        var stale = state.GateIncomingChatMessage(new ChatMessageInfo
        {
            SessionKey = "main", RunId = "old", Role = "assistant",
            Text = "Late pre-reset final", State = "final", Ts = timestamp + 2,
        }, context);
        Assert.True(stale.Suppressed || stale.Drop,
            "A reset-invalidated run's terminal forgot suppression before its late final.");
        var current = state.GateIncomingChatMessage(new ChatMessageInfo
        {
            SessionKey = "main", RunId = "new", Role = "assistant",
            Text = "Current trailing final", State = "final", Ts = timestamp + 3,
        }, context);
        Assert.False(current.Suppressed || current.Drop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifecycle_SuppressesCancelledChatAfterThreadSuppressionClears(bool final)
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "old");
        state.BeginAbort("main", hadActiveTurn: true);
        state.CompleteAbort("main", "old");
        Assert.False(state.IsThreadSuppressed("main"));
        Assert.True(state.ShouldSuppressChatMessage("main", "old", final, [], turnActive: false));
        state.StartRun("main", "new");
        Assert.True(state.ShouldSuppressChatMessage("main", "old", final, [], turnActive: true));
        Assert.False(state.ShouldSuppressChatMessage("main", "new", final, [], turnActive: true));
    }

    [Fact]
    public void Lifecycle_FinalWithoutStartRemembersWireRunAndRejectsReplay()
    {
        var state = new ChatLifecycleState();
        Assert.Equal("wire-run", state.CompleteAssistantFinal("main", "wire-run"));
        Assert.True(state.ShouldSuppressChatMessage("main", "wire-run", true, [], turnActive: false));
        Assert.True(state.ShouldDropTerminal("main", "wire-run", [], false, out _));
        Assert.False(state.ShouldSuppressChatMessage("main", "remote-next", true, [], turnActive: false));
    }

    [Theory]
    [InlineData("assistant", """{"delta":"late"}""")]
    [InlineData("reasoning", """{"delta":"late"}""")]
    [InlineData("lifecycle", """{"phase":"start"}""")]
    public void Lifecycle_CompletedRunCannotRestartThroughAgentOutput(string stream, string data)
    {
        var state = new ChatLifecycleState();
        state.CompleteAssistantFinal("main", "completed");
        var evt = new AgentEventInfo
        {
            SessionKey = "main", RunId = "completed", Stream = stream,
            Data = JsonSerializer.Deserialize<JsonElement>(data),
        };
        Assert.True(state.ShouldSuppressCompletedAgentEvent("main", evt));
        Assert.False(state.ShouldSuppressCompletedAgentEvent("other", evt));
        evt.RunId = "remote-next";
        Assert.False(state.ShouldSuppressCompletedAgentEvent("main", evt));
    }

    [Theory]
    [InlineData(false, "start")]
    [InlineData(false, "result")]
    [InlineData(true, "start")]
    [InlineData(true, "result")]
    public void Lifecycle_CompletedToolRepairIsAllowedOnlyForNonAbortedRun(bool aborted, string phase)
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "run");
        if (aborted)
            state.BeginAbort("main", hadActiveTurn: true);
        Assert.False(state.ShouldDropTerminal("main", "run", [], true, out _));
        state.RemoveAbortedRun("run");
        var evt = new AgentEventInfo
        {
            SessionKey = "main", RunId = "run", Stream = "tool",
            Data = JsonSerializer.SerializeToElement(new { phase, name = "tool", toolCallId = "tool-1", result = "completed" }),
        };
        Assert.Equal(aborted, state.ShouldSuppressCompletedAgentEvent("main", evt, canReconcile: true));
        Assert.True(state.ShouldSuppressCompletedAgentEvent("main", evt, canReconcile: false));
    }

    [Fact]
    public void Lifecycle_OnlyMostRecentCompletedRunCanSupplyTrailingFinal()
    {
        var state = new ChatLifecycleState();
        Assert.False(state.ShouldDropTerminal("main", "old", [], false, out _));
        Assert.False(state.ShouldDropTerminal("main", "new", [], false, out _));
        Assert.True(state.ShouldSuppressChatMessage("main", "old", true, [], turnActive: false));
        Assert.False(state.ShouldSuppressChatMessage("main", "new", true, [], turnActive: false));
        Assert.True(state.ShouldSuppressChatMessage("main", "new", false, [], turnActive: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Lifecycle_MissingRunIdRetainsLegacyAdmission(string? runId)
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "current");
        Assert.False(state.ShouldSuppressChatMessage("main", runId, true, [], turnActive: true));
        state.BeginAbort("main", hadActiveTurn: true);
        Assert.True(state.ShouldSuppressChatMessage("main", runId, true, [], turnActive: true));
    }

    [Fact]
    public void Reset_ExplicitIgnoredRunCannotBorrowCurrentRunTimestampAdmission()
    {
        var state = new ChatResetState();
        state.AddIgnoredRun("main", "old");
        var ignored = state.EvaluateChatMessage(
            "main", "assistant", "late output", timestampMs: long.MaxValue,
            hasPendingLocalEcho: false, activeRunId: "new", runId: "old");
        var current = state.EvaluateChatMessage(
            "main", "assistant", "current output", timestampMs: long.MaxValue,
            hasPendingLocalEcho: false, activeRunId: "new", runId: "new");
        var otherThread = state.EvaluateChatMessage(
            "other", "assistant", "other output", timestampMs: long.MaxValue,
            hasPendingLocalEcho: false, runId: "old");
        Assert.True(ignored.Drop);
        Assert.False(current.Drop);
        Assert.False(otherThread.Drop);
    }

    private static AgentEventInfo Lifecycle(string runId, string phase, long timestamp = 0) => new()
    {
        SessionKey = "main",
        RunId = runId,
        Stream = "lifecycle",
        Ts = timestamp,
        Data = JsonSerializer.SerializeToElement(new { phase }),
    };
}
