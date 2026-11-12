using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatStreamingParityTests
{
    [Fact]
    public void InputPreparation_UpdatesOneCorrelatedRowAndStopsAtTerminal()
    {
        ChatEvent Map(string data, int seq) => ChatEventMapper.Map(Event("tool", data, seq)).Event!;
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), Map("""
            {"phase":"input_delta","name":"edit","toolCallId":"c","diff":{"added":3,"removed":1},
             "inputDelta":"{\"password\":\"DO NOT RENDER"}
            """, 1));
        var id = Assert.Single(state.Entries).Id;
        Assert.True(state.Entries[0].ToolInputPreparing);
        Assert.Equal(new ChatToolDiffStat(3, 1), state.Entries[0].ToolLiveDiff);
        Assert.DoesNotContain("DO NOT RENDER", state.Entries[0].ToString());
        state = ChatTimelineReducer.Apply(state, Map("""
            {"phase":"review","name":"edit","toolCallId":"c",
             "review":{"id":"g","label":"Guardian","status":"in_progress"}}
            """, 2));
        Assert.True(state.Entries[0].ToolInputPreparing);
        state = ChatTimelineReducer.Apply(state, Map("""
            {"phase":"start","name":"edit","toolCallId":"c","args":{"path":"file.cs"}}
            """, 3));
        Assert.Equal(id, Assert.Single(state.Entries).Id);
        Assert.False(state.Entries[0].ToolInputPreparing);
        state = ChatTimelineReducer.Apply(state, Map("""
            {"phase":"result","toolCallId":"c","result":"Saved"}
            """, 4));
        Assert.Null(state.Entries[0].ToolLiveDiff);
        var terminal = state;
        state = ChatTimelineReducer.Apply(state, Map("""
            {"phase":"input_delta","name":"edit","toolCallId":"c","diff":{"added":99,"removed":99}}
            """, 5));
        Assert.Same(terminal, state);
    }

    [Theory]
    [InlineData("""{"added":"3","removed":1}""")]
    [InlineData("""{"added":-1,"removed":1}""")]
    [InlineData("""{"added":1.5,"removed":1}""")]
    public void InputPreparation_MalformedCountsDoNotThrowOrInventDiff(string diff)
    {
        var mapped = Assert.IsType<ChatToolStartEvent>(ChatEventMapper.Map(Event("tool",
            """{"phase":"input_delta","name":"edit","toolCallId":"c","diff":""" + diff + "}")).Event);
        Assert.Null(mapped.Feedback!.Diff);
    }

    [Fact]
    public void Review_SequenceAndRunFencesPreserveDenialWithoutGrantingPermission()
    {
        ChatEvent Review(string status, int seq) => ChatEventMapper.Map(Event("tool", $$$"""
            {"phase":"review","name":"exec","toolCallId":"c",
             "review":{"id":"g","label":"Guardian","status":"{{{status}}}","rationale":"Supplied reason"}}
            """, seq)).Event!;
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), Review("denied", 5));
        state = ChatTimelineReducer.Apply(state, Review("in_progress", 4));
        state = ChatTimelineReducer.Apply(state, Review("approved", 6));
        Assert.Equal("denied", Assert.Single(state.Entries[0].ToolReviews!).Status);
        Assert.Null(state.PendingPermission);
        Assert.Null(ChatEventMapper.Map(Event("tool", """
            {"phase":"review","toolCallId":"c","review":{"id":"g","label":"G","status":"inProgress"}}
            """)).Event);
        Assert.Null(ChatEventMapper.Map(Event("codex_app_server.guardian", """
            {"phase":"started","status":"inProgress","targetItemId":"c"}
            """)).Event);
        Assert.IsType<ChatStatusEvent>(ChatEventMapper.Map(Event("codex_app_server.guardian", """
            {"phase":"strict_review_required","message":"Review before continuing"}
            """)).Event);
    }

    [Fact]
    public void ExplicitChildWaitRemainsVisibleAfterParentStopsWorking()
    {
        var state = ChatTimelineState.Initial();
        var progress = ChatRunProgressProjection.Project(state, awaitingSubagents: true)!;
        Assert.True(progress.Waiting);
        Assert.False(progress.Active);
        Assert.Equal("Waiting for subagents", progress.Label);
        Assert.Null(ChatRunProgressProjection.Project(state));
    }

    [Theory]
    [InlineData("""{"delta":"not cumulative","itemId":"i"}""")]
    [InlineData("""{"text":"missing identity"}""")]
    [InlineData("""{"phase":"persisted","itemId":"i","messageId":"m"}""")]
    public void ThinkingStream_RejectsMissingSourceFacts(string data) =>
        Assert.Null(ChatEventMapper.Map(Event("thinking", data)).Event);

    [Fact]
    public void ThinkingStream_CumulativeAndEmptyTailDoNotResurrectEarlierOccurrence()
    {
        var state = ChatTimelineState.Initial();
        foreach (var data in new[] {
            """{"itemId":"one","text":"First"}""", """{"itemId":"one","text":"First complete"}""",
            """{"itemId":"two","text":"Second"}""", """{"itemId":"two","text":""}""" })
            state = ChatTimelineReducer.Apply(state, ChatEventMapper.Map(Event("thinking", data)).Event!);
        Assert.Equal(["First complete", ""], state.ReasoningStream!.Items.Select(item => item.Text));
        Assert.Empty(state.Entries);
        Assert.Single(ChatReasoningPresentation.Project(state.Entries, state.ReasoningStream, true, true));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
        Assert.Null(state.ReasoningStream);
    }

    [Theory]
    [InlineData("r", "m", false, true)]
    [InlineData("other", "m", false, false)]
    [InlineData("r", "other", false, false)]
    [InlineData("r", "m", true, false)]
    public void ThinkingReceipt_OnlyExactNativePublicationHandsOffPreview(
        string savedRun, string savedMessage, bool imported, bool persisted)
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatReasoningStreamEvent("r", "i", "Thought", 1));
        state = ChatTimelineReducer.Apply(state, new ChatReasoningReceiptEvent("r", "i", "r", "m"));
        state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("Answer",
            Presentation: new(RunId: savedRun, MessageId: savedMessage, SourceRole: "assistant", IsImported: imported)));
        state = ChatReasoningPresentation.Reconcile(state, null);
        Assert.Equal(persisted, state.ReasoningStream!.Items[0].Receipt!.Persisted);
        var on = ChatReasoningPresentation.Project(state.Entries, state.ReasoningStream, true, true);
        Assert.Equal(persisted ? 1 : 2, on.Count);
        var stream = ChatReasoningPresentation.Project(state.Entries, state.ReasoningStream, false, true);
        Assert.Equal(2, stream.Count);
        if (persisted)
        {
            Assert.Equal(["Thought", "Answer"], stream.Select(entry => entry.Text));
            Assert.Empty(ChatReasoningPresentation.Project([], state.ReasoningStream, true, true));
        }
        Assert.Single(ChatReasoningPresentation.Project(state.Entries, state.ReasoningStream, false, false));
    }

    [Fact]
    public void ThinkingReceipt_UnknownOccurrenceAndOpposingRunCannotAcknowledgeCurrentThought()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatReasoningStreamEvent("r", "i", "Thought", 1));
        state = ChatTimelineReducer.Apply(state, new ChatReasoningReceiptEvent("other", "i", "r", "m"));
        state = ChatTimelineReducer.Apply(state, new ChatReasoningReceiptEvent("r", "unknown", "r", "m"));
        Assert.Null(state.ReasoningStream!.Items[0].Receipt);
        state = ChatTimelineReducer.AddLocalUser(state, "Next", "nonce");
        Assert.Null(state.ReasoningStream);
    }

    [Fact]
    public void ThinkingHistoryPrepend_RemapsItsAnchorInsteadOfUsingARecycledLocalEntryId()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(),
            new ChatToolStartEvent("Current", "read", RunId: "r", ToolCallId: "current"));
        var original = Assert.Single(state.Entries);
        state = ChatTimelineReducer.Apply(state, new ChatReasoningStreamEvent("r", "i", "Thought", 1));
        state = ChatTimelineReducer.Apply(state, new ChatReasoningStreamEvent("r", "empty-tail", "", 2));
        var metadata = new ChatEntryMetadata(null, null, GatewayMessageId: "current-message");
        var rebuilt = ChatTimelineState.Initial() with
        {
            Entries = [original with { Text = "Older", ToolCallId = "older" }, original with { Id = "saved-current" }],
        };
        var plan = new ChatHistoryRebuildPlan(null, rebuilt,
            new Dictionary<string, ChatEntryMetadata> { ["saved-current"] = metadata }, 0);
        var (merged, _) = ChatHistoryState.MergeWithLiveEntries(plan, state,
            new Dictionary<string, ChatEntryMetadata> { [original.Id] = metadata }, Start, authoritative: true);
        Assert.Equal("saved-current", merged.ReasoningStream!.Items[0].AfterEntryId);
        var rows = ChatReasoningPresentation.Project(merged.Entries, merged.ReasoningStream, true, true);
        Assert.Equal(["Older", "Current", "Thought"], rows.Select(entry => entry.Text));
    }

    [Fact]
    public void HandoffReceipt_LiveMappingRetainsOnlyPublicFactsBeforeTruncation()
    {
        var start = ChatEventMapper.Map(Event("tool", """
            {"phase":"start","name":"sessions_yield","toolCallId":"yield",
             "args":{"waitFor":"message","context":"private continuation"}}
            """)).Event!;
        var result = ChatEventMapper.Map(Event("tool", """
            {"phase":"result","name":"sessions_yield","toolCallId":"yield",
             "result":{"details":{"status":"yielded","acknowledgment":"Waiting for results"}}}
            """)).Event!;
        var state = ChatTimelineReducer.Apply(ChatTimelineReducer.Apply(ChatTimelineState.Initial(), start), result);
        var entry = Assert.Single(state.Entries);
        Assert.Equal("Waiting for results", entry.ConfirmedHandoff!.Acknowledgment);
        Assert.Null(entry.ToolArgs);
        var projected = ChatToolActivityPresentation.Project(state.Entries, "agent:a:dashboard:d", 0);
        Assert.Equal("Waiting for results", Assert.Single(projected).Entry!.Text);
    }

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
        state = ChatTimelineReducer.Apply(state, new ChatReasoningStreamEvent("r", "i", "Current thought", 100));
        var plan = new ChatHistoryRebuildPlan(null, ChatTimelineState.Initial() with { HistoryLoaded = true },
            new Dictionary<string, ChatEntryMetadata>(), 0);
        var (merged, _) = ChatHistoryState.MergeWithLiveEntries(plan, state,
            new Dictionary<string, ChatEntryMetadata>(), Start, authoritative: true);
        Assert.Equal(state.RunUsage, merged.RunUsage);
        Assert.Equal(state.ReasoningStream!.RunId, merged.ReasoningStream!.RunId);
        Assert.Equal(state.ReasoningStream.Items, merged.ReasoningStream.Items);
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
    public void ToolStart_KeepsRecordedParentCallAndFillsItFromALaterStart()
    {
        var start = Assert.IsType<ChatToolStartEvent>(ChatEventMapper.Map(Event("tool",
            """{"phase":"start","name":"read","toolCallId":"child","parentToolCallId":"wrapper"}""")).Event);
        Assert.Equal("wrapper", start.ParentCallId);
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), start, Start);
        Assert.Equal("wrapper", Assert.Single(state.Entries).ToolParentCallId);

        state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(),
            new ChatToolStartEvent("Read", "read", ToolCallId: "child", RunId: "r"), Start);
        Assert.Null(state.Entries[0].ToolParentCallId);
        state = ChatTimelineReducer.Apply(state,
            new ChatToolStartEvent("Read", "read", ToolCallId: "child", RunId: "r", ParentCallId: "wrapper"), Start);
        Assert.Equal("wrapper", Assert.Single(state.Entries).ToolParentCallId);
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
    public void RetryStatus_CountsDownToSuppliedRetryAt_AndClearsWhenWorkResumes()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent(""), Start);
        var retryAt = Start.AddSeconds(30).ToUnixTimeMilliseconds();
        var evt = Assert.IsType<ChatProgressEvent>(ChatEventMapper.Map(
            Event("run_status", $$"""{"phase":"retrying","retryAt":{{retryAt}}}""")).Event);
        state = ChatTimelineReducer.Apply(state, evt, Start);
        var progress = ChatRunProgressProjection.Project(state)!;
        string Localize(string key, string fallback) => fallback;

        Assert.Equal(retryAt, progress.RetryAt?.ToUnixTimeMilliseconds());
        Assert.Equal("Retrying in 5s", ChatRunProgressProjection.RetryLabelText(progress, Start.AddSeconds(25), Localize));
        Assert.Equal(ChatRunProgressProjection.RetryLabel,
            ChatRunProgressProjection.RetryLabelText(progress, Start.AddSeconds(31), Localize));

        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("back"), Start.AddSeconds(31));
        Assert.Null(state.ProgressRetryAtMs);
        Assert.Null(ChatRunProgressProjection.Project(state)!.RetryAt);
    }

    [Theory]
    [InlineData("""{"phase":"retrying","retryAt":"soon"}""")]
    [InlineData("""{"phase":"retrying","retryAt":-1}""")]
    [InlineData("""{"phase":"retrying","retryAt":1e300}""")]
    public void RetryStatus_IgnoresInvalidRetryAt(string data) =>
        Assert.Null(Assert.IsType<ChatProgressEvent>(ChatEventMapper.Map(Event("run_status", data)).Event).RetryAtMs);

    [Fact]
    public void ThinkingStreamWithoutAnswer_LabelsRunThinking()
    {
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(), new ChatThinkingEvent("", "r"), Start);
        Assert.Equal("Working", ChatRunProgressProjection.Project(state)!.Label);
        state = ChatTimelineReducer.Apply(state,
            ChatEventMapper.Map(Event("thinking", """{"itemId":"i","text":"Planning"}""")).Event!, Start);

        Assert.Equal("Thinking", ChatRunProgressProjection.Project(state)!.Label);
        state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent("Answer"), Start);
        Assert.Equal("Responding", ChatRunProgressProjection.Project(state)!.Label);
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
