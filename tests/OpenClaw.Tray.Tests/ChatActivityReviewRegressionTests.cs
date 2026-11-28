using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

// Regression expectations come from the pinned shipped producer's distinct aggregate/headline rules.
public sealed class ChatActivityReviewRegressionTests
{
    [Fact]
    public void Headline_FiltersHiddenDistinctRecordsBeforeCallDedupWithoutChangingAggregate()
    {
        var visible = Read("""{"itemId":"visible","kind":"tool","phase":"start","title":"Inspect visible files","status":"running"}""");
        var hidden = Read("""{"itemId":"hidden","kind":"tool","phase":"start","title":"Internal bookkeeping","status":"running","hideFromChannelProgress":true}""");
        var merged = ChatToolActivityFacts.Merge(visible, hidden);
        var call = Tool("call") with { ToolActivityFacts = merged };
        Assert.Equal(0, ChatToolActivityPresentation.Summarize([call]).ToolCount);
        var headline = new ChatToolActivityHeadlineState().Update("scope", [call], "run", 0).Headline;
        Assert.Equal("Inspect visible files", headline?.Title);
        // Hiding the SAME item replaces it; it must not revive its prior visible snapshot.
        call = call with { ToolActivityFacts = ChatToolActivityFacts.Merge(visible,
            visible with { HideFromChannelProgress = true }) };
        Assert.Null(new ChatToolActivityHeadlineState().Update("scope", [call], "run", 0).Headline);
    }

    [Fact]
    public void Headline_CurrentRunLegacyFallbackIsNotDisabledByAnEarlierCanonicalRun()
    {
        var older = Tool("old") with { ToolRunId = "old-run", ToolResult = ChatToolCallStatus.Success,
            ToolActivityFacts = new(Title: "Older canonical step", Status: "completed", ItemId: "old-item") };
        var current = Tool("current");
        var selected = new ChatToolActivityHeadlineState().Update("scope", [older, current], "run", 0,
            _ => "Localized current command");
        Assert.Equal("Localized current command", selected.Headline?.Title);
        var canonical = current with { Id = "canonical", ToolCallId = "canonical", ToolActivityFacts =
            new(Title: "Current canonical step", Status: "running", ItemId: "current-item") };
        Assert.Equal("canonical", new ChatToolActivityHeadlineState().Update("scope", [canonical, current], "run", 0).Headline?.Tool.Id);
    }

