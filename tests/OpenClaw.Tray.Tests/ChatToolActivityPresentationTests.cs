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
    public void Project_GroupsMultiToolSpansAndKeepsSingleToolsStandalone()
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
        Assert.Equal("one", rows[1].Entry!.Id);
        Assert.False(rows[1].IsActivityGroup);
        Assert.Equal(["two", "three"], rows[3].Tools.Select(static tool => tool.Id));
        Assert.Equal("four", rows[5].Entry!.Id);
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
    public void Project_KeepsRowKeyStableWhenSingleToolGrowsIntoGroup()
    {
        var single = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell")],
            "session",
            9).Single();
        var grouped = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell"), Tool("second", "read_file")],
            "session",
            9).Single();

        Assert.False(single.IsActivityGroup);
        Assert.True(grouped.IsActivityGroup);
        Assert.Equal(single.Key, grouped.Key);
    }

    [Theory]
    [InlineData(ChatToolCallStatus.Error, " · 1 failed")]
    // Like the web, a stopped call is not a failure; its row says Interrupted.
    [InlineData(ChatToolCallStatus.Interrupted, "")]
    public void Project_UnsuccessfulToolsStayVisibleInsideTheirGroup(ChatToolCallStatus result, string failedSuffix)
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
        Assert.Equal($"1 command · 1 read · 1 write · 1 fetch{failedSuffix}", Format(group.Summary!));
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
            [Tool("before", "read_file"), Tool("before-2", "read_file"), reviewed, preparing,
             Tool("after", "read_file"), Tool("after-2", "read_file")],
            "session",
            9);

        Assert.Equal(["before", "reviewed", "preparing", "after"], rows.Select(RowId));
        Assert.Equal([true, false, false, true], rows.Select(row => row.IsActivityGroup));
    }

    [Theory]
    [InlineData("run-a", "run-b", 0, 0)]
    [InlineData("run-a", null, 0, 0)]
    [InlineData(null, null, 1, 2)]
    public void Project_JoinsAdjacentToolRowsAcrossRunsButNestsOnlyWithinOne(
        string? firstRun, string? secondRun, long firstTurn, long secondTurn)
    {
        var rows = ChatToolActivityPresentation.Project(
            [
                Tool("a", "read") with { ToolRunId = firstRun, ToolLegacyTurn = firstTurn, ToolCallId = "x" },
                Tool("b", "read") with { ToolRunId = firstRun, ToolLegacyTurn = firstTurn },
                Tool("c", "read") with
                {
                    ToolRunId = secondRun, ToolLegacyTurn = secondTurn, ToolParentCallId = "x",
                },
                Tool("d", "read") with { ToolRunId = secondRun, ToolLegacyTurn = secondTurn },
            ], "session", 1);

        // Like the web coalesceActivityRuns, nothing visible separates them, so they read as one log.
        var group = Assert.Single(rows);
        Assert.Equal(["a", "b", "c", "d"], group.Tools.Select(tool => tool.Id));
        Assert.Equal(ChatToolActivityPresentation.ActivityKey("session", 1, "a"), group.Key);
        // Recorded nesting still never crosses a run.
        Assert.Equal(4, ChatToolActivityPresentation.GroupNested(group.Tools).Count);
    }

    [Fact]
    public void Project_HeartbeatBurstAcrossRunsIsOneGroupWithItsFailures()
    {
        var rows = ChatToolActivityPresentation.Project(
            [
                Item("answer", ChatTimelineItemKind.Assistant),
                .. Enumerable.Range(0, 14).Select(index =>
                    Tool($"beat-{index}", "heartbeat_respond", result: ChatToolCallStatus.Error) with
                    {
                        ToolRunId = $"heartbeat-{index}",
                    }),
            ], "session", 1);

        Assert.Equal("answer", rows[0].Entry!.Id);
        Assert.Equal("14 other operations · 14 failed", Format(Assert.Single(rows.Skip(1)).Summary!));
    }

    [Theory]
    [InlineData("heartbeat_respond", "Heartbeat Respond")]
    [InlineData("exec", "Exec")]
    [InlineData("Apply Patch", "Apply Patch")]
    [InlineData("system.run", "system.run")]
    [InlineData("PowerShell", "PowerShell")]
    public void DisplayName_TitleCasesRawSnakeCaseNamesLikeTheWeb(string name, string expected) =>
        Assert.Equal(expected, ChatToolRowPresentation.DisplayName(name));

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
    public void Summarize_CountsStartedSubagentsLastAndLeavesOutRoutineBookkeeping()
    {
        var launch = Tool("spawn", "sessions_spawn") with
        {
            ToolInputDetails = new("""{"label":"Telegram check","task":"Diagnose"}"""),
        };
        var failedLaunch = Tool("failed", "sessions_spawn", result: ChatToolCallStatus.Error);
        var ownSession = Tool("own", "sessions_spawn") with { ToolInputDetails = new("""{"visible":true}""") };
        var poll = Tool("poll", "process") with { ToolInputDetails = new("""{"action":"poll"}""") };

        var summary = ChatToolActivityPresentation.Summarize(
            [launch, Tool("read", "read_file"), failedLaunch, ownSession, Tool("yield", "sessions_yield"), poll]);

        Assert.Equal("1 read · 2 other operations · 1 subagent · 1 failed", Format(summary));
        Assert.Equal(4, summary.ToolCount);
        Assert.Equal("1 other operation",
            Format(ChatToolActivityPresentation.Summarize([Tool("yield", "sessions_yield")])));
    }

    [Theory]
    [InlineData("read_file", "path", "skills/telegram/SKILL.md", ChatToolCallStatus.Success,
        ChatToolRowVerb.Read, "SKILL.md", "skills/telegram")]
    [InlineData("edit_file", "file_path", "C:\\repo\\src\\main.cs", ChatToolCallStatus.Success,
        ChatToolRowVerb.Edited, "main.cs", "C:\\repo\\src")]
    [InlineData("edit_file", "path", "main.cs", ChatToolCallStatus.Error, ChatToolRowVerb.Edit, "main.cs", null)]
    [InlineData("write_file", "path", "out/notes.md", ChatToolCallStatus.InProgress,
        ChatToolRowVerb.Writing, "notes.md", "out")]
    [InlineData("web_fetch", "url", "https://example.test/a", ChatToolCallStatus.Success,
        ChatToolRowVerb.Fetched, "https://example.test/a", null)]
    public void RowView_NamesVerbTargetAndDirectory(
        string tool, string key, string value, ChatToolCallStatus status,
        ChatToolRowVerb verb, string target, string? detail)
    {
        var view = ChatToolRowPresentation.Resolve(Tool("t", tool, Args((key, value)), status));

        Assert.Equal((verb, target, detail), (view.Verb!.Value, view.Target!, view.TargetDetail));
    }

    [Fact]
    public void RowView_ReadsFieldsKeptOnlyInInputDetails()
    {
        var search = Tool("s", "grep") with
        {
            ToolInputDetails = new("""{"pattern":"TODO","path":"src"}"""),
        };
        var titled = Tool("c", "exec", Args(("command", "dotnet test"))) with
        {
            ToolInputDetails = new("""{"title":"Run the tests"}""", "dotnet test"),
        };
        var wrapped = Tool("w", "bash", Args(("command", "bash -lc 'git   status'")));

        Assert.Equal((ChatToolRowVerb.Searched, "TODO", "src"),
            (ChatToolRowPresentation.Resolve(search).Verb!.Value, ChatToolRowPresentation.Resolve(search).Target,
                ChatToolRowPresentation.Resolve(search).TargetDetail));
        Assert.Equal("Run the tests", ChatToolRowPresentation.Resolve(titled).Title);
        Assert.Equal("git status", ChatToolRowPresentation.Resolve(wrapped).Command);
        Assert.True(ChatToolRowPresentation.Resolve(Tool("x", "custom.calendar")).IsEmpty);
        var longPath = string.Join('/', Enumerable.Repeat("very-long-directory", 20)) + "/SKILL.md";
        Assert.Equal("SKILL.md", ChatToolRowPresentation.Resolve(Tool("l", "read_file", Args(("path", longPath)))).Target);
    }

    [Theory]
    [InlineData("*** Begin Patch\n*** Add File: docs/new.md\n+hello\n*** End Patch", ChatToolRowVerb.Created, "new.md", 0)]
    [InlineData("*** Begin Patch\n*** Update File: a/x.cs\n*** Move to: a/y.cs\n*** End Patch", ChatToolRowVerb.Edited,
        "x.cs \u2192 y.cs", 0)]
    [InlineData("*** Begin Patch\n*** Add File: a.md\n*** Delete File: b.md\n*** End Patch", ChatToolRowVerb.Changed, null, 2)]
    [InlineData("diff --git a/src/z.cs b/src/z.cs\n--- a/src/z.cs\n+++ b/src/z.cs\n@@ -1 +1 @@\n-a\n+b",
        ChatToolRowVerb.Edited, "z.cs", 0)]
    [InlineData("--- a/m.cs\n+++ b/m.cs\n@@ -1,2 +1,2 @@\n--- old\n+++ new\n", ChatToolRowVerb.Edited, "m.cs", 0)]
    public void RowView_DescribesPatchOperationsFromFreeFormInput(
        string patch, ChatToolRowVerb verb, string? target, int files)
    {
        var view = ChatToolRowPresentation.Resolve(Tool("p", "apply_patch") with { ToolInputDetails = new(patch) });

        Assert.Equal((verb, target, files), (view.Verb!.Value, view.Target, view.FileCount));
    }

    [Theory]
    [InlineData("running", ChatActivity.Working, false, null, "Working", null)]
    [InlineData("done", ChatActivity.Idle, true, 65000d, "AwaitingSubagents", null)]
    [InlineData("done", ChatActivity.Idle, false, 65000d, "Done", 65000d)]
    [InlineData("done", ChatActivity.Idle, false, null, "Done", 65000d)]
    [InlineData("running", ChatActivity.Idle, false, 65000d, "Ended", 65000d)]
    [InlineData("timeout", ChatActivity.Idle, false, 65000d, "Failed", 65000d)]
    [InlineData("killed", ChatActivity.Idle, false, 65000d, "Stopped", 65000d)]
    public void RowView_ShowsLabeledSubagentWithItsSessionState(
        string runStatus, ChatActivity activity, bool waitingForSubagents, double? runtimeMs,
        string status, double? elapsedMs)
    {
        var launch = Tool("spawn", "sessions_spawn") with
        {
            ToolInputDetails = new("""{"label":"Telegram check","task":"Diagnose the integration"}"""),
            ToolOutput = """{"status":"accepted","childSessionKey":"agent:main:subagent:abc"}""",
        };
        // Without a runtime sample, elapsed time falls back to the recorded start and end.
        var started = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var child = new ChatThread
        {
            Id = "agent:main:subagent:abc", Title = "Telegram check", Activity = activity,
            RunStatus = runStatus, RuntimeMs = runtimeMs, WaitingForSubagents = waitingForSubagents,
            CreatedAt = started, EndedAt = started.AddSeconds(65).ToUnixTimeMilliseconds(),
        };

        Assert.Equal(
            new ChatSpawnedSubagent("Telegram check", Enum.Parse<ChatSubagentStatus>(status),
                elapsedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, "agent:main:subagent:abc"),
            ChatToolRowPresentation.Resolve(launch, [child]).Subagent);
        Assert.Equal(new ChatSpawnedSubagent("Telegram check", ChildSessionKey: "agent:main:subagent:abc"),
            ChatToolRowPresentation.Resolve(launch).Subagent);
        Assert.True(ChatToolRowPresentation.Resolve(launch with
        {
            ToolInputDetails = new("""{"task":"Diagnose the integration"}"""),
        }).IsEmpty);
        Assert.True(ChatToolRowPresentation.Resolve(launch with
        {
            ToolOutput = """{"childSessionKey":"agent:main:dashboard:abc"}""",
        }).IsEmpty);
    }

    [Fact]
    public void AssistantRuns_ToolRowsStayInsideOneRunWithOneAvatarAndOneFooter()
    {
        var rows = ChatToolActivityPresentation.Project(
        [
            Item("user", ChatTimelineItemKind.User),
            Tool("first-tool", "exec"),
            Item("narration", ChatTimelineItemKind.Assistant),
            Tool("second-tool", "exec"),
            Tool("third-tool", "read_file"),
            Item("reasoning", ChatTimelineItemKind.Reasoning),
            Item("answer", ChatTimelineItemKind.Assistant),
            Item("next-user", ChatTimelineItemKind.User),
            Item("reply", ChatTimelineItemKind.Assistant),
            Item("status", ChatTimelineItemKind.Status),
            Item("after-status", ChatTimelineItemKind.Assistant),
        ], "session", 1);

        var positions = ChatTimelineAssistantRuns.Describe(rows);

        Assert.Equal(
            ["user", "first-tool", "narration", "second-tool", "reasoning", "answer", "next-user", "reply", "status", "after-status"],
            rows.Select(RowId));
        // The run's first row carries the avatar even when it is a tool row.
        Assert.Equal(
            ["first-tool", "reply", "after-status"],
            rows.Where((_, index) => positions[index].IsStart).Select(RowId));
        // Only each run's last assistant message owns the actions footer.
        Assert.Equal(
            ["answer", "reply", "after-status"],
            rows.Where((_, index) => positions[index].IsEnd).Select(RowId));
    }

    [Fact]
    public void AssistantRuns_SplitOnRunIdentityAndTurnStartsAndSkipCommentaryAndLiveRuns()
    {
        ChatTimelineItem Reply(string id, string? run, string? phase = null) =>
            new(id, ChatTimelineItemKind.Assistant, id, Presentation: new(Phase: phase, RunId: run));
        var rows = ChatToolActivityPresentation.Project(
        [
            Item("user", ChatTimelineItemKind.User),
            Reply("answer-a", "a"),
            Tool("tool-b", "exec") with { ToolRunId = "b" },
            Reply("answer-b", "b"),
            Reply("note-b", "b", "commentary"),
            Reply("cron", null) with
            {
                Presentation = new(RunId: "cron-run", AutomationKind: "cron", AutomationJobId: "j", AutomationRunId: "r"),
            },
            Reply("live", "c"),
        ], "session", 1);
        ChatMessagePresentation? Facts(ChatTimelineItem entry) => entry.Presentation;

        var settled = ChatTimelineAssistantRuns.Describe(rows, Facts);
        var live = ChatTimelineAssistantRuns.Describe(rows, Facts, lastRunWorking: true);

        // Back-to-back runs share one avatar, like the web transcript.
        Assert.Equal(
            ["answer-a"],
            rows.Where((_, index) => settled[index].IsStart).Select(RowId));
        // Commentary never owns the footer; the run's final answer keeps it.
        Assert.Equal(
            ["answer-a", "answer-b", "cron", "live"],
            rows.Where((_, index) => settled[index].IsEnd).Select(RowId));
        // A run still working has no footer yet, so text arriving below never moves it.
        Assert.Equal(
            ["answer-a", "answer-b", "cron"],
            rows.Where((_, index) => live[index].IsEnd).Select(RowId));
    }

    [Fact]
    public void AssistantRuns_FooterFollowsEveryRunInsideMergedGroupsAndOnlyTheLiveRunWaits()
    {
        ChatTimelineItem Reply(string id, string run) =>
            new(id, ChatTimelineItemKind.Assistant, id, Presentation: new(RunId: run));
        ChatMessagePresentation? Facts(ChatTimelineItem entry) => entry.Presentation;
        string[] Footers(IReadOnlyList<ChatToolActivityRow> rows) =>
            [.. rows.Where((_, index) => ChatTimelineAssistantRuns.Describe(rows, Facts, lastRunWorking: true)[index].IsEnd)
                .Select(RowId)];

        // A later run's tool merged into the earlier run's group still ends that earlier run.
        var merged = ChatToolActivityPresentation.Project(
        [
            Item("user", ChatTimelineItemKind.User),
            Reply("answer-a", "a"),
            Tool("tool-a", "exec") with { ToolRunId = "a" },
            Tool("tool-b", "exec", result: ChatToolCallStatus.InProgress) with { ToolRunId = "b" },
        ], "session", 1);
        Assert.Equal(["user", "answer-a", "tool-a"], merged.Select(RowId));
        Assert.Equal(["answer-a"], Footers(merged));

        // The run with the running tool waits; a settled background reply below it keeps its footer.
        Assert.Equal(["background"], Footers(ChatToolActivityPresentation.Project(
        [
            Item("user", ChatTimelineItemKind.User),
            Tool("running", "exec", result: ChatToolCallStatus.InProgress) with { ToolRunId = "a" },
            Reply("background", "b") with { IsBackgroundFinal = true },
        ], "session", 1)));

        // A streaming answer above a permission prompt is still working, so it has no footer yet.
        Assert.Empty(Footers(ChatToolActivityPresentation.Project(
        [
            Item("user", ChatTimelineItemKind.User),
            Reply("streaming", "a") with { IsStreaming = true },
            Item("permission", ChatTimelineItemKind.PermissionRequest),
        ], "session", 1)));
    }

    [Theory]
    [InlineData("""{"childSessionKey":"agent:main:subagent:a","childSessionKey":"agent:main:subagent:b"}""")]
    [InlineData("""{"childSessionKey":"\ud800"}""")]
    [InlineData("""{"childSessionKey":""")]
    public void RowView_MalformedSpawnOutputNeverBreaksRenderingOrCounting(string output)
    {
        var launch = Tool("spawn", "sessions_spawn") with
        {
            ToolInputDetails = new("""{"label":"Telegram check"}"""),
            ToolOutput = output,
        };

        var view = ChatToolRowPresentation.Resolve(launch, []);
        var summary = ChatToolActivityPresentation.Summarize([launch, Tool("read", "read_file")]);

        Assert.Equal("Telegram check", view.Subagent!.Label);
        Assert.Equal("1 read · 1 subagent", Format(summary));
    }

    [Fact]
    public void Summarize_KeepsAWrapperWhoseNestedCallsAreAllRoutine()
    {
        var poll = Linked("poll", "process", "r", "p", parent: "w") with
        {
            ToolInputDetails = new("""{"action":"poll"}"""),
        };

        var summary = ChatToolActivityPresentation.Summarize(
            [Linked("wrapper", "exec", "r", "w"), poll, Tool("read", "read_file")]);

        Assert.Equal("1 command · 1 read", Format(summary));
    }

    [Fact]
    public void RowView_CutPatchFallsBackInsteadOfAPartialFileSummary()
    {
        var cut = "*** Begin Patch\n*** Add File: a.md\n+hello\n*** Add File: b.md\n+wo\u2026[truncated]";

        Assert.True(ChatToolRowPresentation.Resolve(Tool("p", "apply_patch") with { ToolInputDetails = new(cut) }).IsEmpty);
    }

    [Theory]
    [InlineData("exec", """{"title":"Run the tests"}""", "Run the tests", null)]
    [InlineData("browser", """{"title":"Click save"}""", null, "click")]
    [InlineData("exec", """{}""", null, "npm test")]
    public void RowView_TitlesOnlyShellToolsAndStripsShellPreambles(
        string tool, string details, string? title, string? command)
    {
        var raw = tool == "exec" ? "cd /repo && export CI=1; npm test" : "click";
        var entry = Tool("c", tool, Args(("command", raw))) with { ToolInputDetails = new(details, raw) };

        var view = ChatToolRowPresentation.Resolve(entry);

        Assert.Equal((title, title is null ? command : null), (view.Title, view.Command));
    }

    [Theory]
    [InlineData("cd /repo && npm test", "npm test")]
    [InlineData("set -e; export A=1; unset B; pnpm lint", "pnpm lint")]
    [InlineData("echo 'a && b'", "echo 'a && b'")]
    [InlineData("cd /repo || exit 1", "cd /repo || exit 1")]
    [InlineData("cd /repo", "cd /repo")]
    public void StripShellPreamble_MatchesTheWebRules(string command, string expected) =>
        Assert.Equal(expected, ChatToolRowPresentation.StripShellPreamble(command));

    [Fact]
    public void ExpansionState_GroupInheritsTheChoiceMadeOnTheRowItGrewFrom()
    {
        var state = new ChatToolActivityExpansionState();
        state.SetExplicit("tool-row", true, 0);

        Assert.True(state.IsExpanded("group", 0, "tool-row"));
        state.SetExplicit("group", false, 0);
        Assert.False(state.IsExpanded("group", 0, "tool-row"));
        Assert.False(state.IsExpanded("other-group", 0, null));
    }

    [Fact]
    public void ExpansionState_RemembersARowWasOpenedAfterItClosesUntilCollapseAll()
    {
        var state = new ChatToolActivityExpansionState();
        Assert.False(state.HasChoice("row", 0));

        state.SetExplicit("row", true, 0);
        state.SetExplicit("row", false, 0);

        // A closed row the user opened keeps its built details for reopening.
        Assert.False(state.IsExpanded("row", 0));
        Assert.True(state.HasChoice("row", 0));
        Assert.False(state.HasChoice("row", 1));
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
        Assert.Equal("1 command · 1 read · 1 failed", Format(summary));
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
