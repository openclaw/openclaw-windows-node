using System.Text.Json.Nodes;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolActivityPresentationTests
{
    [Fact]
    public void WorkMotion_OnlyNewCompletedWorkEntersOnceWithinTheCurrentHistory()
    {
        var state = new ChatToolActivityMotionState();
        Assert.Empty(state.Update(["history"], "s", 1, 1));
        Assert.Equal(["completed"], state.Update(["history", "completed"], "s", 1, 1));
        Assert.Empty(state.Update(["history", "completed"], "s", 1, 1));
        Assert.Empty(state.Update(["older", "history", "completed"], "s", 1, 2));
        Assert.Empty(state.Update(["history", "completed"], "other", 1, 2));
        Assert.Empty(state.Update(["history", "completed"], "other", 2, 2));
        Assert.Equal(["next"], state.Update(["history", "completed", "next"], "other", 2, 2));
    }

    [Fact]
    public void WorkMotion_LoadingDoesNotTreatInitialHistoryAsNewWork()
    {
        var state = new ChatToolActivityMotionState();
        Assert.Empty(state.Update([], "s", 1, 1, historyReady: false));
        Assert.Empty(state.Update(["history"], "s", 1, 1));
        Assert.Empty(state.Update([], "s", 1, 1, historyReady: false));
        Assert.Empty(state.Update(["history", "backfilled"], "s", 1, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmedYield_PreservesPublicAcknowledgmentAndPreventsTurnRollup(bool showTools)
    {
        var yield = Tool("yield", "sessions_yield") with
        {
            ToolCallId = "real-call", ToolRunId = "r",
            HandoffInput = new("sessions_yield", false),
            HandoffReceipt = new("sessions_yield", "Waiting for the task"),
            ToolOutput = "receipt not rendered",
        };
        var rows = ChatToolActivityPresentation.Project(
            [Reply("work", "commentary"), yield, Reply("resumed-answer")],
            "agent:a:dashboard:d", 0, showToolCalls: showTools);
        Assert.All(rows, row => Assert.False(row.IsCompletedWork));
        Assert.Equal(["work", "Waiting for the task", "resumed-answer"], rows.Select(row => row.Entry!.Text));
        Assert.Equal("yield:handoff", rows[1].Entry!.Id);
        Assert.Null(rows[1].Entry!.ToolOutput);
    }

    [Theory]
    [InlineData("sessions_yield")]
    [InlineData("github_publish")]
    public void Handoff_KeepsOwnTrailingSuccessfulWorkBeforeAnswerButNeverMovesAFailure(string name)
    {
        var handoff = Tool("handoff", name) with
        {
            ToolCallId = "call", ToolRunId = "r", HandoffInput = new(name, false),
            HandoffReceipt = new(name, null),
        };
        foreach (var status in new[] { ChatToolCallStatus.Success, ChatToolCallStatus.Error })
        {
            var tail = Tool("tail", "read", result: status) with { ToolRunId = "r" };
            var rows = ChatToolActivityPresentation.Project(
                [handoff, Reply("answer"), tail], "agent:a:dashboard:d", 0);
            Assert.All(rows, row => Assert.False(row.IsCompletedWork));
            Assert.Equal(status == ChatToolCallStatus.Success ? "answer" : "tail", RowId(rows[^1]));
        }
    }

    [Theory]
    [InlineData("agent:a:dashboard:d", "r", "done", 65000d, 65000d)]
    [InlineData("agent:a:dashboard:d", "r", "failed", 0d, 0d)]
    [InlineData("agent:other:dashboard:d", "r", "done", 65000d, null)]
    [InlineData("agent:a:dashboard:d", "other", "done", 65000d, null)]
    [InlineData("agent:a:dashboard:d", "r", "running", 65000d, null)]
    [InlineData("agent:a:dashboard:d", "r", "done", -1d, null)]
    [InlineData("agent:a:dashboard:d", "r", "done", double.NaN, null)]
    public void CompletedWorkDuration_RequiresExactSessionTerminalRunAndFiniteDuration(
        string sessionKey, string run, string status, double milliseconds, double? expected)
    {
        var session = new ChatThread
        {
            Id = sessionKey, Title = "Session", LastRunId = run, RunStatus = status, RuntimeMs = milliseconds,
        };
        var rows = ChatToolActivityPresentation.Project(
            [Reply("work", "commentary"), Reply("answer")], "agent:a:dashboard:d", 0, session: session);
        Assert.Equal(expected, rows[0].DurationMs);
        Assert.Equal("1m 5s", ChatRunProgressProjection.ElapsedMilliseconds(65000, key => key));
    }

    [Fact]
    public void MultiRunWorkWithoutOneOwnerRemainsUntimed()
    {
        var session = new ChatThread
        {
            Id = "agent:a:dashboard:d", Title = "Session", LastRunId = "b", RunStatus = "done", RuntimeMs = 1000,
        };
        var rows = ChatToolActivityPresentation.Project(
            [Reply("work-a", "commentary", "a"), Reply("answer-a", run: "a"),
             Reply("work-b", "commentary", "b"), Reply("answer-b", run: "b")],
            session.Id, 0, session: session);
        Assert.Null(rows[0].DurationMs);
    }

    private static ChatTimelineItem Reply(string id, string? phase = null, string? run = "r") =>
        new(id, ChatTimelineItemKind.Assistant, id,
            Presentation: new(Phase: phase, RunId: run));

    [Fact]
    public void Dashboard_CompletedWorkUsesLastUnphasedReplyWithoutChangingStoredEntries()
    {
        var entries = new[] { Reply("narration"), Tool("tool", "read_file"), Reply("answer"),
            Reply("later-commentary", "commentary") };
        var rows = ChatToolActivityPresentation.Project(entries, "agent:a:dashboard:d", 3);
        Assert.Equal(["narration", "tool", "later-commentary"], rows[0].Work!.Select(entry => entry.Id));
        Assert.Equal("answer", rows[1].Entry!.Id);
        Assert.Null(entries[0].Presentation!.Phase);
        Assert.Equal("narration", entries[0].Id);
        var prepend = ChatToolActivityPresentation.Project(
            [Tool("older", "read_file"), .. entries], "agent:a:dashboard:d", 3);
        Assert.Equal(rows[0].Key, prepend[0].Key);
        Assert.NotEqual(rows[0].Key,
            ChatToolActivityPresentation.Project(entries, "agent:a:dashboard:d", 4)[0].Key);
    }

    [Theory]
    [InlineData("main", false, false)]
    [InlineData("agent:a:dashboard:d:extra", false, false)]
    [InlineData("agent:a:dashboard:d", true, false)]
    [InlineData("agent:a:dashboard:d", false, true)]
    public void Dashboard_GatesLeaveNarrationVisible(string session, bool working, bool bubble)
    {
        var rows = ChatToolActivityPresentation.Project(
            [Reply("narration"), Reply("answer")], session, 0,
            runWorking: working, bubbleMode: bubble);
        Assert.All(rows, row => Assert.False(row.IsCompletedWork));
    }

    [Fact]
    public void Dashboard_PreservesExplicitAnswersAndEarlierRunRepliesAndTrailingFailures()
    {
        var rows = ChatToolActivityPresentation.Project(
            [Reply("work", "commentary", "a"), Reply("answer-a", run: "a"),
             Reply("work-b", "commentary", "b"), Reply("explicit", "final_answer", "b"),
             Reply("answer-b", run: "b"), Tool("failed", "exec", result: ChatToolCallStatus.Error) with { ToolRunId = "b" }],
            "agent:a:dashboard:d", 0);
        Assert.Equal(["work", "work-b"], rows[0].Work!.Select(entry => entry.Id));
        Assert.Equal(["answer-a", "explicit", "answer-b", "failed"], rows.Skip(1).Select(RowId));
    }

    [Fact]
    public void Dashboard_ToolOnlyAndStreamingTurnsHaveNoCompletedDisclosure()
    {
        Assert.DoesNotContain(ChatToolActivityPresentation.Project(
            [Tool("running", "read_file", result: ChatToolCallStatus.InProgress), Reply("answer")],
            "agent:a:dashboard:d", 0), row => row.IsCompletedWork);
        Assert.DoesNotContain(ChatToolActivityPresentation.Project(
            [Tool("t", "read_file"), Reply("narration", "commentary")], "agent:a:dashboard:d", 0),
            row => row.IsCompletedWork);
        Assert.DoesNotContain(ChatToolActivityPresentation.Project(
            [Reply("narration") with { IsStreaming = true }, Reply("answer")], "agent:a:dashboard:d", 0),
            row => row.IsCompletedWork);
    }

    [Fact]
    public void Dashboard_SteerKeepsNamedEarlierTurnLive()
    {
        var user = new ChatTimelineItem("u", ChatTimelineItemKind.User, "question", Presentation: new(RunId: "r"));
        var steer = new ChatTimelineItem("s", ChatTimelineItemKind.User, "steer", Presentation: new(SteerTargetRunId: "r"));
        var rows = ChatToolActivityPresentation.Project(
            [user, Reply("work"), Reply("answer"), steer], "agent:a:dashboard:d", 0, runWorking: true);
        Assert.DoesNotContain(rows, row => row.IsCompletedWork);
    }

    [Fact]
    public void Dashboard_IndependentAndStructuralBoundariesNeverMoveEarlierWork()
    {
        foreach (var boundary in new[]
        {
            new ChatTimelineItem("status", ChatTimelineItemKind.Status, "handoff"),
            Reply("forwarded") with { Presentation = new(ProvenanceKind: "inter_session", SourceTool: "sessions_send") },
            Reply("automation") with { Presentation = new(AutomationKind: "cron", AutomationJobId: "j", AutomationRunId: "ar") },
            Reply("boundary", "commentary") with { Presentation = new(Phase: "commentary", TurnBoundary: true) },
        })
        {
            var rows = ChatToolActivityPresentation.Project(
                [Reply("prior-work", "commentary"), boundary, Reply("answer")], "agent:a:dashboard:d", 0);
            Assert.Equal("prior-work", rows[0].Entry!.Id);
            Assert.DoesNotContain(rows.SelectMany(row => row.Work ?? []), entry => entry.Id == "prior-work");
        }
    }

    [Fact]
    public void Dashboard_MediaRemainsVisibleEvenWithCommentaryPhase()
    {
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            ["media"] = new(null, null, AssistantContent: new([
                new(ChatMediaContentKind.Image, "image", null, null, new())])),
        };
        var rows = ChatToolActivityPresentation.Project(
            [Reply("work", "commentary"), Reply("media", "commentary"), Reply("answer")],
            "agent:a:dashboard:d", 0, metadata: metadata);
        Assert.Equal(["work"], rows[0].Work!.Select(entry => entry.Id));
        Assert.Equal(["media", "answer"], rows.Skip(1).Select(row => row.Entry!.Id));
    }

    [Fact]
    public void Dashboard_RecoveryCanFoldEarlierFailureButNotReplylessBackgroundWork()
    {
        var rows = ChatToolActivityPresentation.Project(
            [Tool("failure", "exec", result: ChatToolCallStatus.Error) with { ToolRunId = "r" },
             Reply("answer"), Tool("background", "exec") with { ToolRunId = "background" }],
            "agent:a:dashboard:d", 0);
        Assert.Equal(["failure"], rows[0].Work!.Select(entry => entry.Id));
        Assert.Equal(["answer", "background"], rows.Skip(1).Select(RowId));
    }

    [Theory]
    [InlineData("cron", "job", null)]
    [InlineData("cron", null, "run")]
    [InlineData("unknown", "job", "run")]
    public void Dashboard_MalformedAutomationDoesNotInventIndependentOwnership(string kind, string? job, string? run)
    {
        var entry = Reply("work", "commentary") with
        {
            Presentation = new(Phase: "commentary", AutomationKind: kind, AutomationJobId: job, AutomationRunId: run),
        };
        var rows = ChatToolActivityPresentation.Project([entry, Reply("answer")], "agent:a:dashboard:d", 0);
        Assert.Equal(["work"], rows[0].Work!.Select(item => item.Id));
    }

    [Fact]
    public void Project_GroupsEveryConsecutiveToolSpanIncludingSingleTools()
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
            rows.Select(RowId));
        Assert.Equal(["one"], rows[1].Tools.Select(static tool => tool.Id));
        Assert.Null(rows[1].Entry);
        Assert.Equal(["two", "three"], rows[3].Tools.Select(static tool => tool.Id));
        Assert.Equal(["four"], rows[5].Tools.Select(static tool => tool.Id));
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
    public void Project_KeepsRowKeyStableWhenSingleToolGroupGrows()
    {
        var single = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell")],
            "session",
            9).Single();
        var grouped = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell"), Tool("second", "read_file")],
            "session",
            9).Single();

        Assert.True(single.IsActivityGroup);
        Assert.True(grouped.IsActivityGroup);
        Assert.Equal(single.Key, grouped.Key);
    }

    [Theory]
    [InlineData(ChatToolCallStatus.Error)]
    [InlineData(ChatToolCallStatus.Interrupted)]
    public void Project_UnsuccessfulToolsStayVisibleInsideTheirGroup(ChatToolCallStatus result)
    {
        var rows = ChatToolActivityPresentation.Project(
            [
                Tool("success-1", "read_file"),
                Tool("success-2", "write_file"),
                Tool("failed", "powershell", result: result),
                Tool("success-3", "web_fetch"),
            ],
            "session",
            9);

        var group = Assert.Single(rows);
        Assert.Equal(["success-1", "success-2", "failed", "success-3"], group.Tools.Select(tool => tool.Id));
        Assert.Equal(result, group.Tools[2].ToolResult);
        Assert.Equal("1 command · 1 read · 1 write · 1 fetch", Format(group.Summary!));
    }

    [Fact]
    public void Project_ReviewedAndPreparingToolsKeepStandaloneDisclosure()
    {
        var reviewed = Tool("reviewed", "powershell") with
        {
            ToolReviews = [new ChatToolReview("review", "Review", "approved")],
        };
        var preparing = Tool("preparing", "read_file", result: ChatToolCallStatus.InProgress) with
        {
            ToolInputPreparing = true,
        };

        var rows = ChatToolActivityPresentation.Project(
            [Tool("before", "read_file"), reviewed, preparing, Tool("after", "read_file")],
            "session",
            9);

        Assert.Equal(["before", "reviewed", "preparing", "after"], rows.Select(RowId));
        Assert.Equal([true, false, false, true], rows.Select(row => row.IsActivityGroup));
    }

    [Theory]
    [InlineData("run-a", "run-b", 0, 0)]
    [InlineData("run-a", "RUN-A", 0, 0)]
    [InlineData("run-a", null, 0, 0)]
    [InlineData(null, null, 1, 2)]
    public void Project_DoesNotCombineDifferentExecutionScopes(
        string? firstRun, string? secondRun, long firstTurn, long secondTurn)
    {
        var rows = ChatToolActivityPresentation.Project(
            [
                Tool("a", "read") with { ToolRunId = firstRun, ToolLegacyTurn = firstTurn },
                Tool("b", "read") with { ToolRunId = firstRun, ToolLegacyTurn = firstTurn },
                Tool("c", "read") with { ToolRunId = secondRun, ToolLegacyTurn = secondTurn },
                Tool("d", "read") with { ToolRunId = secondRun, ToolLegacyTurn = secondTurn },
            ], "session", 1);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows[0].Tools.Select(tool => tool.Id));
        Assert.Equal(["c", "d"], rows[1].Tools.Select(tool => tool.Id));
    }

    [Fact]
    public void Project_StreamingAndTerminalUpdatesPreserveGroupIdentityAndExplicitExpansion()
    {
        var state = new ChatToolActivityExpansionState();
        var entries = new[]
        {
            Tool("a", "read", result: ChatToolCallStatus.InProgress) with { ToolRunId = "r" },
            Tool("b", "read", result: ChatToolCallStatus.InProgress) with { ToolRunId = "r" },
        };
        var running = Assert.Single(ChatToolActivityPresentation.Project(entries, "s", 1));
        state.SetExplicit(running.Key, true, 0);
        entries[0] = entries[0] with { ToolOutput = "partial" };
        var partial = Assert.Single(ChatToolActivityPresentation.Project(entries, "s", 1));
        entries[0] = entries[0] with { ToolResult = ChatToolCallStatus.Success, ToolOutput = "final" };
        entries[1] = entries[1] with { ToolResult = ChatToolCallStatus.Success };
        var completed = Assert.Single(ChatToolActivityPresentation.Project(entries, "s", 1));

        Assert.Equal(running.Key, partial.Key);
        Assert.Equal(running.Key, completed.Key);
        Assert.True(state.IsExpanded(completed.Key, completed.Summary!, 0));
        Assert.False(completed.Summary!.IsRunning);
        Assert.Equal("final", completed.Tools[0].ToolOutput);
        Assert.False(state.IsExpanded(
            ChatToolActivityPresentation.ActivityKey("other-session", 1, "a"), completed.Summary, 0));
        Assert.False(state.IsExpanded(
            ChatToolActivityPresentation.ActivityKey("s", 2, "a"), completed.Summary, 0));
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
    [InlineData("Apply Patch", ChatToolActivityCategory.Edit)]
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
    public void Summarize_CountsEachOperationRatherThanDistinctFiles()
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

        Assert.Equal(3, Count(summary, ChatToolActivityCategory.Read));
        Assert.Equal(2, Count(summary, ChatToolActivityCategory.Edit));
        Assert.Equal(1, Count(summary, ChatToolActivityCategory.Write));
        Assert.Equal(6, summary.ToolCount);
        Assert.Equal("3 reads · 2 edits · 1 write", Format(summary));
    }

    [Fact]
    public void Summarize_UsesNewestRunningToolAndCountsGenericTools()
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
    }

    [Fact]
    public void Formatter_CountsUnclassifiedToolsAsOtherOperations()
    {
        var one = ChatToolActivityPresentation.Summarize([Tool("a", "calendar")]);
        var many = ChatToolActivityPresentation.Summarize(
            [Tool("a", "alpha"), Tool("b", "beta"), Tool("c", "alpha"), Tool("d", "delta")]);

        Assert.Equal("1 other operation", Format(one));
        Assert.Equal("4 other operations", Format(many));
    }

    [Fact]
    public void Formatter_JoinsCountsInWebCategoryOrder()
    {
        var summary = ChatToolActivityPresentation.Summarize(
        [
            Tool("x", "custom.calendar"),
            Tool("e", "edit_file"),
            Tool("r", "read_file"),
            Tool("c1", "powershell"),
            Tool("c2", "powershell"),
        ]);

        Assert.Equal("2 commands · 1 read · 1 edit · 1 other operation", Format(summary));
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

    [Fact]
    public void GroupNested_NestsRecordedChildrenWithinTheSameRunInOriginalOrder()
    {
        var tools = new[]
        {
            Linked("wrapper", "exec", "r", "w"),
            Linked("first", "exec", "r", "c1", parent: "w"),
            Linked("other-run", "read_file", "r2", "c2", parent: "w"),
            Linked("unrun", "read_file", null, "c3", parent: "w"),
            Linked("second", "read_file", "r", "c4", parent: "w"),
        };

        var roots = ChatToolActivityPresentation.GroupNested(tools);

        Assert.Equal(["wrapper", "other-run", "unrun"], roots.Select(node => node.Tool.Id));
        Assert.Equal(["first", "second"], roots[0].Children.Select(node => node.Tool.Id));
    }

    [Fact]
    public void GroupNested_LeavesAmbiguousSelfAndCyclicLinksAsRoots()
    {
        var tools = new[]
        {
            Linked("dup-1", "exec", "r", "d"),
            Linked("dup-2", "exec", "r", "d"),
            Linked("to-dup", "exec", "r", "x", parent: "d"),
            Linked("self", "exec", "r", "s", parent: "s"),
            Linked("cycle-a", "exec", "r", "a", parent: "b"),
            Linked("cycle-b", "exec", "r", "b", parent: "a"),
            Linked("into-cycle", "exec", "r", "i", parent: "a"),
        };

        var roots = ChatToolActivityPresentation.GroupNested(tools);

        Assert.Equal(
            ["dup-1", "dup-2", "to-dup", "self", "cycle-a", "cycle-b"],
            roots.Select(node => node.Tool.Id));
        Assert.Equal("into-cycle", Assert.Single(roots[4].Children).Tool.Id);
    }

    [Fact]
    public void Summarize_CountsNestedCallsButNotTheirWrapper()
    {
        var summary = ChatToolActivityPresentation.Summarize(
        [
            Linked("wrapper", "exec", "r", "w"),
            Linked("failed", "exec", "r", "c1", parent: "w", result: ChatToolCallStatus.Error),
            Linked("read", "read_file", "r", "c2", parent: "w"),
        ]);

        Assert.Equal(2, summary.ToolCount);
        Assert.Equal("1 command · 1 read", Format(summary));
    }

    [Fact]
    public void ToolRowKey_DiffersFromTheGroupThatStartsWithTheSameTool()
    {
        Assert.NotEqual(
            ChatToolActivityPresentation.ActivityKey("s", 1, "t"),
            ChatToolActivityPresentation.ToolRowKey("s", 1, "t"));
    }

    [Fact]
    public void ProjectWork_GroupsWorkToolsAroundCommentary()
    {
        var rows = ChatToolActivityPresentation.ProjectWork(
            [Item("note", ChatTimelineItemKind.Assistant), Tool("a", "read_file"), Tool("b", "exec")],
            "s",
            1);

        Assert.Equal("note", rows[0].Entry!.Id);
        Assert.Equal(["a", "b"], rows[1].Tools.Select(tool => tool.Id));
    }

    private static ChatTimelineItem Linked(
        string id,
        string name,
        string? run,
        string call,
        string? parent = null,
        ChatToolCallStatus? result = ChatToolCallStatus.Success) =>
        Tool(id, name, result: result) with { ToolRunId = run, ToolCallId = call, ToolParentCallId = parent };

    private static int Count(ChatToolActivitySummary summary, ChatToolActivityCategory category) =>
        summary.Counts.Single(count => count.Category == category).Count;

    private static string Format(ChatToolActivitySummary summary) =>
        ChatToolActivityFormatter.Format(summary, EnglishTemplates());

    private static string RowId(ChatToolActivityRow row) => row.Entry?.Id ?? row.Tools[0].Id;

    private static ChatToolActivityFormatTemplates EnglishTemplates() => new(
            "{0} command",
            "{0} commands",
            "{0} read",
            "{0} reads",
            "{0} edit",
            "{0} edits",
            "{0} write",
            "{0} writes",
            "{0} search",
            "{0} searches",
            "{0} fetch",
            "{0} fetches",
            "{0} other operation",
            "{0} other operations",
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
