using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatRunCorrelationTests
{
    [Fact]
    public void Lifecycle_CancelledRunStaysCancelledAfterThreadSuppressionClears()
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "old");
        state.BeginAbort("main", hadActiveTurn: true);
        state.CompleteAbort("main", "old");
        Assert.False(state.IsThreadSuppressed("main"));
        Assert.True(state.IsCancelledRun("old"));
        state.StartRun("main", "new");
        Assert.True(state.IsCancelledRun("old"));
        Assert.False(state.IsCancelledRun("new"));
    }

    [Fact]
    public void Lifecycle_CancelledRunStaysCancelledAfterItsTerminal()
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "old");
        state.BeginAbort("main", hadActiveTurn: true);
        state.RemoveAbortedRun("old");
        Assert.False(state.IsRunAborted("old"));
        Assert.True(state.IsRetiredCancelledRun("old"));
        Assert.True(state.IsCancelledRun("old"));
    }

    [Fact]
    public void Lifecycle_RolledBackAbortDoesNotRetireRun()
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "run");
        state.BeginAbort("main", hadActiveTurn: true);
        state.RollbackAbort("main", "run");
        Assert.False(state.IsCancelledRun("run"));
        state.RemoveAbortedRun("run");
        Assert.False(state.IsCancelledRun("run"));
    }

    [Fact]
    public void Lifecycle_RetiredCancelledRunsAreBoundedByArrivalOrder()
    {
        var state = new ChatLifecycleState();
        for (var i = 0; i <= 64; i++)
            state.RetireResetRun($"run-{i}");
        Assert.False(state.IsCancelledRun("run-0"));
        Assert.True(state.IsCancelledRun("run-1"));
        Assert.True(state.IsCancelledRun("run-64"));
    }

    [Fact]
    public void Lifecycle_FailedAbortAfterTerminalReleasesAbortRetirement()
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "run");
        state.BeginAbort("main", hadActiveTurn: true);
        state.RemoveAbortedRun("run");
        Assert.True(state.IsCancelledRun("run"));
        state.RollbackAbort("main", "run");
        Assert.False(state.IsCancelledRun("run"));
    }

    [Fact]
    public void Lifecycle_FailedAbortDoesNotClearIndependentResetFence()
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "run");
        state.BeginAbort("main", hadActiveTurn: true);
        state.RemoveAbortedRun("run");
        state.RetireResetRun("run");
        state.RollbackAbort("main", "run");
        Assert.True(state.IsCancelledRun("run"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Lifecycle_MissingRunIdRetainsLegacyThreadAdmission(string? runId)
    {
        var state = new ChatLifecycleState();
        state.StartRun("main", "current");
        state.RetireResetRun("old");
        Assert.False(state.IsCancelledRun(runId));
        Assert.False(state.ShouldSuppress("main", runId));
        state.BeginAbort("main", hadActiveTurn: true);
        Assert.True(state.ShouldSuppress("main", runId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_CancelledRunChatIsSuppressedBeforeAndAfterItsTerminal(bool terminalBeforeNewRun)
    {
        var (state, context) = CreateState();
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.BeginAbort("main");
        state.CompleteAbort("main", "old");
        if (terminalBeforeNewRun)
            state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context);
        state.ProcessAgentEvent(Lifecycle("new", "start"), "main", context);
        if (!terminalBeforeNewRun)
            state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context);

        Assert.True(state.GateIncomingChatMessage(AssistantFinal("old", "Stale final"), context).Suppressed);
        Assert.False(state.GateIncomingChatMessage(AssistantFinal("new", "Current final"), context).Suppressed);
    }

    [Fact]
    public void Gate_CancelledRunLateAgentOutputAfterTerminalIsDropped()
    {
        var (state, context) = CreateState();
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.BeginAbort("main");
        state.CompleteAbort("main", "old");
        Assert.True(state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context).Process);
        var lateDelta = new AgentEventInfo
        {
            SessionKey = "main", RunId = "old", Stream = "assistant",
            Data = JsonSerializer.SerializeToElement(new { delta = "late" }),
        };
        Assert.False(state.ProcessAgentEvent(lateDelta, "main", context).Process);
        Assert.False(state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context).Process);
    }

    [Fact]
    public void Gate_AbortedStartReplayCannotReplaceCurrentRun()
    {
        var (state, context) = CreateState();
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.BeginAbort("main");
        state.CompleteAbort("main", "old");
        state.ProcessAgentEvent(Lifecycle("new", "start"), "main", context);
        state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context);
        Assert.False(state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context).Process);
        Assert.False(state.GateIncomingChatMessage(AssistantFinal("new", "Current answer"), context).Suppressed);
        Assert.True(state.ProcessAgentEvent(Lifecycle("new", "end"), "main", context).Process);
    }

    [Fact]
    public void Gate_CompletedNonCancelledRunIsNotFenced()
    {
        var (state, context) = CreateState();
        state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context);
        state.ProcessAgentEvent(Lifecycle("run", "end"), "main", context);
        var final = state.GateIncomingChatMessage(AssistantFinal("run", "Trailing final"), context);
        Assert.False(final.Suppressed || final.Drop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_FailedAbortRestoresTrailingFinalRegardlessOfTerminalOrder(bool terminalBeforeRollback)
    {
        var (state, context) = CreateState();
        state.ProcessAgentEvent(Lifecycle("run", "start"), "main", context);
        state.BeginAbort("main");
        if (terminalBeforeRollback)
            state.ProcessAgentEvent(Lifecycle("run", "end"), "main", context);
        state.RollbackAbort("main", "run");
        if (!terminalBeforeRollback)
            state.ProcessAgentEvent(Lifecycle("run", "end"), "main", context);

        var final = state.GateIncomingChatMessage(AssistantFinal("run", "Trailing final"), context);
        Assert.False(final.Suppressed || final.Drop,
            "A failed abort left the uncancelled run's trailing final fenced.");
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("expired")]
    public void Gate_RetiredRunCanCloseOnlyItsPendingApproval(string terminalPhase)
    {
        var (state, context) = CreateState();
        state.ProcessAgentEvent(Lifecycle("old", "start"), "main", context);
        state.ProcessAgentEvent(Approval("old", "requested", "appr-old"), "main", context);
        Assert.Equal("appr-old", state.Snapshot(context).Timelines["main"].PendingPermission?.RequestId);
        state.BeginAbort("main");
        state.CompleteAbort("main", "old");
        state.ProcessAgentEvent(Lifecycle("old", "end"), "main", context);

        Assert.False(state.ProcessAgentEvent(Approval("old", terminalPhase, "appr-other"), "main", context).Process);
        Assert.False(state.ProcessAgentEvent(Approval("old", "requested", "appr-new"), "main", context).Process);
        Assert.Equal("appr-old", state.Snapshot(context).Timelines["main"].PendingPermission?.RequestId);

        Assert.True(state.ProcessAgentEvent(Approval("old", terminalPhase, "appr-old"), "main", context).Process);
        Assert.Null(state.Snapshot(context).Timelines["main"].PendingPermission);
        var lateOutput = new AgentEventInfo
        {
            SessionKey = "main", RunId = "old", Stream = "assistant",
            Data = JsonSerializer.SerializeToElement(new { delta = "late" }),
        };
        Assert.False(state.ProcessAgentEvent(lateOutput, "main", context).Process);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_TerminalDoesNotForgetSuppressionBeforeLateFinal(bool terminalAfterNewRun)
    {
        var (state, context) = CreateState();
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

    private static (ChatConversationState State, ChatProjectionContext Context) CreateState()
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "main", IsMain = true }], context);
        return (state, context);
    }

    private static ChatMessageInfo AssistantFinal(string runId, string text) => new()
    {
        SessionKey = "main", RunId = runId, Role = "assistant", Text = text, State = "final",
    };

    private static AgentEventInfo Approval(string runId, string phase, string approvalId) => new()
    {
        SessionKey = "main",
        RunId = runId,
        Stream = "approval",
        Data = JsonSerializer.SerializeToElement(new
        {
            phase,
            approvalId,
            approvalSlug = approvalId,
            host = "gateway",
            command = "openclaw nodes invoke --node \"Windows Node\" --command system.run",
            title = "Exec approval",
            message = "Approve this exec?",
        }),
    };

    private static AgentEventInfo Lifecycle(string runId, string phase, long timestamp = 0) => new()
    {
        SessionKey = "main",
        RunId = runId,
        Stream = "lifecycle",
        Ts = timestamp,
        Data = JsonSerializer.SerializeToElement(new { phase }),
    };
}