    [Theory]
    [InlineData(null, "Read")]
    [InlineData("running", "")]
    public void Headline_GenericCanonicalTitleIsSuppressedOnlyWhenStatusIsPresent(string? status, string expected)
    {
        var call = Tool("call") with { ToolName = "read", ToolActivityFacts = new(Title: "Read", Status: status, ItemId: "item") };
        Assert.Equal(expected, new ChatToolActivityHeadlineState().Update("scope", [call], "run", 0).Headline?.Title);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Summary_UnpairedRecordDoesNotInventUnknownOutcome(bool unpaired, int unknown)
    {
        var facts = Read("""{"itemId":"item","kind":"tool","phase":"end","title":"Read file","unpairedCall":UNPAIRED}"""
            .Replace("UNPAIRED", unpaired ? "true" : "false", StringComparison.Ordinal));
        var summary = ChatToolActivityPresentation.Summarize([Tool("call") with { ToolActivityFacts = facts }]);
        Assert.Equal(1, summary.ToolCount);
        Assert.Equal(unknown, summary.UnknownCount);
    }

    [Theory]
    [InlineData(ChatToolCallStatus.Error, "failed")]
    [InlineData(ChatToolCallStatus.Success, "completed")]
    public void Outcome_AbsenceOrSparseHintsDoNotMaskTheRealToolResult(ChatToolCallStatus result, string expected)
    {
        var call = Tool("call") with { ToolResult = result, ToolActivityFacts = new(CommandBearing: true) };
        Assert.Equal(expected, ChatToolActivityPresentation.Outcome(call));
        call = call with { ToolActivityFacts = new(HideFromChannelProgress: true, AuthoritativeAbsence: true) };
        Assert.Equal(expected, ChatToolActivityPresentation.Outcome(call));
        call = call with { ToolActivityFacts = new(Title: "Recorded unknown", ItemId: "canonical") };
        Assert.Null(ChatToolActivityPresentation.Outcome(call));
    }

    [Fact]
    public void SparseHint_DoesNotRemoveLegacySiblingOrDisableItsOwnRowTitle()
    {
        var hinted = Tool("hinted") with { ToolActivityFacts = new(CommandBearing: true) };
        var sibling = Tool("read") with { ToolName = "read" };
        var summary = ChatToolActivityPresentation.Summarize([hinted, sibling]);
        Assert.Equal(2, summary.ToolCount);
        Assert.Equal(0, summary.UnknownCount);
        Assert.Equal("Readable command", new ChatToolActivityHeadlineState().Update("scope", [hinted], "run", 0,
            _ => "Readable command").Headline?.Title);
    }

    [Theory]
    [InlineData(null, "run")]
    [InlineData("run", null)]
    public void HistoryMerge_MissingRunPreservesExactDiffButDoesNotMergeActivity(string? targetRun, string? sourceRun)
    {
        var saved = Tool("call") with { ToolName = "edit", ToolResult = ChatToolCallStatus.Success, ToolRunId = targetRun };
        var live = saved with { Id = "live", ToolRunId = sourceRun, ToolResultDiffFacts = new(9, 4),
            ToolActivityFacts = new(Title: "Live only", ItemId: "item") };
        var merged = ChatHistoryState.KeepToolResultFacts(saved, live);
        Assert.Equal(new ChatToolResultDiffFacts(9, 4), merged.ToolResultDiffFacts);
        Assert.Equal(new ChatToolDiffStat(9, 4), ChatToolDiffStats.Resolve(merged));
        Assert.Null(merged.ToolActivityFacts);
        Assert.Null(ChatHistoryState.KeepToolResultFacts(saved with { ToolRunId = "different" },
            live with { ToolRunId = "run" }).ToolResultDiffFacts);
        Assert.Null(ChatHistoryState.KeepToolResultFacts(saved, live with { ToolCallId = "other" }).ToolResultDiffFacts);
    }

    [Theory]
    [InlineData("agent:demo:dashboard:é+<&", "sifting", "tidepooling")]
    [InlineData("agent:demo:dashboard:日本語", "pinching", "cracking")]
    public void WorkingPhrase_SeedMatchesJavaScriptForNonAsciiAndHtmlCharacters(string session, string at30, string at75)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        var progress = new ChatRunProgressPresentation(start, null, "Working", true, false, RunId: "run");
        Assert.Equal(at30, ChatRunProgressProjection.WorkingPhraseKey(progress, session, start.AddSeconds(30)));
        Assert.Equal(at75, ChatRunProgressProjection.WorkingPhraseKey(progress, session, start.AddSeconds(75)));
    }

