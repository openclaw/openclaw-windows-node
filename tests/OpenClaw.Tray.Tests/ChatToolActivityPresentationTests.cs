using System.Text.Json.Nodes;
using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolActivityPresentationTests
{
    [Fact]
    public void Project_GroupsOnlyConsecutiveSpansOfAtLeastTwoTools()
    {
        var entries = new[]
        {
            Item("user", ChatTimelineItemKind.User),
            Tool("one", "powershell"),
            Item("assistant", ChatTimelineItemKind.Assistant),
            Tool("two", "read_file"),
            Tool("three", "write_file"),
            Item("reasoning", ChatTimelineItemKind.Reasoning),
            Tool("four", "web_fetch"),
            Item("status", ChatTimelineItemKind.Status),
            Tool("five", "grep"),
            Tool("six", "edit_file"),
            Item("permission", ChatTimelineItemKind.PermissionRequest),
        };

        var rows = ChatToolActivityPresentation.Project(entries, "session", 7);

        Assert.Equal(
            ["user", "one", "assistant", "two", "reasoning", "four", "status", "five", "permission"],
            rows.Select(row => row.Entry?.Id ?? row.Tools[0].Id));
        Assert.False(rows[1].IsActivityGroup);
        Assert.Equal(["two", "three"], rows[3].Tools.Select(static tool => tool.Id));
        Assert.Equal(["five", "six"], rows[7].Tools.Select(static tool => tool.Id));
    }

    [Fact]
    public void Project_PreservesChronologyAndHidesOnlyToolRows()
    {
        var entries = new[]
        {
            Item("user", ChatTimelineItemKind.User),
            Tool("tool-1", "powershell"),
            Tool("tool-2", "powershell"),
            Item("assistant", ChatTimelineItemKind.Assistant),
            Item("permission", ChatTimelineItemKind.PermissionRequest),
        };

        var rows = ChatToolActivityPresentation.Project(entries, "s", 1, showToolCalls: false);

        Assert.Equal(["user", "assistant", "permission"], rows.Select(row => row.Entry!.Id));
    }

    [Fact]
    public void ActivityKey_RemainsStableWhenAppendingAndChangesForGeneration()
    {
        var first = ChatToolActivityPresentation.Project(
            [Tool("a", "powershell"), Tool("b", "powershell")], "s", 4).Single();
        var appended = ChatToolActivityPresentation.Project(
            [Tool("a", "powershell"), Tool("b", "powershell"), Tool("c", "powershell")], "s", 4).Single();
        var newGeneration = ChatToolActivityPresentation.Project(
            [Tool("a", "powershell"), Tool("b", "powershell")], "s", 5).Single();
        var historyPrepended = ChatToolActivityPresentation.Project(
            [Item("history", ChatTimelineItemKind.Assistant), Tool("a", "powershell"), Tool("b", "powershell")],
            "s",
            4)[1];

        Assert.Equal(first.Key, appended.Key);
        Assert.Equal(first.Key, historyPrepended.Key);
        Assert.NotEqual(first.Key, newGeneration.Key);
    }

    [Fact]
    public void Project_KeepsRowKeyStableWhenStandaloneToolBecomesGroup()
    {
        var standalone = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell")],
            "session",
            9).Single();
        var grouped = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell"), Tool("second", "read_file")],
            "session",
            9).Single();

        Assert.False(standalone.IsActivityGroup);
        Assert.True(grouped.IsActivityGroup);
        Assert.Equal(standalone.Key, grouped.Key);
    }

    [Theory]
    [InlineData("powershell", ChatToolActivityCategory.Command)]
    [InlineData("system.run", ChatToolActivityCategory.Command)]
    [InlineData("functions.bash", ChatToolActivityCategory.Command)]
    [InlineData("process", ChatToolActivityCategory.Command)]
    [InlineData("run_terminal_cmd", ChatToolActivityCategory.Command)]
    [InlineData("read_file", ChatToolActivityCategory.Read)]
    [InlineData("notebook_read", ChatToolActivityCategory.Read)]
    [InlineData("view", ChatToolActivityCategory.Read)]
    [InlineData("apply_patch", ChatToolActivityCategory.Edit)]
    [InlineData("applypatch", ChatToolActivityCategory.Edit)]
    [InlineData("multi_edit", ChatToolActivityCategory.Edit)]
    [InlineData("notebookedit", ChatToolActivityCategory.Edit)]
    [InlineData("write_file", ChatToolActivityCategory.Write)]
    [InlineData("web_search", ChatToolActivityCategory.Search)]
    [InlineData("WebSearch", ChatToolActivityCategory.Search)]
    [InlineData("rg", ChatToolActivityCategory.Search)]
    [InlineData("ls", ChatToolActivityCategory.Search)]
    [InlineData("list", ChatToolActivityCategory.Search)]
    [InlineData("codebase_search", ChatToolActivityCategory.Search)]
    [InlineData("web_fetch", ChatToolActivityCategory.Fetch)]
    [InlineData("WebFetch", ChatToolActivityCategory.Fetch)]
    [InlineData("custom.calendar", ChatToolActivityCategory.Generic)]
    public void Classify_CoversWebAndWindowsAliases(
        string name,
        ChatToolActivityCategory expected) =>
        Assert.Equal(expected, ChatToolActivityPresentation.Classify(name));

    [Fact]
    public void Classify_TextEditorUsesCommand()
    {
        Assert.Equal(
            ChatToolActivityCategory.Read,
            ChatToolActivityPresentation.Classify("str_replace_editor", Args(("command", "view"))));
        Assert.Equal(
            ChatToolActivityCategory.Edit,
            ChatToolActivityPresentation.Classify("str_replace_based_edit_tool", Args(("command", "insert"))));
        Assert.Equal(
            ChatToolActivityCategory.Write,
            ChatToolActivityPresentation.Classify("str_replace_editor", Args(("command", "create"))));
    }

    [Fact]
    public void Summarize_UsesUniquePathsWhenAnyKnownOtherwiseInvocationCount()
    {
        var tools = new[]
        {
            Tool("r1", "read_file", Args(("path", "src/a.cs"))),
            Tool("r2", "read_file", Args(("file_path", "SRC/A.cs"))),
            Tool("r3", "read_file"),
            Tool("e1", "edit_file", Args(("path", "src/a.cs"))),
            Tool("e2", "apply_patch"),
            Tool("w1", "write_file", Args(("paths", new JsonArray("a", "b", "a")))),
        };

        var summary = ChatToolActivityPresentation.Summarize(tools);

        Assert.Equal(1, Count(summary, ChatToolActivityCategory.Read));
        Assert.Equal(1, Count(summary, ChatToolActivityCategory.Edit));
        Assert.Equal(2, Count(summary, ChatToolActivityCategory.Write));

        var pathless = ChatToolActivityPresentation.Summarize(
            [Tool("r1", "read_file"), Tool("r2", "read_file")]);
        Assert.Equal(2, Count(pathless, ChatToolActivityCategory.Read));
    }

    [Fact]
    public void Summarize_MultiFileCodexPatchCountsUniqueTargets()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: src/a.cs
            @@
            -old
            +new
            *** Add File: src/b.cs
            +content
            *** End Patch
            """;
        var summary = ChatToolActivityPresentation.Summarize(
            [Tool("patch", "apply_patch", Args(("patch", patch)))]);

        Assert.Equal(2, Count(summary, ChatToolActivityCategory.Edit));
        Assert.Equal("Edited 2 files", Format(summary));
    }

    [Fact]
    public void ExtractPaths_StructuredChangesUseMoveDestination()
    {
        var changes = new JsonArray
        {
            new JsonObject { ["path"] = "src/a.cs" },
            new JsonObject
            {
                ["path"] = "src/old.cs",
                ["kind"] = new JsonObject { ["movePath"] = "src/new.cs" },
            },
        };

        var paths = ChatToolActivityPresentation.ExtractPaths(
            Args(("changes", changes)),
            "applypatch");

        Assert.Equal(
            ["src/a.cs", "src/new.cs"],
            paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractPaths_UnifiedDiffUsesNewPathAndOldPathForDeletion()
    {
        const string diff = """
            diff --git a/src/changed.cs b/src/changed.cs
            --- a/src/changed.cs
            +++ b/src/changed.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/src/deleted.cs b/src/deleted.cs
            deleted file mode 100644
            --- a/src/deleted.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            """;

        var paths = ChatToolActivityPresentation.ExtractPaths(
            Args(("diff", diff)),
            "patch");

        Assert.Equal(
            ["src/changed.cs", "src/deleted.cs"],
            paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractPaths_RenameOnlyDiffPreservesDistinctSpacedTargets()
    {
        const string diff = """
            diff --git "a/src/old one.cs" "b/src/new one.cs"
            similarity index 100%
            rename from src/old one.cs
            rename to src/new one.cs
            diff --git "a/src/old two.cs" "b/src/new two.cs"
            similarity index 100%
            rename from src/old two.cs
            rename to src/new two.cs
            """;

        var paths = ChatToolActivityPresentation.ExtractPaths(
            Args(("diff", diff)),
            "patch");

        Assert.Equal(
            ["src/new one.cs", "src/new two.cs"],
            paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Summarize_UsesNewestRunningToolAndNamedGenericTools()
    {
        var tools = new[]
        {
            Tool("a", "calendar", result: ChatToolCallStatus.Success),
            Tool("b", "web_fetch"),
            Tool("c", "powershell", result: ChatToolCallStatus.InProgress),
            Tool("d", "custom.weather", result: null),
        };

        var summary = ChatToolActivityPresentation.Summarize(tools);

        Assert.Equal("d", summary.NewestRunningTool!.Id);
        var generic = Assert.Single(
            summary.Counts,
            count => count.Category == ChatToolActivityCategory.Generic);
        Assert.Equal(2, generic.Count);
        Assert.Equal(["calendar", "weather"], generic.ToolNames);
    }

    [Fact]
    public void Formatter_AggregatesGenericNamesAndCapsLongNameLists()
    {
        var named = ChatToolActivityPresentation.Summarize(
            [Tool("a", "calendar"), Tool("b", "weather"), Tool("c", "calendar")]);
        var manyNames = ChatToolActivityPresentation.Summarize(
            [Tool("a", "alpha"), Tool("b", "beta"), Tool("c", "gamma"), Tool("d", "delta")]);

        Assert.Equal("Used calendar, weather 3 times", Format(named));
        Assert.Equal("Used 4 tools", Format(manyNames));
        Assert.Single(named.Counts, count => count.Category == ChatToolActivityCategory.Generic);
        Assert.Single(manyNames.Counts, count => count.Category == ChatToolActivityCategory.Generic);
    }

    [Fact]
    public void Formatter_CapitalizesFirstSegment()
    {
        var summary = ChatToolActivityPresentation.Summarize(
        [
            Tool("r", "read_file", Args(("path", "a"))),
            Tool("e", "edit_file", Args(("path", "b"))),
        ]);

        Assert.Equal("Read 1 file, edited 1 file", Format(summary));
    }

    [Fact]
    public void Formatter_LocalizesUnnamedRunningToolFallback()
    {
        var summary = ChatToolActivityPresentation.Summarize(
            [Tool("running", "", result: ChatToolCallStatus.InProgress)]);
        var templates = EnglishTemplates() with { ToolFallback = "Localized tool" };

        Assert.Equal("Running Localized tool", ChatToolActivityFormatter.Format(summary, templates));
    }

    [Fact]
    public void ExpansionState_DefaultsCollapsedAndExplicitChoiceSurvivesUntilReset()
    {
        var state = new ChatToolActivityExpansionState();
        var running = ChatToolActivityPresentation.Summarize(
            [Tool("a", "powershell", result: ChatToolCallStatus.InProgress)]);

        Assert.False(state.IsExpanded("group", running, 0));
        state.SetExplicit("group", true, 0);
        Assert.True(state.IsExpanded("group", running, 0));
        Assert.False(state.IsExpanded("group", running, 1));
    }

    private static int Count(ChatToolActivitySummary summary, ChatToolActivityCategory category) =>
        summary.Counts.Single(count => count.Category == category).Count;

    private static string Format(ChatToolActivitySummary summary) =>
        ChatToolActivityFormatter.Format(summary, EnglishTemplates());

    private static ChatToolActivityFormatTemplates EnglishTemplates() => new(
            "Ran {0} command",
            "Ran {0} commands",
            "read {0} file",
            "read {0} files",
            "edited {0} file",
            "edited {0} files",
            "wrote {0} file",
            "wrote {0} files",
            "ran {0} search",
            "ran {0} searches",
            "fetched {0} page",
            "fetched {0} pages",
            "used {0} tool",
            "used {0} tools",
            "used {1}",
            "used {1} {0} times",
            "Running {0}",
            "Tool");

    private static ChatTimelineItem Item(string id, ChatTimelineItemKind kind) =>
        new(id, kind, id);

    private static ChatTimelineItem Tool(
        string id,
        string name,
        JsonObject? args = null,
        ChatToolCallStatus? result = ChatToolCallStatus.Success) =>
        new(id, ChatTimelineItemKind.ToolCall, id, ToolName: name, ToolResult: result, ToolArgs: args);

    private static JsonObject Args(params (string Name, object Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
            result[name] = value is JsonNode node ? node : JsonValue.Create(value);
        return result;
    }
}
