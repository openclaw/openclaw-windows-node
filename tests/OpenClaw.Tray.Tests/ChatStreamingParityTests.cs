using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatStreamingParityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(100);

    [Fact]
    public void RunUsage_AcceptsCorrectionsAndLateFinalCounts_WithoutReopeningOrResettingClock()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent("", "r"), Start);
        state = ChatTimelineReducer.Apply(state, Assert.IsType<ChatRunUsageEvent>(
            ChatEventMapper.Map(Event("usage", """{"outputTokens":120}""", 8)).Event));
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 115, 9));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent(), Start.AddSeconds(5));
        state = ChatTimelineReducer.Apply(state, new ChatToolReplayResetEvent());
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 150, 7));
        Assert.Equal(115, state.RunUsage!.OutputTokens);
        Assert.Null(ChatRunProgressProjection.Project(state));
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 125, 10));
        Assert.Equal(125, state.RunUsage!.OutputTokens);
        Assert.Null(ChatRunProgressProjection.Project(state));
        Assert.Equal(Start, state.TurnStartedAt);
        Assert.Equal(Start.AddSeconds(5), state.TurnEndedAt);
        Assert.False(state.TurnActive);
        Assert.Empty(state.Entries);
    }

    [Fact]
    public void RunUsage_CannotCarryOverToLocalOrRemoteTurns()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent("", "r"), Start);
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 120, 8));
        state = ChatTimelineReducer.AddLocalUser(state, "next", "new-turn", Start.AddSeconds(5));
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 200, 9));
        Assert.Null(ChatRunProgressProjection.Project(state)!.OutputTokens);
        state = ChatTimelineReducer.Apply(state, new ChatThinkingEvent("", "new-run"));
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("new-run", 0, 1));
        Assert.Equal(0, ChatRunProgressProjection.Project(state)!.OutputTokens);
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("foreign", 999, 10));
        Assert.Equal(0, ChatRunProgressProjection.Project(state)!.OutputTokens);
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
        state = ChatTimelineReducer.Apply(state, new ChatThinkingEvent("", "remote-run"));
        Assert.Null(ChatRunProgressProjection.Project(state)!.OutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryMerge_PreservesRunUsageEvenBeforeFirstTranscriptEntry(bool hasEntry)
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent("", "r"), Start);
        if (hasEntry)
            state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Working on it"));
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 120, 8));
        var plan = new ChatHistoryRebuildPlan(null, ChatTimelineState.Initial() with { HistoryLoaded = true },
            new Dictionary<string, ChatEntryMetadata>(), 0);
        var (merged, _) = ChatHistoryState.MergeWithLiveEntries(plan, state,
            new Dictionary<string, ChatEntryMetadata>(), Start, authoritative: true);
        Assert.Equal(state.RunUsage, merged.RunUsage);
        Assert.True(merged.TurnActive);
        Assert.Equal(Start, merged.TurnStartedAt);
        merged = ChatTimelineReducer.Apply(merged, new ChatRunUsageEvent("r", 99, 7));
        Assert.Equal(120, merged.RunUsage!.OutputTokens);
    }

    [Theory]
    [InlineData("""{"outputTokens":-1}""")]
    [InlineData("""{"outputTokens":1.5}""")]
    [InlineData("""{"outputTokens":"123"}""")]
    [InlineData("""{"outputTokens":null}""")]
    [InlineData("""{"inputTokens":99}""")]
    public void RunUsage_DoesNotInventCountsFromMalformedOrSessionTotals(string data)
    {
        Assert.Null(ChatEventMapper.Map(Event("usage", data)).Event);
    }

    [Fact]
    public void Progress_SurvivesAssistantPreambleAndToolWork_WithoutResettingClock()
    {
        var state = ChatTimelineReducer.AddLocalUser(ChatTimelineState.Initial(), "hello", "nonce", Start);
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("I will investigate"), Start.AddSeconds(3));
        Assert.True(ChatRunProgressProjection.Project(state)!.Active);
        state = ChatTimelineReducer.Apply(state, new ChatToolStartEvent("search", "search", ToolCallId: "tool"), Start.AddSeconds(5));
        var progress = ChatRunProgressProjection.Project(state)!;
        Assert.Equal(Start, progress.StartedAt);
        Assert.Equal("Running search", progress.Label);
        Assert.Equal("1m 5s", ChatRunProgressProjection.Elapsed(progress.StartedAt, Start.AddSeconds(65)));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent(), Start.AddSeconds(70));
        Assert.Null(ChatRunProgressProjection.Project(state));
        Assert.False(state.TurnActive);
        Assert.Equal(Start.AddSeconds(70), state.TurnEndedAt);
    }

    [Theory]
    [InlineData("lifecycle", """{"phase":"end"}""")]
    [InlineData("lifecycle", """{"phase":"error","message":"Run failed"}""")]
    [InlineData("job", """{"state":"done"}""")]
    public void TerminalEvents_DismissProgressWithoutDiscardingRunMetadata(string stream, string data)
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent("", "r"), Start);
        state = ChatTimelineReducer.Apply(state, new ChatRunUsageEvent("r", 120, 1));
        Assert.NotNull(ChatRunProgressProjection.Project(state));
        var terminal = ChatEventMapper.Map(Event(stream, data, 2)).Event;
        Assert.NotNull(terminal);
        state = ChatTimelineReducer.Apply(state, terminal, Start.AddSeconds(2));
        Assert.Null(ChatRunProgressProjection.Project(state));
        Assert.False(state.TurnActive);
        Assert.Equal(Start, state.TurnStartedAt);
        Assert.Equal(Start.AddSeconds(2), state.TurnEndedAt);
        Assert.Equal(120, state.RunUsage!.OutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingInput_KeepsProgressVisibleAfterTurnEndUntilResolved(bool awaitingQuestion)
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent("", "r"), Start);
        if (!awaitingQuestion)
            state = ChatTimelineReducer.Apply(state,
                new ChatPermissionRequestEvent("approval", "exec", "shell", "Allow the command?"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent(), Start.AddSeconds(2));
        var progress = ChatRunProgressProjection.Project(state, awaitingQuestion);
        Assert.NotNull(progress);
        Assert.True(progress.Waiting);
        Assert.False(progress.Active);
        Assert.Equal(awaitingQuestion ? "Waiting for your answer" : "Waiting for approval", progress.Label);
        Assert.Equal(Start, progress.StartedAt);
        Assert.Equal(Start.AddSeconds(2), progress.EndedAt);
        Assert.Null(ChatRunProgressProjection.Project(state with { PendingPermission = null }));
    }

    [Fact]
    public void HistoryReplay_DoesNotInventRunDuration()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatMessageEvent("history"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
        Assert.Null(ChatRunProgressProjection.Project(state));
    }

    [Fact]
    public void PendingQuestion_ReplacesWorkingLabelWithoutDiscardingRunClock()
    {
        var state = ChatTimelineReducer.AddLocalUser(ChatTimelineState.Initial(), "hello", "nonce", Start);
        var progress = ChatRunProgressProjection.Project(state, awaitingQuestion: true)!;
        Assert.True(progress.Waiting);
        Assert.Equal("Waiting for your answer", progress.Label);
        Assert.Equal(Start, progress.StartedAt);
    }

    [Fact]
    public void ToolPartialResult_RemainsRunning_AndStaleUpdatesCannotOverwriteFinal()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(),
            new ChatToolStartEvent("Read", "read", ToolCallId: "t", RunId: "r"), Start);
        var mapping = ChatEventMapper.Map(Event("tool", """{"phase":"update","toolCallId":"t","partialResult":{"content":[{"type":"text","text":"first chunk"}]}}""", 2));
        state = ChatTimelineReducer.Apply(state, Assert.IsType<ChatToolProgressEvent>(mapping.Event));
        Assert.Equal(ChatToolCallStatus.InProgress, state.Entries[0].ToolResult);
        Assert.Equal("first chunk", state.Entries[0].ToolOutput);
        state = ChatTimelineReducer.Apply(state, new ChatToolProgressEvent("", "t", "r", 3));
        Assert.Equal("first chunk", state.Entries[0].ToolOutput);
        state = ChatTimelineReducer.Apply(state, new ChatToolProgressEvent("older", "t", "r", 1));
        Assert.Equal("first chunk", state.Entries[0].ToolOutput);
        state = ChatTimelineReducer.Apply(state, new ChatToolOutputEvent("final", "t", "r"));
        state = ChatTimelineReducer.Apply(state, new ChatToolProgressEvent("late", "t", "r", 3));
        Assert.Equal("final", state.Entries[0].ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, state.Entries[0].ToolResult);
    }

    [Fact]
    public void PartialResult_IsRunScoped()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(),
            new ChatToolStartEvent("Read", "read", ToolCallId: "t", RunId: "r"));
        state = ChatTimelineReducer.Apply(state, new ChatToolProgressEvent("wrong run", "t", "other", 2));
        Assert.Null(state.Entries[0].ToolOutput);
    }

    [Theory]
    [InlineData("compaction", """{"phase":"start"}""", "Compacting context")]
    [InlineData("compaction", """{"phase":"end","willRetry":true}""", "Retrying after context compaction")]
    [InlineData("run_status", """{"phase":"retrying"}""", "Retrying the model request")]
    [InlineData("lifecycle", """{"phase":"waiting-approval"}""", "Waiting for approval")]
    [InlineData("lifecycle", """{"phase":"approval-resolved"}""", "Working")]
    public void StatusStreams_AreVisibleWithoutTerminatingTheTurn(string stream, string data, string expected)
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent(""), Start);
        var evt = Assert.IsType<ChatProgressEvent>(ChatEventMapper.Map(Event(stream, data)).Event);
        state = ChatTimelineReducer.Apply(state, evt, Start.AddSeconds(1));
        Assert.True(state.TurnActive);
        Assert.Equal(expected, ChatRunProgressProjection.Project(state)!.Label);
    }

    [Fact]
    public void FallbackAndRecoveryNotices_UseNativeResources_AndPreserveUnknownGatewayNotices()
    {
        var resources = new Dictionary<string, string>
        {
            ["Chat_Status_PrimaryRestored"] = "Restored (localized)",
            ["Chat_Status_FallbackSwitching"] = "Switching (localized)",
            ["Chat_Status_FallbackModel"] = "Fallback (localized): {0}",
            ["Chat_Status_StreamIncomplete"] = "Reload (localized)",
        };
        string Text(string stream, string data) => Assert.IsType<ChatStatusEvent>(
            ChatEventMapper.Map(Event(stream, data), key => resources[key]).Event).Text;
        Assert.Equal("Restored (localized)", Text("fallback", """{"phase":"fallback_cleared"}"""));
        Assert.Equal("Restored (localized)", Text("lifecycle", """{"phase":"fallback_cleared"}"""));
        Assert.Equal("Switching (localized)", Text("fallback", "{}"));
        Assert.Equal("Fallback (localized): provider/model", Text("fallback", """{"activeModel":"provider/model"}"""));
        Assert.Equal("Reload (localized)", Text("notice", """{"reason":"chat_stream_incomplete","message":"English fallback"}"""));
        Assert.Equal("Gateway message", Text("notice", """{"message":"Gateway message"}"""));
        Assert.Equal("Using fallback model: provider/model", Assert.IsType<ChatStatusEvent>(
            ChatEventMapper.Map(Event("fallback", """{"toModel":"provider/model"}"""), key => key).Event).Text);
    }

    [Theory]
    [InlineData(9, "Chat_Run_ElapsedSeconds", "9 sec")]
    [InlineData(65, "Chat_Run_ElapsedMinutes", "1 min 5 sec")]
    [InlineData(3661, "Chat_Run_ElapsedHours", "1 hr 1 min 1 sec")]
    public void Elapsed_UsesLocalizedUnits(int seconds, string expectedKey, string expected)
    {
        string Lookup(string key)
        {
            Assert.Equal(expectedKey, key);
            return key switch
            {
                "Chat_Run_ElapsedHours" => "{0} hr {1} min {2} sec",
                "Chat_Run_ElapsedMinutes" => "{1} min {2} sec",
                _ => "{2} sec",
            };
        }
        Assert.Equal(expected, ChatRunProgressProjection.Elapsed(Start, Start.AddSeconds(seconds), Lookup));
    }

    [Fact]
    public void ClockSkew_ClampsNegativeElapsedTime()
    {
        Assert.Equal("0s", ChatRunProgressProjection.Elapsed(Start, Start.AddSeconds(-10)));
        Assert.Equal("1h 1m 1s", ChatRunProgressProjection.Elapsed(Start, Start.AddSeconds(3661)));
    }

    private static AgentEventInfo Event(string stream, string data, int seq = 1) =>
        new() { SessionKey = "session", RunId = "r", Seq = seq, Stream = stream, Ts = 100000,
            Data = JsonDocument.Parse(data).RootElement.Clone() };
}