    [Fact]
    public void ResidualRegression_SparseRoutineChildDoesNotEraseItsCompletedWrapper()
    {
        var wrapper = Tool("wrapper") with { ToolResult = ChatToolCallStatus.Success };
        var poll = Tool("poll") with
        {
            ToolName = "process", ToolResult = ChatToolCallStatus.Success,
            ToolParentCallId = "wrapper", ToolActivityFacts = new(CommandBearing: false),
            ToolArgs = new System.Text.Json.Nodes.JsonObject { ["action"] = "poll" },
        };
        var sibling = Tool("read") with { ToolName = "read", ToolResult = ChatToolCallStatus.Success };
        var summary = ChatToolActivityPresentation.Summarize([wrapper, poll, sibling]);
        Assert.Equal(2, summary.ToolCount);
        Assert.Equal(1, summary.Counts.Single(count => count.Category == ChatToolActivityCategory.Command).Count);
        Assert.Equal(1, summary.Counts.Single(count => count.Category == ChatToolActivityCategory.Read).Count);
        var nonroutine = poll with { ToolName = "read", ToolArgs = null };
        Assert.Equal(2, ChatToolActivityPresentation.Summarize([wrapper, nonroutine, sibling]).ToolCount);
        var hidden = nonroutine with { ToolActivityFacts = new(HideFromChannelProgress: true) };
        Assert.Equal(2, ChatToolActivityPresentation.Summarize([wrapper, hidden, sibling]).ToolCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidualRegression_SparseCompletionNeverMasksIdentityMatchedActualFailure(bool errorFirst)
    {
        ChatEvent Map(string stream, string json) => ChatEventMapper.Map(new AgentEventInfo
        {
            Stream = stream, RunId = "run", Data = JsonSerializer.SerializeToElement(JsonDocument.Parse(json).RootElement),
        }).Event!;
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(),
            new ChatToolStartEvent("Run tests", "exec", ToolCallId: "call", RunId: "run"));
        var completed = Map("item", """{"kind":"tool","phase":"end","toolCallId":"call","itemId":"call","name":"exec","status":"completed"}""");
        var error = Map("tool", """{"phase":"error","toolCallId":"call","name":"exec","error":"Tests failed"}""");
        foreach (var evt in errorFirst ? new[] { error, completed } : new[] { completed, error })
            state = ChatTimelineReducer.Apply(state, evt);
        var call = Assert.Single(state.Entries);
        Assert.Equal(ChatToolCallStatus.Error, call.ToolResult);
        Assert.Equal("failed", ChatToolActivityPresentation.Outcome(call));
        Assert.Equal(1, ChatToolActivityPresentation.Summarize(state.Entries).FailedCount);
        var prior = Tool("prior");
        var headline = new ChatToolActivityHeadlineState();
        headline.Update("scope", [prior], "run", 0, _ => "Prior tool");
        Assert.Equal(call.Id, headline.Update("scope", [prior, call], "run", 100, entry => entry.Text).Headline?.Tool.Id);
        Assert.Null(ChatToolActivityPresentation.Outcome(call with
            { ToolActivityFacts = new(ItemId: "canonical", Title: "Unknown canonical result") }));
        Assert.Equal("blocked", ChatToolActivityPresentation.Outcome(call with
            { ToolResult = ChatToolCallStatus.Success, ToolActivityFacts = new(Status: "blocked") }));
    }

    [Fact]
    public void FinalRegression_SparseBlockedItemKeepsBlockedDisplayDespiteErrorStorage()
    {
        using var data = JsonDocument.Parse("""{"kind":"tool","phase":"end","itemId":"call","toolCallId":"call","name":"exec","status":"blocked"}""");
        var mapped = ChatEventMapper.Map(new AgentEventInfo
        {
            Stream = "item", RunId = "run", Data = data.RootElement.Clone(),
        }).Event!;
        var state = ChatTimelineReducer.Apply(ChatTimelineState.Initial(),
            new ChatToolStartEvent("Blocked command", "exec", ToolCallId: "call", RunId: "run"));
        state = ChatTimelineReducer.Apply(state, mapped);
        var call = Assert.Single(state.Entries);
        Assert.Equal(ChatToolCallStatus.Error, call.ToolResult);
        Assert.Equal("blocked", ChatToolActivityPresentation.Outcome(call));
        Assert.Equal(1, ChatToolActivityPresentation.Summarize(state.Entries).BlockedCount);
        Assert.Equal(0, ChatToolActivityPresentation.Summarize(state.Entries).FailedCount);
        Assert.Equal("failed", ChatToolActivityPresentation.Outcome(call with
            { ToolActivityFacts = call.ToolActivityFacts! with { Status = "completed" } }));
    }

    private static ChatTimelineItem Tool(string call) => new(call, ChatTimelineItemKind.ToolCall, call,
        ToolName: "exec", ToolResult: ChatToolCallStatus.InProgress, ToolCallId: call, ToolRunId: "run");

    private static ChatToolActivityFacts Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ChatToolActivityFacts.Read(doc.RootElement, requireCanonical: true)!;
    }
}
