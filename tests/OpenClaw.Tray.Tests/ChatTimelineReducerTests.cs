using System.Linq;
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
    public void NewTurnFinalAssistant_DoesNotOverwriteFinalizedPreviousAssistant()
    {
        // Regression: a system.run approval-denied scenario produces a turn
        // shape like:
        //   1. user prompt
        //   2. assistant finalised reply ("I'll check by running ...")
        //   3. tool call + tool output
        //   4. status entries (approval submitted, denied, etc.)
        //   5. NEW turn: final assistant reply ("I can't run that.")
        //
        // OpenClawChatDataProvider always tags chat.message events with
        // ReconcilePrevious=true. Before the fix the reducer scanned
        // backwards past the tool / status entries, found the previous
        // turn's finalised assistant entry, and silently OVERWROTE its text
        // in place — making the new reply invisible and corrupting the
        // earlier bubble. After the fix, reconcile only collapses into a
        // still-streaming assistant entry, so a finalised assistant from a
        // completed turn is left alone and the new reply appears as its
        // own bubble.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Identify which version of Node, Python, and git are installed."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("I'll check by running a small command.", ReconcilePrevious: true));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("system.run", "system.run"));
        state = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent("denied: no matching rule"));

        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("I can't run that command — it was denied.", ReconcilePrevious: true));

        var assistantEntries = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistantEntries.Count);
        Assert.Equal("I'll check by running a small command.", assistantEntries[0].Text);
        Assert.Equal("I can't run that command — it was denied.", assistantEntries[1].Text);
        Assert.False(assistantEntries[1].IsStreaming);
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
    public void StaleStreamingPreview_DoesNotMergeAcrossUserBoundary()
    {
        // Regression for the cross-turn stale-preview class identified by
        // both reviewers: a streaming preview that never received its terminal
        // frame (network drop / aborted turn) must not be silently overwritten
        // by a NEXT turn's reconcile-flagged final once the user sends a new
        // prompt. The user message acts as a hard turn boundary that clears
        // both ActiveAssistantId and stale IsStreaming on prior entries.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("first prompt"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("partial preview"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        // No final ever arrived for turn 1 — preview is orphaned.
        // Now turn 2 begins with a fresh user message and final reply.
        state = ChatTimelineReducer.Apply(state, new ChatUserMessageEvent("second prompt"));
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("turn 2 final", ReconcilePrevious: true));

        var assistantEntries = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistantEntries.Count);
        Assert.Equal("partial preview", assistantEntries[0].Text);
        Assert.False(assistantEntries[0].IsStreaming);
        Assert.Equal("turn 2 final", assistantEntries[1].Text);
    }

    [Fact]
    public void UserMessage_AsTurnBoundary_PreventsCrossTurnOverwrite()
    {
        // Regression for the dropped-ChatTurnEndEvent edge case: if the
        // gateway omits chat.turn.end before the next user prompt, the
        // reducer must still treat ChatUserMessageEvent as a hard turn
        // boundary by clearing ActiveAssistantId. Otherwise the fast-path
        // overwrite branch in UpsertAssistant would silently replace the
        // previous turn's assistant reply in place.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("first"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("first reply"));
        // Note: NO ChatTurnEndEvent before the next user message.
        Assert.NotNull(state.ActiveAssistantId);

        state = ChatTimelineReducer.Apply(state, new ChatUserMessageEvent("second"));
        Assert.Null(state.ActiveAssistantId);

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("second reply"));

        var assistantEntries = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistantEntries.Count);
        Assert.Equal("first reply", assistantEntries[0].Text);
        Assert.Equal("second reply", assistantEntries[1].Text);
    }

    [Fact]
    public void AddLocalUser_AsTurnBoundary_PreventsCrossTurnOverwrite()
    {
        // Same regression as above but exercises the PRODUCTION typed-message
        // path (AddLocalUser) rather than gateway-injected ChatUserMessageEvent.
        // The tray's text-input box calls AddLocalUser; SSE echoes are usually
        // suppressed before they reach ApplyUserMessage. So the cross-turn
        // boundary cleanup MUST also live in AddLocalUser.
        var state = ChatTimelineReducer.AddLocalUser(
            ChatTimelineState.Initial(),
            "first",
            "nonce-1");
        state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("first reply"));
        // Note: NO ChatTurnEndEvent before the next typed message.
        Assert.NotNull(state.ActiveAssistantId);

        state = ChatTimelineReducer.AddLocalUser(state, "second", "nonce-2");
        Assert.Null(state.ActiveAssistantId);

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("second reply"));

        var assistantEntries = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistantEntries.Count);
        Assert.Equal("first reply", assistantEntries[0].Text);
        Assert.Equal("second reply", assistantEntries[1].Text);
    }

    [Fact]
    public void AddLocalUser_ClearsStaleStreamingPreviewAcrossTurns()
    {
        // Stale-streaming regression via the production typed-message path.
        // A streaming preview that never received its terminal frame must not
        // be silently overwritten when the user types their next prompt.
        var state = ChatTimelineReducer.AddLocalUser(
            ChatTimelineState.Initial(),
            "first prompt",
            "nonce-1");
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("partial preview"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        // No final ever arrived for turn 1 — preview is orphaned but still IsStreaming=true.
        state = ChatTimelineReducer.AddLocalUser(state, "second prompt", "nonce-2");
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("turn 2 final", ReconcilePrevious: true));

        var assistantEntries = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistantEntries.Count);
        Assert.Equal("partial preview", assistantEntries[0].Text);
        Assert.False(assistantEntries[0].IsStreaming);
        Assert.Equal("turn 2 final", assistantEntries[1].Text);
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

    [Fact]
    public void ReasoningEnd_ClearsActiveReasoningIdWithoutEndingTurn()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatReasoningEvent("first pass"));

        Assert.NotNull(state.ActiveReasoningId);
        Assert.True(state.TurnActive);

        var updated = ChatTimelineReducer.Apply(state, new ChatReasoningEndEvent());

        Assert.Null(updated.ActiveReasoningId);
        Assert.True(updated.TurnActive);
        // The original reasoning entry is preserved (not deleted).
        Assert.Single(updated.Entries);
        Assert.Equal("first pass", updated.Entries[0].Text);
    }

    [Fact]
    public void ReasoningEnd_NextReasoningChunkStartsFreshEntry()
    {
        var s1 = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatReasoningDeltaEvent("thinking about A"));
        var s2 = ChatTimelineReducer.Apply(s1, new ChatReasoningEndEvent());
        var s3 = ChatTimelineReducer.Apply(s2, new ChatReasoningDeltaEvent("thinking about B"));

        Assert.Equal(2, s3.Entries.Count);
        Assert.Equal("thinking about A", s3.Entries[0].Text);
        Assert.Equal("thinking about B", s3.Entries[1].Text);
        Assert.Equal(ChatTimelineItemKind.Reasoning, s3.Entries[0].Kind);
        Assert.Equal(ChatTimelineItemKind.Reasoning, s3.Entries[1].Kind);
    }

    [Fact]
    public void ReasoningEnd_NoActiveReasoning_IsNoOp()
    {
        var initial = ChatTimelineState.Initial();
        var updated = ChatTimelineReducer.Apply(initial, new ChatReasoningEndEvent());

        Assert.Equal(initial, updated);
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

    // ── Bubble-splitting across intra-turn tool calls ──

    [Fact]
    public void IntraTurnAssistantAfterTool_CreatesSeparateBubble()
    {
        // Scenario observed in real chats: within a single turn the model
        // produces a preamble, calls a tool, then continues speaking with a
        // distinct message. The reducer must keep the preamble bubble and
        // append a second assistant bubble after the tool chip — the HTML
        // dashboard renders this correctly; native used to collapse both
        // into a single bubble because UpsertAssistant's ActiveAssistantId
        // fast-path merged the second message into the first entry even
        // though a ToolCall had been appended between them.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("What files are in this directory?"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("Let me list them for you.", ReconcilePrevious: true));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("a.txt\nb.txt", ToolCallId: "tc-1"));

        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("Two files: a.txt and b.txt.", ReconcilePrevious: true));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal("Let me list them for you.", assistants[0].Text);
        Assert.Equal("Two files: a.txt and b.txt.", assistants[1].Text);
        // Ordering: [User, Assistant₁, ToolCall, Assistant₂].
        Assert.Equal(ChatTimelineItemKind.User, updated.Entries[0].Kind);
        Assert.Equal(ChatTimelineItemKind.Assistant, updated.Entries[1].Kind);
        Assert.Equal(ChatTimelineItemKind.ToolCall, updated.Entries[2].Kind);
        Assert.Equal(ChatTimelineItemKind.Assistant, updated.Entries[3].Kind);
    }

    [Fact]
    public void IntraTurnDeltaThenToolThenDelta_CreatesSeparateBubble()
    {
        // Post-tool deltas must NOT merge back into the pre-tool streaming
        // preview. The active assistant is no longer the timeline frontier
        // (a tool entry is), so the fast-path is bypassed; the scan-back
        // block is gated on `replace`, so delta events (replace=false)
        // fall through to create a fresh bubble — which is the desired
        // behavior matching the HTML dashboard.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Continue after the tool."));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Looking"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent(" up..."));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("lookup", "lookup", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("done", ToolCallId: "tc-1"));

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("The answer "));
        updated = ChatTimelineReducer.Apply(updated, new ChatMessageDeltaEvent("is 42."));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal("Looking up...", assistants[0].Text);
        Assert.Equal("The answer is 42.", assistants[1].Text);
        // Second delta after the new bubble must merge into it (fast-path
        // re-engages because the new bubble is now the frontier).
        Assert.True(assistants[1].IsStreaming);
    }

    [Fact]
    public void IntraTurnFinalAfterTool_CreatesNewBubbleNotReconcile()
    {
        // Contract (matches HTML dashboard): when a final ChatMessage with
        // REFINED text arrives after a tool round-trip, the reducer must
        // create a NEW bubble for the post-tool segment — NOT reconcile it
        // into the pre-tool streaming preview. The reconcile-into-streaming
        // path must not cross non-Assistant entries; otherwise the
        // text₁ → tool → text₂ split visible in the HTML UI silently
        // collapses into a single bubble carrying only the post-tool text
        // (the bug that motivated PR #676).
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("List the files."));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Looking"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("..."));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("a.txt\nb.txt", ToolCallId: "tc-1"));

        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("Found 2 files: a.txt and b.txt.",
                ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal("Looking...", assistants[0].Text);
        Assert.Equal("Found 2 files: a.txt and b.txt.", assistants[1].Text);
        Assert.False(assistants[1].IsStreaming);
    }

    [Fact]
    public void DeltaAfterFinal_WithRedundantTail_DoesNotDuplicate()
    {
        // Repro for the in-bubble tail-duplication bug:
        // chat.message (replace=true, cumulative) commits the full sentence
        // into the bubble, then an agent.message.delta (replace=false)
        // arrives carrying just the tail of that sentence. Without overlap-
        // aware append the fast-path's `existing.Text + text` would produce
        // "...mounted here. D: is mounted here.".
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Investigate."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(
                "The WSL path didn't resolve. Let me check how D: is mounted here.",
                ReconcilePrevious: true,
                IsStreaming: true));
        // Gateway re-emits the tail as a delta — must be deduplicated.
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageDeltaEvent(" D: is mounted here."));

        var assistant = Assert.Single(updated.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal(
            "The WSL path didn't resolve. Let me check how D: is mounted here.",
            assistant.Text);
    }

    [Fact]
    public void DeltaWithShortNaturalBoundary_DoesNotAccidentallyDedup()
    {
        // Negative guard: streaming token deltas often share 1-2 character
        // boundaries by coincidence ("Hel" + "lo " shares 'l'). The overlap
        // threshold must NOT collapse these — that would lose characters
        // from the bubble.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Continue."));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Hel"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("lo "));
        var updated = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("world"));

        var assistant = Assert.Single(updated.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal("Hello world", assistant.Text);
    }

    [Fact]
    public void ApplyTurnEnd_DemotesAbandonedStreamingAssistant()
    {
        // Regression guard: after the UpsertAssistant frontier-gate fix,
        // intra-turn delta→tool→delta produces an abandoned streaming bubble
        // (the pre-tool deltas). ApplyTurnEnd must demote IsStreaming on any
        // *non-last* assistant entry so the typing indicator does not persist
        // on the abandoned bubble. The LAST assistant entry retains
        // IsStreaming=true to allow a late ChatMessageEvent(reconcile=true)
        // to collapse into it (see FinalAssistant_UpdatesStreamingAssistantAfterTurnEnd).
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Do work."));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Hello"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("tool", "tool", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("done", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("World"));

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.False(assistants[0].IsStreaming);
        // The last assistant intentionally keeps IsStreaming=true so a late
        // final reconcile (post-TurnEnd) can still collapse into it.
        Assert.True(assistants[1].IsStreaming);
    }

    [Fact]
    public void IntraTurnFinalAfterThreeSegments_StripsAllPriorAssistantsFromCumulative()
    {
        // Regression for the live-app bug: when a turn produces 3+
        // assistant segments (A1 → Tool → A2 → Tool → A3), the gateway
        // emits cumulative-for-turn text containing A1+sep+A2+sep+A3.
        // The reducer must strip BOTH prior assistants (chronologically
        // A1 first, then A2) so the third bubble displays only A3.
        const string a1 = "Hmm — /mnt/d exists as a mount point but is empty.";
        const string a2 = "The WSL environment does not have Windows drives mounted.";
        const string a3 = "Here's what I've determined so far.";
        const string sep = "\n\n";

        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Investigate the repo."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls2", "ls2", ToolCallId: "tc-2"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-2"));
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2 + sep + a3,
                ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(3, assistants.Count);
        Assert.Equal(a1, assistants[0].Text);
        Assert.Equal(a2, assistants[1].Text);
        Assert.Equal(a3, assistants[2].Text);
    }

    [Fact]
    public void IntraTurnLateReconcile_AfterTurnEnd_DoesNotPrependPriorSegments()
    {
        // Regression for the live-app duplicate-bubble bug whose smoking
        // gun was a final UpsertA call with aid=null (cleared by
        // ApplyTurnEnd) that bypassed the fast-path strip and fell into
        // the scan-back reconcile. With reconcilePrevious=true and the
        // last assistant still IsStreaming=true (preserved by
        // ApplyTurnEnd's preserveIdx), shouldMerge=true and the raw
        // cumulative text (A1 + A2 + A3) was being written verbatim
        // into A3 — visible to users as A3 mutating, AFTER initially
        // rendering correctly, to repeat all prior segments.
        // The scan-back reconcile must now apply
        // StripCumulativeTurnPrefix on the merge target so the bubble
        // retains only its segment tail.
        const string a1 = "I'll check the mount layout first.";
        const string a2 = "The /mnt/d entry exists but is empty.";
        const string a3 = "Here's a summary of what I found about the workspace.";
        const string sep = "\n\n";

        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Investigate."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls2", "ls2", ToolCallId: "tc-2"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-2"));
        // A3 arrives as a streaming preview to model the live-app state
        // where the post-tool final hadn't yet flipped to IsStreaming=false.
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2 + sep + a3,
                ReconcilePrevious: true, IsStreaming: true));
        // Turn ends — aid is cleared, A3 stays IsStreaming=true because
        // it's the timeline tail (ApplyTurnEnd.preserveIdx).
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        Assert.Null(state.ActiveAssistantId);
        var preEnd = state.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(3, preEnd.Count);
        Assert.Equal(a3, preEnd[2].Text);
        Assert.True(preEnd[2].IsStreaming);
        var a3IdBeforeReconcile = preEnd[2].Id;

        // A late cumulative chat.message arrives with reconcilePrevious=true
        // (the bug-producing event in the live repro). Before the fix
        // the scan-back reconcile branch overwrote A3 with the raw
        // cumulative; after the fix it strips the A1+A2 prefix first.
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2 + sep + a3,
                ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(3, assistants.Count);
        Assert.Equal(a1, assistants[0].Text);
        Assert.Equal(a2, assistants[1].Text);
        Assert.Equal(a3, assistants[2].Text);
        Assert.False(assistants[2].IsStreaming);
        // Lock the test to the in-place scan-back-merge path: A3's id must
        // be unchanged, proving no new bubble was created. (If a future
        // refactor of ApplyTurnEnd.preserveIdx demotes A3.IsStreaming the
        // scan-back merge would fail shouldMerge and fall through to the
        // new-bubble path → strip-to-empty → no-op guard, which would still
        // produce 3 assistants with the right text but a *different* code
        // path than the in-place reconcile merge this test targets.
        // The id assertion makes the path explicit.)
        Assert.Equal(a3IdBeforeReconcile, assistants[2].Id);
    }

    [Fact]
    public void IntraTurnLateReconcile_RegressionCumulative_DoesNotBlankCandidate()
    {
        // Defensive regression: on a gateway regression / duplicate
        // cumulative frame that's fully consumed by OTHER priors in
        // the turn (i.e. carries no new content for the streaming
        // candidate), the scan-back reconcile merge path used to
        // overwrite the candidate's text with the empty string
        // returned by StripCumulativeTurnPrefix. The guard mirrors
        // the symmetric no-op on the new-bubble path and leaves the
        // candidate's text intact.
        const string a1 = "Investigating the workspace layout in detail now.";
        const string a2Tail = "Here's the partial finding so far for A2.";

        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Look at the repo."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-1"));
        // A2 arrives as a streaming preview carrying its OWN tail (the
        // strip already peeled A1 when it was created via the new-bubble
        // path). It is the timeline tail, so scan-back-reconcile is the
        // path under test.
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + "\n\n" + a2Tail, ReconcilePrevious: true, IsStreaming: true));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var preEnd = state.Entries.Where(e => e.Kind == ChatTimelineItemKind.Assistant).ToList();
        Assert.Equal(2, preEnd.Count);
        Assert.Equal(a2Tail, preEnd[1].Text);
        Assert.True(preEnd[1].IsStreaming);
        var a2Id = preEnd[1].Id;

        // Gateway re-emits a stale cumulative frame containing ONLY A1's
        // content (no A2 tail). Without the guard, strip(excludeIndex=A2)
        // would peel A1 fully → mergedText="" → A2.Text would be blanked.
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1, ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries.Where(e => e.Kind == ChatTimelineItemKind.Assistant).ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal(a1, assistants[0].Text);
        // The candidate must NOT have been blanked.
        Assert.Equal(a2Tail, assistants[1].Text);
        Assert.Equal(a2Id, assistants[1].Id);
    }

    [Fact]
    public void IntraTurnFinalResend_AfterTools_IsTreatedAsNoop()
    {
        // Regression for the live-app duplicate-bubble bug: after a
        // multi-segment turn rendered as
        //   [User, A1, Tool, ToolOut, A2, Tool, ToolOut, A3]
        // the gateway sometimes re-emits the FINAL cumulative
        // chat.message a second time (same text as last time). The
        // scan-back byte-equal merge is intentionally blocked by the
        // !hasInterveningEntries gate (Tool entries follow A3),
        // so before this fix UpsertAssistant fell through to the
        // new-bubble path and created a duplicate A3 — visible to users
        // as the last bubble repeating word-for-word right below itself.
        // StripCumulativeTurnPrefix now peels every prior in the turn
        // including the last assistant; an empty result signals a
        // duplicate resend and UpsertAssistant must leave the timeline
        // unchanged.
        const string a1 = "Hmm — /mnt/d exists as a mount point but is empty.";
        const string a2 = "The WSL environment does not have Windows drives mounted.";
        const string a3 = "Here's what I've determined about the workspace layout.";
        const string sep = "\n\n";

        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Investigate the repo."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls2", "ls2", ToolCallId: "tc-2"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-2"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2 + sep + a3,
                ReconcilePrevious: true, IsStreaming: false));
        // Now another tool runs and finishes — these append entries AFTER
        // A3, so any subsequent assistant event will be blocked from
        // merging into A3 by the !hasInterveningEntries gate.
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls3", "ls3", ToolCallId: "tc-3"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-3"));
        // The gateway re-emits the IDENTICAL final cumulative.
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(a1 + sep + a2 + sep + a3,
                ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(3, assistants.Count);
        Assert.Equal(a1, assistants[0].Text);
        Assert.Equal(a2, assistants[1].Text);
        Assert.Equal(a3, assistants[2].Text);
    }

    [Fact]
    public void ApplyTurnEnd_DemotesLastAssistantWhenToolFollowsIt()
    {
        // Regression for orphaned post-tool typing indicators: if a turn ends as
        // [Assistant(streaming), Tool] (no post-tool final arrived), the
        // last Assistant is NOT the timeline tail and the scan-back's
        // hasInterveningEntries gate will block any late-reconcile merge.
        // Preserving IsStreaming on that orphaned bubble would leave a
        // stranded typing indicator with no possible recovery.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Do work."));
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Working..."));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("tool", "tool", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("done", ToolCallId: "tc-1"));

        var updated = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var assistant = Assert.Single(updated.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.False(assistant.IsStreaming);
    }

    [Fact]
    public void IntraTurnFinalAfterTool_PreservesLegitimateIndentation()
    {
        // Regression for indentation-eating strip: post-strip TrimStart must
        // NOT eat legitimate leading spaces/tabs — only the boundary
        // newline introduced by cumulative framing. A code-block bubble
        // starting with 4-space indentation must keep its indentation.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Show me a code block."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(
                "First I'll explain the approach in detail here.",
                ReconcilePrevious: true,
                IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("tool", "tool", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("done", ToolCallId: "tc-1"));
        // Cumulative: prior text + boundary newline + indented code block.
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(
                "First I'll explain the approach in detail here.\n    indented line one\n    indented line two",
                ReconcilePrevious: true,
                IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal("    indented line one\n    indented line two", assistants[1].Text);
    }

    [Fact]
    public void IntraTurnFinalAfterTool_IdenticalShortReplies_DoNotCollapseAcrossTool()
    {
        // Regression for over-eager duplicate collapse: the byte-equal duplicate
        // safety net used to run unconditionally, which meant two
        // legitimately distinct assistant turns with identical short text
        // ("Done.", "OK.") would collapse back into one bubble across a
        // tool — reintroducing the symptom this PR fixes for the
        // exact-match case.
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("Run twice."));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("Done.", ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("tool", "tool", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("ok", ToolCallId: "tc-1"));
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent("Done.", ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal("Done.", assistants[0].Text);
        Assert.Equal("Done.", assistants[1].Text);
    }

    [Fact]
    public void IntraTurnFinalAfterTool_StripsCumulativePrefixFromPriorBubble()
    {
        // Regression: live ChatMessageEvent text is CUMULATIVE for the entire
        // turn (see OpenClawChatDataProvider.OnChatMessageReceived's comment
        // about block-streamed content). When a new bubble is created
        // post-tool due to the frontier-gate, naïvely persisting that
        // cumulative text would visibly duplicate the previous bubble's
        // content as a prefix on the second bubble. UpsertAssistant must
        // strip the prior assistant entry's text prefix (and the separating
        // whitespace the gateway inserts between blocks) so the new bubble
        // only shows its own segment.
        const string preamble = "Let me check the directory.";
        const string follow = "The directory is empty.";

        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("What's in the directory?"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(preamble, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-1"));

        // Gateway sends cumulative-for-turn text (preamble + separator + follow).
        var cumulative = preamble + "\n\n" + follow;
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(cumulative, ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal(preamble, assistants[0].Text);
        // The second bubble must contain ONLY the new segment, not the
        // duplicated preamble prefix.
        Assert.Equal(follow, assistants[1].Text);
    }

    [Fact]
    public void IntraTurnFinalAfterTool_StripsPrefixEvenWhenPriorBubbleMutatedByLateDelta()
    {
        // Regression: a late `agent.message.delta` append can mutate the
        // PRIOR assistant bubble's text AFTER we have transitioned to a new
        // bubble in the same turn. In that case the prior bubble's stored
        // text is no longer a clean prefix of the cumulative-for-turn text
        // sent by the gateway. UpsertAssistant must still strip the common
        // portion using longest-common-prefix so the new bubble does not
        // visibly duplicate the original prior content.
        const string originalPreamble = "Let me check the directory thoroughly first.";
        const string lateAppendTail = " extra duplicated tail content";
        const string follow = "The directory is empty.";

        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatUserMessageEvent("What's in the directory?"));
        state = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(originalPreamble, ReconcilePrevious: true, IsStreaming: false));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("ls", "ls", ToolCallId: "tc-1"));
        state = ChatTimelineReducer.Apply(state,
            new ChatToolOutputEvent("", ToolCallId: "tc-1"));

        // Simulate the production symptom: bubble1's stored text has been
        // mutated to have a divergent tail (e.g. via a late append that
        // landed on it before the new turn segment was created). We mutate
        // the immutable state directly to focus this test on the LCP
        // behavior of UpsertAssistant.
        var bubble1Index = state.Entries.FindIndex(e =>
            e.Kind == ChatTimelineItemKind.Assistant);
        Assert.True(bubble1Index >= 0);
        var bubble1 = state.Entries[bubble1Index];
        state = state with
        {
            Entries = state.Entries.SetItem(bubble1Index,
                bubble1 with { Text = originalPreamble + lateAppendTail })
        };

        // Gateway then sends cumulative-for-turn text. Note this does NOT
        // include the late-appended tail because that came from a different
        // event stream — the cumulative reflects the original turn shape.
        var cumulative = originalPreamble + "\n\n" + follow;
        var updated = ChatTimelineReducer.Apply(state,
            new ChatMessageEvent(cumulative, ReconcilePrevious: true, IsStreaming: false));

        var assistants = updated.Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        // First bubble keeps its mutated text (the late-append issue is
        // out of scope for this fix).
        Assert.Equal(originalPreamble + lateAppendTail, assistants[0].Text);
        // Second bubble must not visibly duplicate the original preamble.
        Assert.Equal(follow, assistants[1].Text);
    }

    private sealed record UnknownTestEvent : ChatEvent;
}
