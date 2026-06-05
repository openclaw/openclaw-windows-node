using OpenClaw.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatTimelineReducerTests
{
    [Fact]
    public void ToolStart_BeginsTurnWhenLifecycleStartWasMissed()
    {
        var state = ChatTimelineState.Initial();

        var updated = ChatTimelineReducer.Apply(
            state,
            new ChatToolStartEvent("powershell", "powershell"));

        Assert.True(updated.TurnActive);
        Assert.Single(updated.Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, updated.Entries[0].Kind);
    }

    [Fact]
    public void Error_EndsActiveTurn()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatThinkingEvent(string.Empty));

        var updated = ChatTimelineReducer.Apply(
            state,
            new ChatErrorEvent("Agent error"));

        Assert.False(updated.TurnActive);
        Assert.Null(updated.ActiveAssistantId);
        Assert.Null(updated.ActiveReasoningId);
        Assert.Null(updated.ActiveToolCallId);
        Assert.Null(updated.PendingPermission);
        Assert.Single(updated.Entries);
        Assert.Equal(ChatTimelineItemKind.Status, updated.Entries[0].Kind);
        Assert.Equal(ChatTone.Error, updated.Entries[0].Tone);
    }

    [Fact]
    public void FinalAssistant_UpdatesStreamingAssistantAfterTurnEnd()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageDeltaEvent("partial"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("final", ReconcilePrevious: true));

        Assert.Single(updated.Entries);
        Assert.Equal("final", updated.Entries[0].Text);
        Assert.False(updated.Entries[0].IsStreaming);
    }

    [Fact]
    public void DuplicateFinalAssistant_DoesNotCreateSecondEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageEvent("final"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("final", ReconcilePrevious: true));

        Assert.Single(updated.Entries);
        Assert.Equal("final", updated.Entries[0].Text);
    }

    [Fact]
    public void DuplicateFinalAssistant_IdenticalText_DedupesWithoutReconcileFlag()
    {
        // Reproduces the duplicate-bubble screenshot bug: gateway re-emits
        // the exact same final message after a turn end without setting the
        // ReconcilePrevious flag. The reducer must collapse identical-text
        // duplicates as a safety net so the UI doesn't render the same
        // assistant text twice in a row.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageEvent("I don't see a pending approval."));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
        Assert.False(state.TurnActive);

        var updated = ChatTimelineReducer.Apply(
            state,
            new ChatMessageEvent("I don't see a pending approval."));

        Assert.Single(updated.Entries);
        Assert.Equal("I don't see a pending approval.", updated.Entries[0].Text);
    }

    [Fact]
    public void DuplicateFinalAssistant_DoesNotReactivatePreviousAssistant()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageEvent("previous"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
        state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("previous"));
        state = ChatTimelineReducer.Apply(state, new ChatUserMessageEvent("next request"));

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("next response"));

        Assert.Equal(3, updated.Entries.Count);
        Assert.Equal("previous", updated.Entries[0].Text);
        Assert.Equal(ChatTimelineItemKind.User, updated.Entries[1].Kind);
        Assert.Equal("next response", updated.Entries[2].Text);
    }

    [Fact]
    public void SubsequentAssistant_DifferentText_AfterTurnEnd_CreatesNewEntry()
    {
        // Guard against over-aggressive dedupe: a genuinely new assistant
        // message in a later turn (different text, no reconcile flag, turn
        // already ended) must NOT be merged into the previous entry.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageEvent("first"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("second"));

        Assert.Equal(2, updated.Entries.Count);
        Assert.Equal("first", updated.Entries[0].Text);
        Assert.Equal("second", updated.Entries[1].Text);
    }

    [Fact]
    public void AddLocalUser_CapsTrackedNonces()
    {
        var state = ChatTimelineState.Initial();
        for (var i = 0; i < 300; i++)
        {
            state = ChatTimelineReducer.AddLocalUser(state, $"message {i}", $"nonce-{i}");
        }

        Assert.Equal(256, state.LocalNonces.Count);
        Assert.Contains("nonce-299", state.LocalNonces);
    }

    // ── ToolCallId matching ──

    [Fact]
    public void ToolOutput_WithToolCallId_MatchesCorrectToolEntry()
    {
        var state = ChatTimelineState.Initial();

        // Start two tools with different ToolCallIds
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("grep foo", "grep", ToolCallId: "call-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls dir", "ls", ToolCallId: "call-2"));

        Assert.Equal(2, state.Entries.Count);
        Assert.Equal(ChatToolCallStatus.InProgress, state.Entries[0].ToolResult);
        Assert.Equal(ChatToolCallStatus.InProgress, state.Entries[1].ToolResult);

        // Complete the first tool by ToolCallId (out of order)
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("found 3 matches", ToolCallId: "call-1"));

        Assert.Equal(ChatToolCallStatus.Success, state.Entries[0].ToolResult);
        Assert.Equal("found 3 matches", state.Entries[0].ToolOutput);
        // Second tool still in progress
        Assert.Equal(ChatToolCallStatus.InProgress, state.Entries[1].ToolResult);
    }

    [Fact]
    public void ToolError_WithToolCallId_MatchesCorrectToolEntry()
    {
        var state = ChatTimelineState.Initial();

        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("run script", "bash", ToolCallId: "call-A"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("read file", "cat", ToolCallId: "call-B"));

        // Error the second tool
        state = ChatTimelineReducer.Apply(state,
            new ChatToolErrorEvent("file not found", ToolCallId: "call-B"));

        // First tool still in progress
        Assert.Equal(ChatToolCallStatus.InProgress, state.Entries[0].ToolResult);
        // Second tool errored
        Assert.Equal(ChatToolCallStatus.Error, state.Entries[1].ToolResult);
        Assert.Equal("file not found", state.Entries[1].ToolOutput);
    }

    [Fact]
    public void ToolOutput_WithoutToolCallId_FallsBackToActiveToolCallId()
    {
        var state = ChatTimelineState.Initial();

        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("powershell", "powershell"));

        // Output without ToolCallId should use ActiveToolCallId fallback
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("output text"));

        Assert.Single(state.Entries);
        Assert.Equal(ChatToolCallStatus.Success, state.Entries[0].ToolResult);
        Assert.Equal("output text", state.Entries[0].ToolOutput);
    }

    [Fact]
    public void ToolStart_StoresToolCallIdOnEntry()
    {
        var state = ChatTimelineState.Initial();

        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("grep foo", "grep", ToolCallId: "tc-42"));

        Assert.Single(state.Entries);
        Assert.Equal("tc-42", state.Entries[0].ToolCallId);
    }

    [Fact]
    public void ToolOutput_WithUnknownToolCallId_DoesNotCorruptActiveEntry()
    {
        var state = ChatTimelineState.Initial();

        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("grep foo", "grep", ToolCallId: "call-1"));

        // Output with an unknown ToolCallId should NOT fall back to active
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("stale output", ToolCallId: "call-unknown"));

        // Active tool should remain InProgress — not corrupted
        Assert.Equal(ChatToolCallStatus.InProgress, state.Entries[0].ToolResult);
        Assert.Null(state.Entries[0].ToolOutput);
    }

    [Fact]
    public void TurnEnd_ClearsActiveToolCallId()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatToolStartEvent("powershell", "powershell"));

        Assert.NotNull(state.ActiveToolCallId);

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.Null(updated.ActiveToolCallId);
    }

    [Fact]
    public void TurnEnd_MarksInProgressToolAsInterrupted()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatToolStartEvent("powershell", "powershell"));

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.Single(updated.Entries);
        Assert.Equal(ChatToolCallStatus.Interrupted, updated.Entries[0].ToolResult);
    }

    [Fact]
    public void TurnEnd_WithNoActiveTool_IsNoOpForToolState()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatThinkingEvent(string.Empty));

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.False(updated.TurnActive);
        Assert.Null(updated.ActiveToolCallId);
    }

    [Fact]
    public void TurnEnd_MarksMultipleParallelToolsAsInterrupted()
    {
        var state = ChatTimelineState.Initial();
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("read", "read", ToolCallId: "tc1"));
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("grep", "grep", ToolCallId: "tc2"));

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.Equal(2, updated.Entries.Count);
        Assert.Equal(ChatToolCallStatus.Interrupted, updated.Entries[0].ToolResult);
        Assert.Equal(ChatToolCallStatus.Interrupted, updated.Entries[1].ToolResult);
        Assert.Null(updated.ActiveToolCallId);
        Assert.Empty(updated.ActiveToolCalls);
    }

    [Fact]
    public void ToolOutput_WithToolCallId_MatchesCorrectTool()
    {
        var state = ChatTimelineState.Initial();
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("read foo", "read", ToolCallId: "tc1"));
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("grep bar", "grep", ToolCallId: "tc2"));

        // Output for tc1 arrives (even though tc2 started later)
        var updated = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent("file contents", ToolCallId: "tc1"));

        Assert.Equal(ChatToolCallStatus.Success, updated.Entries[0].ToolResult);
        Assert.Equal("file contents", updated.Entries[0].ToolOutput);
        // tc2 still in progress
        Assert.Equal(ChatToolCallStatus.InProgress, updated.Entries[1].ToolResult);
    }

    [Fact]
    public void ToolOutput_WithToolCallId_PreservesOutputWhenEndEventIsEmpty()
    {
        var state = ChatTimelineState.Initial();
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("exec command", "exec", ToolCallId: "tc1"));
        state = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent("command output", ToolCallId: "tc1"));

        var updated = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent(string.Empty, ToolCallId: "tc1"));

        Assert.Equal(ChatToolCallStatus.Success, updated.Entries[0].ToolResult);
        Assert.Equal("command output", updated.Entries[0].ToolOutput);
    }

    [Fact]
    public void ToolError_WithToolCallId_MatchesCorrectTool()
    {
        var state = ChatTimelineState.Initial();
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("read foo", "read", ToolCallId: "tc1"));
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("grep bar", "grep", ToolCallId: "tc2"));

        var updated = ChatTimelineReducer.Apply(state, new ChatToolErrorEvent("not found", ToolCallId: "tc2"));

        // tc1 still in progress
        Assert.Equal(ChatToolCallStatus.InProgress, updated.Entries[0].ToolResult);
        // tc2 errored
        Assert.Equal(ChatToolCallStatus.Error, updated.Entries[1].ToolResult);
        Assert.Equal("not found", updated.Entries[1].ToolOutput);
    }

    [Fact]
    public void ToolOutput_WithoutToolCallId_FallsBackToLastStarted()
    {
        // Legacy events without ToolCallId use positional fallback
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatToolStartEvent("powershell", "powershell"));

        var updated = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent("output text"));

        Assert.Equal(ChatToolCallStatus.Success, updated.Entries[0].ToolResult);
        Assert.Null(updated.ActiveToolCallId);
    }

    [Fact]
    public void Error_MarksActiveToolAsInterrupted()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatToolStartEvent("powershell", "powershell"));

        var updated = ChatTimelineReducer.Apply(state, new ChatErrorEvent("Something broke"));

        // Tool should be marked Interrupted (not Success — it never completed)
        Assert.Equal(ChatToolCallStatus.Interrupted, updated.Entries[0].ToolResult);
        Assert.Null(updated.ActiveToolCallId);
        Assert.False(updated.TurnActive);
    }

    // ── Reasoning events ──

    [Fact]
    public void Reasoning_CreatesReasoningEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatReasoningEvent("thinking..."));

        Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.Reasoning, state.Entries[0].Kind);
        Assert.Equal("thinking...", state.Entries[0].Text);
        Assert.NotNull(state.ActiveReasoningId);
    }

    [Fact]
    public void ReasoningDelta_AppendsToExistingReasoningEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatReasoningEvent("first"));
        var updated = ChatTimelineReducer.Apply(state, new ChatReasoningDeltaEvent(" second"));

        Assert.Single(updated.Entries);
        Assert.Equal("first second", updated.Entries[0].Text);
    }

    [Fact]
    public void Reasoning_ReplacesTextOnFullEvent()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatReasoningDeltaEvent("partial"));
        var updated = ChatTimelineReducer.Apply(state, new ChatReasoningEvent("final"));

        Assert.Single(updated.Entries);
        Assert.Equal("final", updated.Entries[0].Text);
    }

    [Fact]
    public void TurnEnd_ClearsActiveReasoningId()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatReasoningEvent("thinking"));

        Assert.NotNull(state.ActiveReasoningId);

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.Null(updated.ActiveReasoningId);
    }

    // ── Intent events ──

    [Fact]
    public void Intent_SetsCurrentIntent()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatIntentEvent("searching files"));

        Assert.Equal("searching files", state.CurrentIntent);
        Assert.Empty(state.Entries); // no timeline entry
    }

    [Fact]
    public void Intent_OverwritesPreviousIntent()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatIntentEvent("first"));
        var updated = ChatTimelineReducer.Apply(state, new ChatIntentEvent("second"));

        Assert.Equal("second", updated.CurrentIntent);
    }

    // ── Permission request events ──

    [Fact]
    public void PermissionRequest_SetsPendingPermissionAndPushesEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        Assert.NotNull(state.PendingPermission);
        Assert.Equal("req-1", state.PendingPermission!.RequestId);
        Assert.Equal("shell.exec", state.PendingPermission.PermissionKind);
        Assert.Equal("bash", state.PendingPermission.ToolName);
        Assert.Equal("run script.sh", state.PendingPermission.Detail);

        // Inline timeline entry — the in-bubble approval lives in the
        // conversation now (composer banner removed).
        var entry = Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.PermissionRequest, entry.Kind);
        Assert.Equal("req-1", entry.PermissionRequestId);
        Assert.Equal(ChatPermissionDecision.Pending, entry.PermissionDecision);
        Assert.Equal("run script.sh", entry.Text);
        Assert.Equal("bash", entry.ToolName);
        Assert.Equal("shell.exec", entry.IntentSummary);
    }

    [Fact]
    public void ClearPermission_RemovesPendingPermissionAndMarksEntryExpired()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        Assert.NotNull(state.PendingPermission);

        var updated = ChatTimelineReducer.ClearPermission(state);

        Assert.Null(updated.PendingPermission);
        var entry = Assert.Single(updated.Entries);
        Assert.Equal(ChatPermissionDecision.Expired, entry.PermissionDecision);
    }

    [Fact]
    public void ResolvePermission_Allowed_StampsEntryAndClearsPending()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        var updated = ChatTimelineReducer.ResolvePermission(state, "req-1", ChatPermissionDecision.Allowed);

        Assert.Null(updated.PendingPermission);
        var entry = Assert.Single(updated.Entries);
        Assert.Equal(ChatPermissionDecision.Allowed, entry.PermissionDecision);
    }

    [Fact]
    public void ApplyPermissionRequest_EmptyRequestId_DroppedToAvoidOrphanedEntry()
    {
        var initial = ChatTimelineState.Initial();
        var afterEmpty = ChatTimelineReducer.Apply(
            initial,
            new ChatPermissionRequestEvent("", "shell.exec", "bash", "run script.sh"));
        var afterWhitespace = ChatTimelineReducer.Apply(
            initial,
            new ChatPermissionRequestEvent("   ", "shell.exec", "bash", "run script.sh"));

        Assert.Empty(afterEmpty.Entries);
        Assert.Null(afterEmpty.PendingPermission);
        Assert.Empty(afterWhitespace.Entries);
        Assert.Null(afterWhitespace.PendingPermission);
    }

    [Fact]
    public void ResolvePermission_Denied_StampsEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        var updated = ChatTimelineReducer.ResolvePermission(state, "req-1", ChatPermissionDecision.Denied);

        Assert.Null(updated.PendingPermission);
        var entry = Assert.Single(updated.Entries);
        Assert.Equal(ChatPermissionDecision.Denied, entry.PermissionDecision);
    }

    [Fact]
    public void ResolvePermission_DoesNotDowngradeAlreadyDecidedEntry()
    {
        // Local Allow click stamped the entry Allowed; a subsequent
        // gateway backstop event with decision=Expired must not overwrite
        // the user's choice.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));
        var allowed = ChatTimelineReducer.ResolvePermission(state, "req-1", ChatPermissionDecision.Allowed);

        var backstop = ChatTimelineReducer.ResolvePermission(allowed, "req-1", ChatPermissionDecision.Expired);

        var entry = Assert.Single(backstop.Entries);
        Assert.Equal(ChatPermissionDecision.Allowed, entry.PermissionDecision);
    }

    [Fact]
    public void ResolvePermission_MismatchedRequestId_NoOp()
    {
        // A late terminal event for a stale request must not clobber the
        // current live entry. ResolvePermission walks Entries looking for
        // the matching PermissionRequestId; finding none is a no-op for
        // both entries and PendingPermission (so the user can still act
        // on the live request).
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        var updated = ChatTimelineReducer.ResolvePermission(state, "req-unknown", ChatPermissionDecision.Allowed);

        Assert.NotNull(updated.PendingPermission);
        Assert.Equal("req-1", updated.PendingPermission!.RequestId);
        var entry = Assert.Single(updated.Entries);
        Assert.Equal(ChatPermissionDecision.Pending, entry.PermissionDecision);
    }

    [Fact]
    public void TurnEnd_PreservesPendingPermission()
    {
        // Exec approvals can outlive the originating turn: the gateway emits
        // ``exec.approval.resolved`` after the user clicks Allow/Deny in the
        // dashboard or tray, and that resolution is what should clear the
        // banner — not turn-end. See OpenClawChatDataProvider's approval
        // flow and ChatTimelineReducer.ApplyTurnEnd for the rationale.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.NotNull(updated.PendingPermission);
        Assert.Equal("req-1", updated.PendingPermission!.RequestId);
    }

    [Fact]
    public void ToolOutput_PreservesPendingPermission()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        var updated = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent("ok", ToolCallId: null));

        Assert.NotNull(updated.PendingPermission);
    }

    [Fact]
    public void ToolError_PreservesPendingPermission()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        var updated = ChatTimelineReducer.Apply(state, new ChatToolErrorEvent("boom", ToolCallId: null));

        Assert.NotNull(updated.PendingPermission);
    }

    [Fact]
    public void SecondPermissionRequest_ReplacesPriorPendingAndMarksFirstEntryExpired()
    {
        // A second exec-approval can arrive before the first is resolved
        // (the gateway is free to issue them in sequence). The reducer
        // must surface the newest request so the user is responding to
        // the live one — and mark the prior inline bubble as Expired so
        // the timeline never shows two live Allow/Deny prompts at once.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatPermissionRequestEvent("req-1", "shell.exec", "bash", "run script.sh"));

        Assert.Equal("req-1", state.PendingPermission!.RequestId);

        var updated = ChatTimelineReducer.Apply(
            state,
            new ChatPermissionRequestEvent("req-2", "shell.exec", "bash", "rm -rf /tmp/x"));

        Assert.NotNull(updated.PendingPermission);
        Assert.Equal("req-2", updated.PendingPermission!.RequestId);
        Assert.Equal("rm -rf /tmp/x", updated.PendingPermission.Detail);

        Assert.Equal(2, updated.Entries.Count);
        Assert.Equal(ChatPermissionDecision.Expired, updated.Entries[0].PermissionDecision);
        Assert.Equal("req-1", updated.Entries[0].PermissionRequestId);
        Assert.Equal(ChatPermissionDecision.Pending, updated.Entries[1].PermissionDecision);
        Assert.Equal("req-2", updated.Entries[1].PermissionRequestId);
    }

    // ── Status and system events ──

    [Fact]
    public void Status_AddsStatusEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatStatusEvent("Connected", ChatTone.Success));

        Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.Status, state.Entries[0].Kind);
        Assert.Equal("Connected", state.Entries[0].Text);
        Assert.Equal(ChatTone.Success, state.Entries[0].Tone);
    }

    [Fact]
    public void AddSystem_AddsStatusEntry()
    {
        var state = ChatTimelineReducer.AddSystem(ChatTimelineState.Initial(), "system note");

        Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.Status, state.Entries[0].Kind);
        Assert.Equal("system note", state.Entries[0].Text);
        Assert.Equal(ChatTone.Info, state.Entries[0].Tone);
    }

    [Fact]
    public void AddSystem_WithExplicitTone_UsesTone()
    {
        var state = ChatTimelineReducer.AddSystem(ChatTimelineState.Initial(), "warning!", ChatTone.Warning);

        Assert.Equal(ChatTone.Warning, state.Entries[0].Tone);
    }

    [Fact]
    public void Restored_AddsInfoStatusEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatRestoredEvent("History restored"));

        Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.Status, state.Entries[0].Kind);
        Assert.Equal("History restored", state.Entries[0].Text);
        Assert.Equal(ChatTone.Info, state.Entries[0].Tone);
    }

    [Fact]
    public void ModelChanged_AddsSuccessStatusEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatModelChangedEvent("gpt-4o"));

        Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.Status, state.Entries[0].Kind);
        Assert.Contains("gpt-4o", state.Entries[0].Text);
        Assert.Equal(ChatTone.Success, state.Entries[0].Tone);
    }

    // ── Raw events ──

    [Fact]
    public void Raw_WithText_AddsRawEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatRawEvent("unknown.event", "raw payload"));

        Assert.Single(state.Entries);
        Assert.Equal(ChatTimelineItemKind.Raw, state.Entries[0].Kind);
        Assert.Equal("raw payload", state.Entries[0].Text);
    }

    [Fact]
    public void Raw_WithNullText_IsNoOp()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatRawEvent("unknown.event", null));

        Assert.Empty(state.Entries);
    }

    [Fact]
    public void Raw_WithEmptyText_IsNoOp()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatRawEvent("unknown.event", ""));

        Assert.Empty(state.Entries);
    }

    // ── ContextChanged ──

    [Fact]
    public void ContextChanged_IsNoOp()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatContextChangedEvent("/home/user/project", "main"));

        Assert.Empty(state.Entries);
        Assert.False(state.TurnActive);
    }

    // ── Unknown event type ──

    [Fact]
    public void UnknownEvent_IsNoOp()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new UnknownTestEvent());

        Assert.Empty(state.Entries);
        Assert.False(state.TurnActive);
    }

    private sealed record UnknownTestEvent : ChatEvent;
}
