using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatTranscriptSearchTests
{
    [Theory]
    [InlineData("output marker")]
    [InlineData("command marker")]
    [InlineData("options marker")]
    public void SearchesFullDisplayDetailsInsteadOfOnlyCompactPreview(string query)
    {
        var tool = new ChatTimelineItem("t", ChatTimelineItemKind.ToolCall, "compact",
            ToolName: "system.run", ToolOutput: new string('x', 5000) + " OUTPUT MARKER",
            ToolInputDetails: new("OPTIONS MARKER", "COMMAND MARKER"));
        Assert.Contains(query, ChatTranscriptSearch.Match(tool, query)!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ChatTranscriptSearch.Match(tool, "not present"));
    }

    [Fact]
    public void YieldSearchNeverExposesPrivateContinuationInput()
    {
        var tool = new ChatTimelineItem("y", ChatTimelineItemKind.ToolCall, "private continuation",
            ToolName: "sessions_yield", ToolOutput: "Public acknowledgement",
            ToolInputDetails: new("private continuation"));
        Assert.Null(ChatTranscriptSearch.Match(tool, "private"));
        Assert.Equal("Public acknowledgement", ChatTranscriptSearch.Match(tool, "public"));
    }

    [Fact]
    public void SnippetIsLiteralAndBoundedAroundTheMatch()
    {
        var entry = new ChatTimelineItem("m", ChatTimelineItemKind.Assistant,
            new string('a', 1000) + "<needle>" + new string('b', 1000));
        var snippet = ChatTranscriptSearch.Match(entry, " needle ")!;
        Assert.Contains("<needle>", snippet);
        Assert.StartsWith("...", snippet);
        Assert.EndsWith("...", snippet);
        Assert.InRange(snippet.Length, 6, 175);
    }
}
