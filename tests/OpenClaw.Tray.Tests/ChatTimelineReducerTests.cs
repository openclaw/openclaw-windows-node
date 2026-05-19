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
}
