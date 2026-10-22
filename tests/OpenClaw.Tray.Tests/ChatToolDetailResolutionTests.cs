using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolDetailResolutionTests
{
    [Fact]
    public void HistoryRevision_ChangesOnCommitButNotLiveOutputOrStatusUpdates()
    {
        var state = new ChatConversationState(ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("session-a", HasHandshakeSnapshot: true);
        state.Load([new SessionInfo { Key = "session-a", IsMain = true }], context);
        var token = state.CaptureHistoryToken("session-a");
        var plan = new ChatHistoryRebuildPlan(
            "durable-session-a",
            ChatTimelineState.Initial() with { HistoryLoaded = true },
            new Dictionary<string, ChatEntryMetadata>(), 0);
        Assert.True(state.CommitHistory(token, plan, DateTimeOffset.UnixEpoch, authoritative: false));
        var started = state.ApplyEvent("session-a",
            new ChatToolStartEvent("command", "powershell", ToolCallId: "call-a", RunId: "run-a"),
            null, context);
        var original = Assert.Single(started.Timelines["session-a"].Entries);
        Assert.Equal(1, started.HistoryRevisions!["session-a"]);
        var request = new ChatToolDetailRequest(
            "session-a", 0, original.Id, "run-a", 1, "call-a", "powershell");

        state.ApplyEvent("session-a", new ChatMessageDeltaEvent("Live prose"), null, context);
        var updated = state.ApplyEvent("session-a",
            new ChatToolOutputEvent("Current output", "call-a", "run-a"), null, context);

        Assert.Equal(1, updated.HistoryRevisions!["session-a"]);
        Assert.True(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 0, updated.HistoryRevisions["session-a"],
            updated.Timelines["session-a"].Entries, out var entry));
        Assert.Equal("Current output", entry.ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.NotSame(original, entry);

        Assert.True(state.CommitHistory(token, plan, DateTimeOffset.UnixEpoch, authoritative: false));
        var reloaded = state.Snapshot(context);
        Assert.Equal(2, reloaded.HistoryRevisions!["session-a"]);
        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 0, reloaded.HistoryRevisions["session-a"],
            reloaded.Timelines["session-a"].Entries, out entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Resolve_AfterHistoryReplay_DoesNotBorrowReassignedLocalEntryId()
    {
        var request = new ChatToolDetailRequest("session-a", 2, "e1", null, 7);
        var replacement = Tool("e1", "new-call", "Different tool output");

        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 8, [replacement], out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Resolve_ToolCallIdWithinRevision_UsesCurrentOutputNotClickTimeCopy()
    {
        var request = new ChatToolDetailRequest(
            "session-a", 2, "e1", "run-a", 8, "original-call", "powershell");
        var different = Tool("e1", "different-call", "Unrelated output");
        var current = Tool("e4", "original-call", "Current streamed output");

        Assert.True(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 8, [different, current], out var entry));
        Assert.Same(current, entry);
        Assert.Equal("Current streamed output", entry.ToolOutput);
    }

    [Theory]
    [InlineData("history-tool-0")]
    [InlineData("gateway-looking-call-id")]
    public void Resolve_ToolCallIdWithoutProvenance_CannotAuthorizeCrossRevision(string toolCallId)
    {
        var request = new ChatToolDetailRequest(
            "session-a", 2, "e1", "run-a", 7, toolCallId, "powershell");

        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 8, [Tool("e1", toolCallId, "Potentially unrelated output")], out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Resolve_WithoutToolCallId_UsesLocalEntryOnlyWithinSameHistoryRevision()
    {
        var request = new ChatToolDetailRequest("session-a", 2, "e1", null, 7);
        var current = Tool("e1", null, "Current output");

        Assert.True(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 7, [current], out var entry));
        Assert.Same(current, entry);
    }

    [Fact]
    public void Resolve_DuplicateToolCallId_FailsClosed()
    {
        var request = new ChatToolDetailRequest(
            "session-a", 2, "e1", null, 7, "duplicate-call", "powershell");

        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 7,
            [Tool("e1", "duplicate-call", "First"), Tool("e2", "duplicate-call", "Second")],
            out var entry));
        Assert.Null(entry);
    }

    [Theory]
    [InlineData("session-b", 2, "powershell", "run-a")]
    [InlineData("session-a", 3, "powershell", "run-a")]
    [InlineData("session-a", 2, "read_file", "run-a")]
    [InlineData("session-a", 2, "powershell", "run-b")]
    public void Resolve_ChangedOwnerOrToolIdentity_FailsClosed(
        string session, long generation, string toolName, string runId)
    {
        var request = new ChatToolDetailRequest(
            "session-a", 2, "e1", "run-a", 7, "call-a", "powershell");
        var current = Tool("e1", "call-a", "Output") with { ToolName = toolName, ToolRunId = runId };

        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, session, generation, 7, [current], out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Resolve_MissingExplicitIdentity_DoesNotFallBackToLocalId()
    {
        var request = new ChatToolDetailRequest(
            "session-a", 2, "e1", null, 7, "gone-call", "powershell");

        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 7, [Tool("e1", "new-call", "Wrong output")], out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Resolve_NonToolEntryWithSameLocalId_IsUnavailable()
    {
        var request = new ChatToolDetailRequest("session-a", 2, "e1", null, 7);

        Assert.False(ChatToolActivityPresentation.TryResolveDetails(
            request, "session-a", 2, 7,
            [new("e1", ChatTimelineItemKind.Assistant, "Assistant prose")], out var entry));
        Assert.Null(entry);
    }

    private static ChatTimelineItem Tool(string entryId, string? toolCallId, string output) =>
        new(entryId, ChatTimelineItemKind.ToolCall, "command",
            ToolName: "powershell", ToolOutput: output, ToolCallId: toolCallId, ToolRunId: "run-a");
}
