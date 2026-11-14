using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolDiffStatsTests
{
    private static ChatTimelineItem Tool(string name, string argsJson,
        ChatToolCallStatus? status = ChatToolCallStatus.Success, ChatToolResultDiffFacts? facts = null,
        ChatToolDiffStat? live = null)
    {
        using var args = JsonDocument.Parse(argsJson);
        return new("t", ChatTimelineItemKind.ToolCall, "", ToolName: name, ToolResult: status,
            ToolInputDetails: NativeToolProjector.ProjectInputDetails(args.RootElement, name),
            ToolResultDiffFacts: facts, ToolLiveDiff: live);
    }

    [Theory]
    [InlineData("""{"path":"a.cs","oldText":"a\nb\nc","newText":"a\nB\nc\nd"}""", 2, 1)]
    [InlineData("""{"path":"a.cs","edits":[{"old_string":"x","new_string":"y"},{"oldText":"keep\n","newText":"keep\nmore\n"}]}""", 2, 1)]
    [InlineData("""{"path":"a.cs","oldText":"same","newText":"same"}""", 0, 0)]
    [InlineData("""{"path":"a.cs","oldText":"gone\n","newText":""}""", 0, 1)]
    [InlineData("""{"path":"a.md","oldText":"intro\n***\nend…","newText":"intro\n***\nend…\nmore"}""", 1, 0)]
    public void Edit_CountsLinesFromOldAndNewTextPairs(string args, long added, long removed) =>
        Assert.Equal(new ChatToolDiffStat(added, removed), ChatToolDiffStats.Resolve(Tool("edit", args)));

    [Fact]
    public void Edit_DetailsDiffIsAuthoritativeEvenWhenItsCountsAreUnknown()
    {
        const string args = """{"path":"a.cs","oldText":"a","newText":"b"}""";
        Assert.Equal(new ChatToolDiffStat(5, 0),
            ChatToolDiffStats.Resolve(Tool("edit", args, facts: new(5, 0))));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit", args, facts: new(DiffTruncated: true))));
        Assert.Equal(new ChatToolDiffStat(1, 1), ChatToolDiffStats.Resolve(Tool("edit", args, facts: new())));
    }

    [Fact]
    public void TryParseDetailsDiff_ReadsNumberedLinesAndRejectsOtherFormats()
    {
        Assert.True(ChatToolResultDiffFacts.TryParseDetailsDiff(" 10 keep\n-11 old\n+11 new\n+12 add\n     ...\n", out var counts));
        Assert.Equal((2L, 1L), counts);
        Assert.True(ChatToolResultDiffFacts.TryParseDetailsDiff("+1 a\n...(truncated)...", out counts));
        Assert.Null(counts);
        Assert.False(ChatToolResultDiffFacts.TryParseDetailsDiff("@@ -1 +1 @@\n-a\n+b", out _));
        Assert.False(ChatToolResultDiffFacts.TryParseDetailsDiff(" 1 unchanged", out _));
    }

    [Fact]
    public void TryParseDetailsDiff_SkipMarkersCountTowardTheWebRowBound()
    {
        // 300 numbered rows plus 101 skip markers exceed the web's 400-row bound, so the counts are unknown.
        var rows = Enumerable.Range(1, 300).Select(line => $"+{line} x").ToList();
        rows.InsertRange(1, Enumerable.Repeat("   ...", 101));
        Assert.True(ChatToolResultDiffFacts.TryParseDetailsDiff(string.Join('\n', rows), out var counts));
        Assert.Null(counts);
        rows.RemoveRange(1, 2);
        Assert.True(ChatToolResultDiffFacts.TryParseDetailsDiff(string.Join('\n', rows), out counts));
        Assert.Equal((300L, 0L), counts);
    }

    [Fact]
    public void Patch_RawTextInputAndPrefixedToolNamesStillCount()
    {
        const string patch = "*** Begin Patch\n*** Update File: a.cs\n@@\n-old\n+new\n+more\n*** End Patch";
        var raw = Tool("apply_patch", JsonSerializer.Serialize(patch));
        Assert.Equal(new ChatToolDiffStat(2, 1), ChatToolDiffStats.Resolve(raw));
        Assert.Equal(new ChatToolDiffStat(2, 1), ChatToolDiffStats.Resolve(Tool("codex_apply_patch",
            JsonSerializer.Serialize(new { input = patch }))));
    }

    [Theory]
    [InlineData("one\u2028two")]
    [InlineData("one\u2029two")]
    public void UnicodeLineSeparatorsMakeTheInputInexact(string content)
    {
        var entry = Tool("write", JsonSerializer.Serialize(new { path = "a.txt", content }));
        Assert.True(entry.ToolInputDetails!.Lossy);
        Assert.Null(ChatToolDiffStats.Resolve(entry));
    }

    [Fact]
    public void Resolve_CachesPerInputAndRecomputesWhenTheOutcomeChanges()
    {
        var entry = Tool("edit", """{"path":"a.cs","oldText":"a","newText":"b\nc"}""");
        var first = ChatToolDiffStats.Resolve(entry);
        Assert.Same(first, ChatToolDiffStats.Resolve(entry with { Id = "rerender" }));
        Assert.Equal(new ChatToolDiffStat(9, 4), ChatToolDiffStats.Resolve(entry with { ToolResultDiffFacts = new(9, 4) }));
        Assert.Equal(new ChatToolDiffStat(3, 3), ChatToolDiffStats.Resolve(
            entry with { ToolResult = ChatToolCallStatus.InProgress, ToolLiveDiff = new(3, 3) }));
        Assert.Null(ChatToolDiffStats.Resolve(entry with { ToolResult = ChatToolCallStatus.Error }));
        Assert.Equal(new ChatToolDiffStat(2, 1), ChatToolDiffStats.Resolve(entry));
    }

    [Fact]
    public void Write_CountsContentOnlyWhenTheFileIsKnownToBeNew()
    {
        const string args = """{"path":"new.md","content":"one\ntwo\nthree\n"}""";
        Assert.Null(ChatToolDiffStats.Resolve(Tool("write", args)));
        Assert.Equal(new ChatToolDiffStat(3, 0), ChatToolDiffStats.Resolve(Tool("write", args, facts: new(Created: true))));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("write", args, facts: new(Created: false))));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("write", args, facts: new())));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("write", args, facts: new(Created: true, Changed: false))));
        Assert.Equal(new ChatToolDiffStat(1, 0), ChatToolDiffStats.Resolve(Tool("str_replace_editor",
            """{"command":"create","path":"x","file_text":"only"}""")));
    }

    [Theory]
    [InlineData("""*** Begin Patch\n*** Update File: a.cs\n@@\n keep\n-old\n+new\n+more\n*** Add File: b.cs\n+one\n+two\n*** End Patch""", 4, 1)]
    [InlineData("""diff --git a/a.cs b/a.cs\nindex 1..2 100644\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n keep\n--- old dashes\n+++ new pluses\n""", 1, 1)]
    public void Patch_CountsHunkLines(string patch, long added, long removed) =>
        Assert.Equal(new ChatToolDiffStat(added, removed), ChatToolDiffStats.Resolve(Tool("apply_patch",
            JsonSerializer.Serialize(new { input = patch.Replace("\\n", "\n") }))));

    [Theory]
    [InlineData("*** Begin Patch\n*** Delete File: old.cs\n*** End Patch")]
    [InlineData("*** Begin Patch\n*** Update File: a.cs\n@@\n-old\n+new\n*** Delete File: old.cs\n*** End Patch")]
    public void Patch_UnknownDeletedContentsSuppressAggregateCounts(string patch)
    {
        var entry = Tool("apply_patch", JsonSerializer.Serialize(new { input = patch }));
        Assert.Null(ChatToolDiffStats.Resolve(entry));
        Assert.Equal(new ChatToolDiffStat(2, 7),
            ChatToolDiffStats.Resolve(entry with { ToolResultDiffFacts = new(2, 7) }));
        Assert.Null(ChatToolDiffStats.Resolve(entry with { ToolResultDiffFacts = new(DiffTruncated: true) }));
    }

    [Fact]
    public void Patch_StructuredDeletionWithoutContentsHasNoAggregateCount()
    {
        const string args = """
            {"changes":[{"path":"new.cs","kind":"add","stat":{"added":1,"removed":0}},
                        {"path":"old.cs","kind":"delete"}]}
            """;
        Assert.Null(ChatToolDiffStats.Resolve(Tool("apply_patch", args)));
    }

    [Fact]
    public void Patch_StructuredChangesPreferProducerCounts()
    {
        const string args = """
            {"changes":[{"path":"a.cs","kind":{"type":"update"},"stat":{"added":7,"removed":2},"diff":"@@ -1 +1 @@\n-x\n+y"},
                        {"path":"b.cs","kind":"add","diff":"one\ntwo"}]}
            """;
        Assert.Equal(new ChatToolDiffStat(9, 2), ChatToolDiffStats.Resolve(Tool("Apply Patch", args)));
    }

    [Fact]
    public void CutOrOversizedInputNeverYieldsAnApproximateCount()
    {
        var huge = new string('x', 20_000);
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit",
            JsonSerializer.Serialize(new { path = "a", oldText = huge, newText = huge + "y" }))));
        var lines = string.Join('\n', Enumerable.Range(0, 700));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit",
            JsonSerializer.Serialize(new { path = "a", oldText = lines, newText = lines + "\nz" }))));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit", """{"path":"a","edits":[]}""")));
    }

    [Theory]
    // Short secrets become "***", so different values look equal.
    [InlineData("api_key=old-example-value", "api_key=new-example-value")]
    // Secrets of 18+ characters keep their ends ("first6…last4"); equal ends hide a change.
    [InlineData("Authorization: Bearer abcdefOLDOLDOLDOLDOLDwxyz", "Authorization: Bearer abcdefNEWNEWNEWNEWNEWwxyz")]
    // A PEM key collapses to BEGIN / …redacted… / END, hiding its real line count.
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIBVAIBADANBgkqhkiG9w0BAQEFAASC\nAT4wggE6AgEAAkEAq7BFUpkGp3+LQmlQ\n-----END PRIVATE KEY-----",
        "-----BEGIN PRIVATE KEY-----\nMIIBVAIBADANBgkqhkiG9w0BAQEFAASC\nAT4wggE6AgEAAkEAq7BFUpkGp3+LQmlQ\nYx3Kr1Wv8T0xWq2QZQnVb6c9yRlW7Ehd\nGmkXmA6s6n0d3PfS4r5hJ8n2o1b7kQ9v\n-----END PRIVATE KEY-----")]
    public void RedactedInputNeverYieldsCounts(string oldText, string newText)
    {
        var entry = Tool("edit", JsonSerializer.Serialize(new { path = "notes.txt", oldText, newText }));
        Assert.True(entry.ToolInputDetails!.Lossy);
        Assert.Null(ChatToolDiffStats.Resolve(entry));
        // Exact result facts stay usable, because they do not depend on the masked input.
        Assert.Equal(new ChatToolDiffStat(2, 0), ChatToolDiffStats.Resolve(entry with { ToolResultDiffFacts = new(2, 0) }));
    }

    [Fact]
    public void Status_ShowsLiveCountsWhileRunningAndNothingAfterFailure()
    {
        const string args = """{"path":"a.cs","oldText":"a","newText":"b"}""";
        Assert.Equal(new ChatToolDiffStat(4, 3), ChatToolDiffStats.Resolve(
            Tool("edit", args, ChatToolCallStatus.InProgress, live: new(4, 3))));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit", args, ChatToolCallStatus.InProgress)));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit", args, ChatToolCallStatus.Error, live: new(4, 3))));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("edit", args, ChatToolCallStatus.Interrupted)));
        Assert.Null(ChatToolDiffStats.Resolve(Tool("read", """{"path":"a.cs"}""", live: new(1, 1))));
    }
}
