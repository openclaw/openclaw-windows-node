using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolActivityHeadlineTests
{
    [Fact]
    public void Headline_CanonicalMetaPrecedesLegacyRowProjection()
    {
        var tool = Tool("read", "Read") with { ToolName = "read", ToolActivityFacts = new(Title: "Read", Meta: "file.cs", Status: "running", ItemId: "read-item") };
        Assert.Equal("file.cs", new ChatToolActivityHeadlineState().Update("scope", [tool], "run", 100,
            _ => "Read file.cs").Headline!.Title);
    }

    [Fact]
    public void Headline_HoldsFastStepsButRefreshesCompletionAndThenShowsNextStep()
    {
        var state = new ChatToolActivityHeadlineState();
        var first = Tool("first", "Inspect test projects");
        var second = Tool("second", "Run focused tests");
        Assert.Equal("Inspect test projects", state.Update("scope", [first], "run", 100).Headline!.Title);
        var completed = first with { ToolResult = ChatToolCallStatus.Success };
        var held = state.Update("scope", [completed, second], "run", 1100);
        Assert.Equal("first", held.Headline!.Tool.Id);
        Assert.False(held.Headline.Running);
        Assert.Equal(2000, held.RefreshAfterMs);
        Assert.Equal("second", state.Update("scope", [completed, second], "run", 3100).Headline!.Tool.Id);
    }

    [Fact]
    public void Headline_FailureAndNewScopeBypassDwellAndOtherRunsNeverLeak()
    {
        var state = new ChatToolActivityHeadlineState();
        var first = Tool("first", "Inspect projects");
        var failed = Tool("failed", "Run tests") with { ToolResult = ChatToolCallStatus.Error };
        state.Update("scope", [first], "run", 100);
        Assert.Equal("failed", state.Update("scope", [first, failed], "run", 200).Headline!.Tool.Id);
        var other = Tool("other", "Other task") with { ToolRunId = "other-run" };
        Assert.Equal("other", state.Update("new-scope", [first, other], "other-run", 300).Headline!.Tool.Id);
        Assert.Null(state.Update("new-scope", [first], "other-run", 400).Headline);
    }

    [Fact]
    public void Headline_CompletedCurrentRunStepRemainsUntilRunEnds()
    {
        var state = new ChatToolActivityHeadlineState();
        var completed = Tool("done", "Inspect projects") with { ToolResult = ChatToolCallStatus.Success };
        var headline = state.Update("scope", [completed], "run", 100).Headline;
        Assert.Equal("Inspect projects", headline!.Title);
        Assert.False(headline.Running);
        Assert.Null(state.Update("scope", [], null, 200).Headline);
    }

    [Fact]
    public void Headline_CanonicalMetaPrecedesTitleAndHiddenStepsDoNotReplaceVisibleStep()
    {
        var visible = Tool("visible", "Generic label") with
        {
            ToolActivityFacts = new(Title: "Generic label", Meta: "Inspect repository", Status: "running", ItemId: "visible-item"),
        };
        var hidden = Tool("hidden", "Bookkeeping") with { ToolActivityFacts = new(HideFromChannelProgress: true) };
        var state = new ChatToolActivityHeadlineState();
        Assert.Equal("Inspect repository", state.Update("scope", [visible, hidden], "run", 100).Headline!.Title);
    }

    [Fact]
    public void Headline_UsesCompleteLocalizedRowProjectionForUntitledLegacyOperations()
    {
        var tool = Tool("read", "read") with
        {
            ToolName = "read", ToolActivityFacts = null,
            ToolArgs = new System.Text.Json.Nodes.JsonObject { ["path"] = "src/file.cs" },
        };
        ChatToolRowView? observed = null;
        var state = new ChatToolActivityHeadlineState();
        var selected = state.Update("scope", [tool], "run", 100, entry =>
        {
            observed = ChatToolRowPresentation.Resolve(entry);
            return "Localized Read " + observed.Target;
        });
        Assert.Equal(ChatToolRowVerb.Read, observed!.Verb);
        Assert.Equal("Localized Read file.cs", selected.Headline!.Title);
    }

    [Fact]
    public void Headline_HidingTheHeldCallImmediatelyShowsTheNextVisibleCall()
    {
        var state = new ChatToolActivityHeadlineState();
        var first = Tool("first", "Inspect projects");
        state.Update("scope", [first], "run", 100);
        var hidden = first with { ToolActivityFacts = first.ToolActivityFacts! with { HideFromChannelProgress = true } };
        var result = state.Update("scope", [hidden, Tool("second", "Run tests")], "run", 200);
        Assert.Equal("second", result.Headline!.Tool.Id);
        Assert.Equal(0, result.RefreshAfterMs);
    }

    private static ChatTimelineItem Tool(string id, string title) => new(
        id, ChatTimelineItemKind.ToolCall, title, ToolName: "exec", ToolResult: ChatToolCallStatus.InProgress,
        ToolCallId: id, ToolRunId: "run", ToolActivityFacts: new(CommandBearing: true, Title: title, Status: "running", ItemId: id));
}
