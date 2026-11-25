using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatNoticePresentationTests
{
    private static string English(string key, string fallback) => fallback;

    private static ChatMessagePresentation InternalUser(string? sourceTool, string? pluginName = null) =>
        new(ProvenanceKind: "internal_system", SourceTool: sourceTool, SourceRole: "user", PluginSteeringName: pluginName);

    private static readonly ChatSkillChangeNotice LearnedSkills = new("main", "review-1",
        [new ChatSkillChange("pdf", ChatSkillChangeAction.Created, " Extract tables ")]);

    public static TheoryData<ChatMessagePresentation?, string?, ChatNoticeKind> Classifications => new()
    {
        { null, "Hello", ChatNoticeKind.None },
        { new(SourceRole: "assistant"), "Learned pdf", ChatNoticeKind.None },
        { new(RunFailure: new("r", "boom"), SkillChanges: LearnedSkills), "", ChatNoticeKind.RunFailure },
        { new(SkillChanges: LearnedSkills), "", ChatNoticeKind.SkillLearned },
        { new(LegacySkillNotice: true), "Learned pdf", ChatNoticeKind.LegacySkill },
        { new(LegacySkillNotice: true), " ", ChatNoticeKind.Hidden },
        { new(ModelFallback: new("r", "a/x", "b/y")), "", ChatNoticeKind.ModelFallback },
        { InternalUser("restart"), "[System] Gateway restarted", ChatNoticeKind.Hidden },
        { InternalUser("cli_harness_context"), "[System] Workspace rules", ChatNoticeKind.System },
        { InternalUser("cli_harness_context"), "[System] ", ChatNoticeKind.Hidden },
        { InternalUser("cron_failure_repair"), "", ChatNoticeKind.System },
        { InternalUser(null), "[System] Wake up", ChatNoticeKind.System },
        { new(ProvenanceKind: "internal_system", SourceTool: "cli_harness_context", SourceRole: "assistant"), "x", ChatNoticeKind.None },
        // Independent scheduled turns keep their automation rendering.
        { new(ProvenanceKind: "internal_system", SourceTool: "cron", SourceRole: "user", JobId: "j", ProvenanceRunId: "r",
            SourceSessionKey: "s"), "Run report", ChatNoticeKind.None },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Classify_SelectsNoticesOnlyFromTypedGatewayFacts(
        ChatMessagePresentation? presentation, string? text, ChatNoticeKind expected) =>
        Assert.Equal(expected, ChatNoticePresentation.Classify(presentation, text));

    [Fact]
    public void Classify_IgnoresNoticeLikeTextWithoutTypedFacts()
    {
        Assert.Equal(ChatNoticeKind.None, ChatNoticePresentation.Classify(new(SourceRole: "user"), "[System] Gateway restarted"));
        Assert.Equal(ChatNoticeKind.None, ChatNoticePresentation.Classify(new(SourceRole: "assistant"), "💡 Learned pdf"));
    }

    [Fact]
    public void EntryText_IsThePlainCopyableNoticeText()
    {
        Assert.Equal("pdf, git", ChatNoticePresentation.EntryText(ChatNoticeKind.SkillLearned,
            new(SkillChanges: LearnedSkills with { Skills = [LearnedSkills.Skills[0], new("git", ChatSkillChangeAction.Archived)] }), ""));
        Assert.Equal("a/x → b/y", ChatNoticePresentation.EntryText(ChatNoticeKind.ModelFallback,
            new(ModelFallback: new("r", "a/x", "b/y")), ""));
        Assert.Equal("boom", ChatNoticePresentation.EntryText(ChatNoticeKind.RunFailure, new(RunFailure: new("r", "boom")), "x"));
        Assert.Equal("Workspace rules", ChatNoticePresentation.EntryText(ChatNoticeKind.System,
            InternalUser("cli_harness_context"), "[System] Workspace rules"));
    }

    [Theory]
    [InlineData("cli_harness_context", "Rules", "System · injected context", "Rules", true, false)]
    [InlineData("cron_failure_repair", "ignored", "System · automation recovery: Automation failed.", "", false, true)]
    [InlineData(null, "Wake up", "System", "Wake up", false, true)]
    public void ResolveSystem_MatchesWebLabelsAndCollapse(
        string? sourceTool, string input, string label, string text, bool collapsed, bool startsTurn)
    {
        var presentation = InternalUser(sourceTool);

        Assert.Equal(new ChatSystemNoticeView(label, text, ChatSystemNoticeIcon.Cpu, collapsed, ShowTimestamp: false),
            ChatNoticePresentation.ResolveSystem(presentation, "[System] " + input, English));
        Assert.Equal(startsTurn, ChatNoticePresentation.StartsTurn(presentation));
    }

    [Fact]
    public void ResolveSystem_PluginSteeringUsesPluginNameAndCompass()
    {
        var view = ChatNoticePresentation.ResolveSystem(InternalUser("plugin:focus", "Focus Coach"), "Stay on task", English);

        Assert.Equal(new ChatSystemNoticeView("Focus Coach", "Stay on task", ChatSystemNoticeIcon.Compass, true, true), view);
        Assert.Equal("focus", ChatNoticePresentation.ResolveSystem(InternalUser("plugin:focus"), "x", English)!.Label);
        Assert.False(ChatNoticePresentation.StartsTurn(InternalUser("plugin:focus")));
        Assert.Null(ChatNoticePresentation.ResolveSystem(new(SkillChanges: LearnedSkills), "x", English));
    }

    [Fact]
    public void ModelFallback_FormatsDirectionAndReadableReason()
    {
        Assert.Equal("Model fallback: a/x → b/y",
            ChatNoticePresentation.FallbackLabel(new("r", "a/x", "b/y"), English));
        Assert.Equal("Returned to selected model: b/y → a/x",
            ChatNoticePresentation.FallbackLabel(new("r", "b/y", "a/x", Restored: true), English));
        Assert.Equal("Reason: rate limit",
            ChatNoticePresentation.FallbackReason(new("r", "a", "b", " rate_limit "), English));
        Assert.Null(ChatNoticePresentation.FallbackReason(new("r", "a", "b", " "), English));
    }

    [Theory]
    [InlineData("anthropic", "auth", "anthropic didn’t accept the saved credentials")]
    [InlineData("anthropic", "auth_permanent", "anthropic didn’t accept the saved credentials")]
    [InlineData("openai", "billing", "openai reports a billing problem")]
    [InlineData(null, "auth", "Couldn’t finish this reply")]
    [InlineData("openai", "rate_limit", "Couldn’t finish this reply")]
    public void RunFailureTitle_NamesOnlyActionableProviderCauses(string? provider, string? reason, string expected) =>
        Assert.Equal(expected, ChatNoticePresentation.RunFailureTitle(new("r", "s", provider, reason), English));

    [Fact]
    public void RunFailureLead_RepeatsOnlyAConciseDistinctFirstLine()
    {
        Assert.Equal("Request failed", ChatNoticePresentation.RunFailureLead("Request failed\r\nstack trace"));
        Assert.Null(ChatNoticePresentation.RunFailureLead("  Request failed  "));
        Assert.Null(ChatNoticePresentation.RunFailureLead(
            new string('x', ChatNoticePresentation.RunFailureLeadMaxLength + 1) + "\ndetail"));
    }

    [Fact]
    public void Skill_AccessibleNameIncludesVerbAndTrimmedSummary()
    {
        Assert.Equal("Created pdf: Extract tables", ChatNoticePresentation.SkillAccessibleName(LearnedSkills.Skills[0], English));
        Assert.Equal("Archived git",
            ChatNoticePresentation.SkillAccessibleName(new("git", ChatSkillChangeAction.Archived, " "), English));
        Assert.Null(ChatNoticePresentation.SkillToolTip(new("git", ChatSkillChangeAction.Restored)));
    }

    [Fact]
    public void RunFailureStreaks_CollapseIntoTheLatestUntilRealOperatorInput()
    {
        ChatTimelineItem Failure(string id, string summary) =>
            new(id, ChatTimelineItemKind.Status, summary, Presentation: new(RunFailure: new(id, summary)));
        ChatTimelineItem User(string id, string? provenance = null) =>
            new(id, ChatTimelineItemKind.User, "go", Presentation: new(ProvenanceKind: provenance));

        var streaks = ChatNoticePresentation.ProjectRunFailureStreaks(
        [
            Failure("f1", "boom"),
            User("u1"),
            Failure("f2", "boom"),
            User("u2", "inter_session"),
            Failure("f3", "boom"),
            Failure("f4", "other"),
            Failure("f5", "other"),
        ], metadata: null);

        Assert.Equal(new[] { "f2", "f4" }, streaks.HiddenEntryIds.Order(StringComparer.Ordinal));
        Assert.Equal(1, streaks.RepeatCount("f1"));
        Assert.Equal(2, streaks.RepeatCount("f3"));
        Assert.Equal(2, streaks.RepeatCount("f5"));
        Assert.Same(ChatRunFailureStreaks.Empty,
            ChatNoticePresentation.ProjectRunFailureStreaks([Failure("a", "x"), User("u"), Failure("b", "x")], null));
    }
}

public sealed class ChatSkillUndoTrackerTests
{
    private const string StepTimeoutVariable = "OPENCLAW_TRAY_TEST_SKILL_UNDO_TIMEOUT_SECONDS";
    // Hang guard only: completions run on the shared thread pool, which a loaded runner can starve.
    private static readonly TimeSpan StepTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(StepTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);

    private static readonly ChatSkillChangeNotice Notice = new("main", "review-1",
        [new ChatSkillChange("pdf", ChatSkillChangeAction.Created)]);

    [Fact]
    public async Task UndoAsync_IsSingleFlightAndUndoneIsSticky()
    {
        var calls = 0;
        var reply = new TaskCompletionSource<ChatSkillUndoOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new ChatSkillUndoTracker(() => true, (_, _) => { calls++; return reply.Task; });

        var first = tracker.UndoAsync(Notice);
        var second = tracker.UndoAsync(Notice with { Skills = [.. Notice.Skills] });

        Assert.Same(first, second);
        Assert.Same(first, tracker.PendingTask(Notice));
        Assert.Equal(ChatSkillUndoStatus.Pending, tracker.StatusOf(Notice));
        reply.SetResult(ChatSkillUndoOutcome.Undone);
        Assert.Equal(ChatSkillUndoStatus.Undone, await first.WaitAsync(StepTimeout));
        Assert.Equal(ChatSkillUndoStatus.Undone, await tracker.UndoAsync(Notice));
        Assert.Null(tracker.PendingTask(Notice));
        Assert.Equal(1, calls);
        Assert.Equal(ChatSkillUndoStatus.Idle, tracker.StatusOf(Notice with { RunId = "review-2" }));
    }

    [Theory]
    [InlineData(ChatSkillUndoOutcome.Failed, false)]
    [InlineData(ChatSkillUndoOutcome.Unavailable, false)]
    [InlineData(ChatSkillUndoOutcome.Failed, true)]
    public async Task UndoAsync_FailureOrUnknownOutcomeAllowsAManualRetry(ChatSkillUndoOutcome outcome, bool throws)
    {
        var calls = 0;
        var tracker = new ChatSkillUndoTracker(() => true, (_, _) =>
        {
            calls++;
            if (calls == 1 && throws)
                throw new TimeoutException();
            return Task.FromResult(calls == 1 ? outcome : ChatSkillUndoOutcome.Undone);
        });

        Assert.Equal(ChatSkillUndoStatus.Failed, await tracker.UndoAsync(Notice).WaitAsync(StepTimeout));
        Assert.Equal(ChatSkillUndoStatus.Failed, tracker.StatusOf(Notice));
        Assert.Equal(ChatSkillUndoStatus.Undone, await tracker.UndoAsync(Notice).WaitAsync(StepTimeout));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void CanUndo_ReflectsTheCurrentBridgeGate()
    {
        var supported = false;
        var tracker = new ChatSkillUndoTracker(() => supported, (_, _) => Task.FromResult(ChatSkillUndoOutcome.Undone));

        Assert.False(tracker.CanUndo);
        supported = true;
        Assert.True(tracker.CanUndo);
    }
}
