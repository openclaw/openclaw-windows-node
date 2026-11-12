using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared.Cron;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class CronModelTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string FullJob = """
    {
      "id": "brief", "agentId": "main", "name": "brief", "displayName": "Morning brief", "description": "Digest",
      "enabled": true, "deleteAfterRun": false, "createdAtMs": 1, "updatedAtMs": 2, "configRevision": "rev-7",
      "sessionKey": "agent:main:main", "sessionTarget": "isolated", "wakeMode": "next-heartbeat", "idleOnly": true,
      "schedule": { "kind": "cron", "expr": "0 7 * * 1-5", "tz": "America/Los_Angeles", "staggerMs": 0 },
      "activeHours": { "start": "08:00", "end": "24:00", "timezone": "user" },
      "trigger": { "script": "return json({ fire: true })", "once": true },
      "payload": { "kind": "agentTurn", "message": "Summarize", "model": "fixture/fast", "thinking": "low", "timeoutSeconds": 90,
                   "lightContext": true, "includeReasoning": false, "skipIfScratchEmpty": true, "toolsAllow": ["read"], "fallbacks": ["b"] },
      "delivery": { "mode": "announce", "channel": "slack", "to": "#daily", "accountId": "work", "threadId": 42, "bestEffort": true,
                    "target": "owner", "directPolicy": "block", "completionDestination": { "mode": "webhook", "to": "https://x.test" } },
      "failureAlert": { "after": 3, "cooldownMs": 1500, "channel": "slack", "to": "#ops", "mode": "announce", "accountId": "a", "includeSkipped": true },
      "state": { "nextRunAtMs": 100, "runningAtMs": 5, "lastRunAtMs": 50, "lastStatus": "error", "lastError": "boom",
                 "consecutiveErrors": 4, "autoDisabled": { "reason": "schedule-errors", "atMs": 60, "consecutiveErrors": 4 },
                 "lastDeliveryStatus": "not-delivered", "triggerEvalCount": 9, "lastTriggerFireAtMs": 40 },
      "deliveryPreview": { "label": "Slack", "detail": "#daily" }
    }
    """;

    [Fact]
    public void ParseJob_ReadsEveryDisplayedField()
    {
        var job = CronJsonReader.ParseJob(Json(FullJob))!;

        Assert.Equal("Morning brief", job.Title);
        Assert.Equal("main", job.AgentId);
        Assert.Equal("rev-7", job.ConfigRevision);
        Assert.Equal(CronScheduleKind.Cron, job.Schedule.Kind);
        Assert.Equal("America/Los_Angeles", job.Schedule.Tz);
        Assert.Equal(0, job.Schedule.StaggerMs);
        Assert.Equal(new CronActiveHours("08:00", "24:00", "user"), job.ActiveHours);
        Assert.Equal(new CronTrigger("return json({ fire: true })", true), job.Trigger);
        Assert.Equal("next-heartbeat", job.WakeMode);
        Assert.Equal(CronPayloadKind.AgentTurn, job.Payload.Kind);
        Assert.Equal("Summarize", job.Payload.Text);
        Assert.Equal(90, job.Payload.TimeoutSeconds);
        Assert.Equal(["read"], job.Payload.ToolsAllow);
        Assert.Equal("42", job.Delivery!.ThreadId);
        Assert.Equal("owner", job.Delivery.Target);
        Assert.NotNull(job.Delivery.CompletionDestination);
        Assert.Equal(3, job.FailureAlert!.After);
        Assert.Equal("error", job.State.LastRunStatus);
        Assert.True(job.IsRunning);
        Assert.True(job.IsAutoDisabled);
        Assert.True(job.State.AutoDisabled!.IsScheduleErrors);
        Assert.True(job.IsFailing);
        Assert.Equal("Slack", job.DeliveryPreview!.Label);
    }

    [Theory]
    [InlineData("""{"id":"a","name":"a","schedule":"0 1 * * *","payload":{"kind":"systemEvent","text":"x"},"enabled":false,"failureAlert":false,"state":{"lastRunOk":true}}""")]
    public void ParseJob_ToleratesLegacyShapes(string json)
    {
        var job = CronJsonReader.ParseJob(Json(json))!;
        Assert.Equal(CronScheduleKind.Cron, job.Schedule.Kind);
        Assert.Equal("0 1 * * *", job.Schedule.Expr);
        Assert.True(job.FailureAlert!.Disabled);
        Assert.Equal("ok", job.State.LastRunStatus);
        Assert.Equal(CronPayloadKind.SystemEvent, job.Payload.Kind);
    }

    [Fact]
    public void ParseJob_SkipsRowsWithoutIdAndUnwrapsDeclarativeAddResults()
    {
        Assert.Null(CronJsonReader.ParseJob(Json("""{"name":"no id"}""")));
        var wrapped = CronJsonReader.ParseJob(Json($$"""{"created":true,"job":{{FullJob}}}"""));
        Assert.Equal("brief", wrapped!.Id);
    }

    [Fact]
    public void ParseSchedule_ReadsEventAndProcessKindsAsWebManaged()
    {
        var mcp = CronJsonReader.ParseSchedule(Json("""{"kind":"event","source":"mcp-events","options":{"server":"github","name":"pr.opened","arguments":{"repo":"a/b"}}}"""));
        Assert.True(mcp.IsMcpEvent);
        Assert.True(mcp.IsWebManaged);
        Assert.Equal("github", mcp.EventServer);
        Assert.Contains("\"repo\"", mcp.EventArgumentsJson);
        var plugin = CronJsonReader.ParseSchedule(Json("""{"kind":"event","source":"calendar","options":{}}"""));
        Assert.True(plugin.IsWebManaged);
        var stream = CronJsonReader.ParseSchedule(Json("""{"kind":"stream","command":["tail","-f"]}"""));
        Assert.Equal(CronScheduleKind.Stream, stream.Kind);
        Assert.Equal("tail -f", stream.Command);
        Assert.True(stream.IsWebManaged);
        Assert.False(CronJsonReader.ParseSchedule(Json("""{"kind":"cron","expr":"0 9 * * 1-5"}""")).IsWebManaged);
        Assert.False(CronJsonReader.ParseSchedule(Json("""{"kind":"every","everyMs":60000}""")).IsWebManaged);
        Assert.False(CronJsonReader.ParseSchedule(Json("""{"kind":"at","at":"2030-01-01T00:00:00Z"}""")).IsWebManaged);
    }

    [Fact]
    public void ParseJob_UsesTheGatewayResolvedOwnerOnlyWhenNoneIsStored()
    {
        var implicitOwner = CronJsonReader.ParseJob(Json("""{"id":"a","name":"A","enabled":true,"effectiveAgentId":"main","schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}"""))!;
        Assert.Null(implicitOwner.AgentId);
        Assert.Equal("main", implicitOwner.Owner);
        // The resolved owner is display-only: a basic edit does not pin it.
        Assert.False((CronJobForm.FromJob(implicitOwner) with { Name = "B" }).BuildDefinition(implicitOwner).ContainsKey("agentId"));

        var explicitOwner = CronJsonReader.ParseJob(Json("""{"id":"a","name":"A","enabled":true,"agentId":"ops","effectiveAgentId":"main","schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}"""))!;
        Assert.Equal("ops", explicitOwner.Owner);
    }

    [Fact]
    public void ParseListPage_ComputesPagingWhenGatewayOmitsFields()
    {
        var page = CronJsonReader.ParseListPage(Json($$"""{"jobs":[{{FullJob}}],"total":7,"hasMore":true}"""), requestedOffset: 3);
        Assert.Single(page.Jobs);
        Assert.Equal(7, page.Total);
        Assert.Equal(3, page.Offset);
        Assert.Equal(4, page.NextOffset);
        var array = CronJsonReader.ParseListPage(Json($"[{FullJob}]"));
        Assert.Single(array.Jobs);
        Assert.False(array.HasMore);
        Assert.Equal(1, array.Total);
    }

    [Fact]
    public void ParseListPage_DerivesATotalOnlyFromTheFinalPage()
    {
        var partial = CronJsonReader.ParseListPage(Json($$"""{"jobs":[{{FullJob}}],"offset":0,"hasMore":true}"""));
        Assert.Null(partial.Total);
        Assert.Equal(1, partial.NextOffset);
        var cursorOnly = CronJsonReader.ParseListPage(Json($$"""{"jobs":[{{FullJob}}],"nextOffset":1}"""));
        Assert.True(cursorOnly.HasMore);
        Assert.Null(cursorOnly.Total);
        Assert.Equal(5, CronJsonReader.ParseListPage(Json($$"""{"jobs":[{{FullJob}}],"offset":4,"hasMore":false}""")).Total);
    }

    [Theory]
    [InlineData("""{"id":"a","name":"A","schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}""")]
    [InlineData("""{"id":"a","name":"A","enabled":"false","schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}""")]
    [InlineData("""{"id":"a","name":"A","enabled":null,"schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"}}""")]
    public void ParseJob_RejectsAJobWithoutAGatewayEnabledState(string json)
    {
        // An unstated enabled flag is not evidence that the job is paused.
        Assert.Null(CronJsonReader.ParseJob(Json(json)));
        var list = Assert.Throws<InvalidOperationException>(() => CronJsonReader.ParseListPage(Json($$"""{"jobs":[{{FullJob}},{{json}}],"total":2}""")));
        Assert.Equal(CronJsonReader.UnreadableJobMessage, list.Message);
    }

    [Theory]
    [InlineData("""{"total":3}""")]
    [InlineData("""{"jobs":null,"total":0}""")]
    [InlineData("""{"jobs":{},"total":0}""")]
    [InlineData("null")]
    public void ParseListPage_RejectsAnUnreadableEnvelope(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CronJsonReader.ParseListPage(Json(json)));
        Assert.Equal(CronJsonReader.UnreadableListMessage, error.Message);
    }

    [Fact]
    public void ParseRunsPage_DerivesATotalOnlyFromACompleteFinalPage()
    {
        Assert.Equal(9, CronJsonReader.ParseRunsPage(Json("""{"entries":[{"jobId":"j","runId":"r1"}],"total":9}""")).Total);
        Assert.Equal(3, CronJsonReader.ParseRunsPage(Json("""{"entries":[{"jobId":"j","runId":"r1"}],"offset":2,"hasMore":false}""")).Total);
        Assert.Null(CronJsonReader.ParseRunsPage(Json("""{"entries":[{"jobId":"j","runId":"r1"}],"hasMore":true}""")).Total);
        Assert.Null(CronJsonReader.ParseRunsPage(Json("""{"entries":[{"jobId":"j","runId":"r1"}],"nextOffset":1}""")).Total);
        // A dropped, unidentifiable entry means the page no longer says how many runs exist.
        var dropped = CronJsonReader.ParseRunsPage(Json("""{"entries":[{"jobId":"j","runId":"r1"},{"jobId":"j"}],"hasMore":false}"""));
        Assert.Single(dropped.Entries);
        Assert.Null(dropped.Total);
        var error = Assert.Throws<InvalidOperationException>(() => CronJsonReader.ParseRunsPage(Json("""{"total":2}""")));
        Assert.Equal(CronJsonReader.UnreadableRunsMessage, error.Message);
    }

    [Fact]
    public void ParseRunsAndStatus()
    {
        var runs = CronJsonReader.ParseRunsPage(Json("""
        {"entries":[{"ts":10,"jobId":"j","status":"ok","summary":"done","deliveryStatus":"delivered","runId":"r1","runAtMs":5,
          "durationMs":5,"usage":{"input_tokens":3,"output_tokens":4},"diagnostics":{"summary":"fine"},"deliverySuppressionReason":"quiet"},
          {"ts":20,"jobId":"j"},{"noJob":true}],"total":3,"offset":0,"hasMore":false}
        """));
        Assert.Equal(2, runs.Entries.Count);
        var run = runs.Entries[0];
        Assert.Equal((3L, 4L), (run.InputTokens, run.OutputTokens));
        Assert.Equal("fine", run.DiagnosticsSummary);
        Assert.Equal("r1", run.Key);
        Assert.Equal("j:20", runs.Entries[1].Key);

        var status = CronJsonReader.ParseStatus(Json("""{"enabled":false,"nextWakeAtMs":99,"jobs":4}"""));
        Assert.Equal(new CronSchedulerStatus(false, 99, 4, null), status);
    }

    [Fact]
    public void ParseRuns_LeavesAMissingTimeUnknownAndDropsUnidentifiableEntries()
    {
        var runs = CronJsonReader.ParseRunsPage(Json("""
        {"entries":[{"jobId":"j","runId":"r1","status":"ok"},{"jobId":"j","runAtMs":7},{"jobId":"j","status":"ok"}],"total":3,"offset":0,"hasMore":false}
        """));
        Assert.Equal(2, runs.Entries.Count);
        Assert.Null(runs.Entries[0].Ts);
        Assert.Null(runs.Entries[0].EffectiveRunAtMs);
        Assert.Null(runs.Entries[0].InputTokens);
        Assert.Equal(7, runs.Entries[1].EffectiveRunAtMs);
    }

    [Fact]
    public void ParseJob_LeavesOmittedGatewayFieldsUnset()
    {
        var job = CronJsonReader.ParseJob(Json("""
        {"id":"a","name":"A","enabled":false,"schedule":{"kind":"every","everyMs":60000},"payload":{"kind":"agentTurn","message":"m"},
         "state":{"autoDisabled":{}}}
        """))!;
        Assert.Null(job.SessionTarget);
        Assert.Null(job.WakeMode);
        Assert.Null(job.Delivery);
        Assert.Equal(new CronAutoDisabled(null, null, null), job.State.AutoDisabled);
        Assert.True(job.IsAutoDisabled);
    }

    [Fact]
    public void ListQuery_SendsTheAgentScopeOnlyWhenOneAgentIsFiltered()
    {
        var p = new CronListQuery { AgentId = " research ", Query = " sync ", Enabled = CronEnabledFilter.Disabled, SortBy = CronSortBy.Name, Descending = true, Offset = 50 }.ToParameters();
        Assert.Equal("research", p["agentId"]!.GetValue<string>());
        Assert.Equal("sync", p["query"]!.GetValue<string>());
        Assert.Equal("disabled", p["enabled"]!.GetValue<string>());
        Assert.False(p["includeDisabled"]!.GetValue<bool>());
        Assert.Equal("name", p["sortBy"]!.GetValue<string>());
        Assert.Equal("desc", p["sortDir"]!.GetValue<string>());
        Assert.Equal(50, p["offset"]!.GetValue<int>());
        Assert.False(p.ContainsKey("scheduleKind"));
        Assert.True(new CronListQuery { LastRunStatus = "error" }.ToParameters().ContainsKey("scheduleKind"));

        // All agents: no agent scope, and "all" status still lists paused jobs.
        var everyAgent = new CronListQuery { AgentId = null }.ToParameters();
        Assert.False(everyAgent.ContainsKey("agentId"));
        Assert.True(everyAgent["includeDisabled"]!.GetValue<bool>());
        Assert.Equal("all", everyAgent["enabled"]!.GetValue<string>());
    }

    [Fact]
    public void RunsQuery_ScopesExactJobsByIdAndOverviewsByAgent()
    {
        var job = new CronRunsQuery { JobId = "a", AgentId = "main", Statuses = ["error"], OldestFirst = true }.ToParameters();
        Assert.Equal("job", job["scope"]!.GetValue<string>());
        Assert.Equal("a", job["id"]!.GetValue<string>());
        Assert.False(job.ContainsKey("agentId"));
        Assert.Equal("asc", job["sortDir"]!.GetValue<string>());
        var all = new CronRunsQuery { AgentId = "main" }.ToParameters();
        Assert.Equal("all", all["scope"]!.GetValue<string>());
        Assert.Equal("main", all["agentId"]!.GetValue<string>());
        Assert.False(all.ContainsKey("statuses"));
        Assert.False(new CronRunsQuery().ToParameters().ContainsKey("agentId"));
    }

    [Fact]
    public void RunsQuery_SendsOnlyPropertiesTheReleasedGatewayAccepts()
    {
        // The released Gateway (v2026.11.1) validates cron.runs params as a closed object.
        // The "view" option exists only from 2026.11.5-beta and makes older Gateways reject the request.
        string[] released =
        [
            "agentId", "scope", "id", "jobId", "runId", "limit", "offset", "statuses", "status",
            "deliveryStatuses", "deliveryStatus", "query", "sortDir",
        ];
        CronRunsQuery[] queries =
        [
            new(),
            new() { AgentId = "main", Statuses = ["error", "ok"], DeliveryStatuses = ["not-delivered"], Query = "sync", OldestFirst = true, Limit = 5, Offset = 10 },
            new() { JobId = "a", RunId = "run-1", Statuses = ["skipped"] },
        ];

        foreach (var query in queries)
            Assert.All(query.ToParameters().Select(property => property.Key), key => Assert.Contains(key, released));
    }

    [Theory]
    [InlineData(CronCalendarPattern.Daily, 7, 30, "30 7 * * *")]
    [InlineData(CronCalendarPattern.Weekdays, 9, 0, "0 9 * * 1-5")]
    [InlineData(CronCalendarPattern.Weekly, 18, 5, "5 18 * * 1")]
    [InlineData(CronCalendarPattern.Monthly, 0, 0, "0 0 1 * *")]
    public void Calendar_PatternsRoundTripTheirTime(CronCalendarPattern pattern, int hour, int minute, string expr)
    {
        Assert.Equal(expr, CronCalendar.Build(pattern, new TimeSpan(hour, minute, 0)));
        Assert.Equal((pattern, new TimeSpan(hour, minute, 0)), CronCalendar.TryParse(expr));
    }

    [Theory]
    [InlineData("*/15 * * * *")]
    [InlineData("0 9 * * 1,3")]
    [InlineData("0 9 * 1 *")]
    [InlineData("0 24 * * *")]
    [InlineData("00 9 * * *")]
    [InlineData("0 9 * * * *")]
    [InlineData("")]
    public void Calendar_TreatsOtherExpressionsAsCustomRules(string expr) => Assert.Null(CronCalendar.TryParse(expr));

    [Fact]
    public void Calendar_HourlyHasNoTimeOfDay() =>
        Assert.Equal(CronCalendarPattern.Hourly, CronCalendar.TryParse(CronCalendar.Build(CronCalendarPattern.Hourly, new TimeSpan(7, 0, 0)))!.Value.Pattern);

    [Theory]
    [InlineData(null, "cron")]
    [InlineData(" ", "cron")]
    [InlineData("morning-brief", "cron?job=morning-brief")]
    [InlineData("a b/c&d#e", "cron?job=a%20b%2Fc%26d%23e")]
    public void WebPath_UsesTheDocumentedAutomationsRouteAndJobLink(string? jobId, string expected) =>
        Assert.Equal(expected, CronProtocol.WebPath(jobId));

    [Theory]
    [InlineData("30", CronDurationUnit.Minutes, 1_800_000L)]
    [InlineData("1.5", CronDurationUnit.Hours, 5_400_000L)]
    [InlineData(".001", CronDurationUnit.Seconds, 1L)]
    [InlineData("0.0005", CronDurationUnit.Seconds, null)]
    [InlineData("0", CronDurationUnit.Minutes, null)]
    [InlineData("-1", CronDurationUnit.Minutes, null)]
    [InlineData("abc", CronDurationUnit.Days, null)]
    [InlineData("\uFF13\uFF10", CronDurationUnit.Minutes, null)]
    public void Durations_ParseExactDecimals(string text, CronDurationUnit unit, long? expected) =>
        Assert.Equal(expected, CronDurations.ParseMs(text, unit));

    [Theory]
    [InlineData(86_400_000L, "1", CronDurationUnit.Days)]
    [InlineData(5_400_000L, "90", CronDurationUnit.Minutes)]
    [InlineData(1_500L, "1.5", CronDurationUnit.Seconds)]
    public void Durations_SplitIntoLargestExactUnit(long ms, string amount, CronDurationUnit unit) =>
        Assert.Equal((amount, unit), CronDurations.Split(ms));

    [Theory]
    [InlineData("0 * * * *", 300_000L)]
    [InlineData("0 */2 * * *", 300_000L)]
    [InlineData("0 0 * * * *", 300_000L)]
    [InlineData("0 9 * * *", null)]
    [InlineData("15 * * * *", null)]
    public void Durations_DefaultStaggerAppliesToTopOfHourOnly(string expr, long? expected) =>
        Assert.Equal(expected, CronDurations.DefaultStaggerMs(expr));
}

public sealed class CronJobFormTests
{
    private static CronJobForm Valid => CronJobForm.CreateDefault("main") with { Name = "N", PayloadText = "Do it" };

    [Fact]
    public void Validate_AcceptsDefaultsOnceRequiredFieldsAreFilled() => Assert.Empty(Valid.Validate());

    [Fact]
    public void Validate_RequiresNameAndPromptByPayloadKind()
    {
        var errors = CronJobForm.CreateDefault(null).Validate();
        Assert.Equal(CronFormError.NameRequired, errors[CronFormField.Name]);
        Assert.Equal(CronFormError.AgentMessageRequired, errors[CronFormField.PayloadText]);
        var timeline = (CronJobForm.CreateDefault(null) with { PayloadKind = CronPayloadKind.SystemEvent, SessionTarget = "main" }).Validate();
        Assert.Equal(CronFormError.SystemTextRequired, timeline[CronFormField.PayloadText]);
    }

    [Theory]
    [InlineData(CronScheduleKind.Every, "0", null, CronFormField.EveryAmount, CronFormError.EveryAmountInvalid)]
    [InlineData(CronScheduleKind.Cron, null, "", CronFormField.CronExpr, CronFormError.CronExprRequired)]
    [InlineData(CronScheduleKind.At, null, null, CronFormField.ScheduleAt, CronFormError.ScheduleAtInvalid)]
    public void Validate_ScheduleFields(CronScheduleKind kind, string? every, string? expr, CronFormField field, CronFormError error)
    {
        var form = Valid with { ScheduleKind = kind, EveryAmount = every ?? "30", CronExpr = expr ?? "0 7 * * *", ScheduleAt = null };
        Assert.Equal(error, form.Validate()[field]);
    }

    [Fact]
    public void Validate_TimezonesStaggerAndActiveHours()
    {
        var form = Valid with
        {
            ScheduleKind = CronScheduleKind.Cron,
            CronTz = "Not/AZone",
            StaggerAmount = "-3",
            ActiveHoursEnabled = true,
            ActiveHoursStart = "24:00",
            ActiveHoursEnd = "7:00",
            ActiveHoursTimezone = "Mars/Base",
        };
        var errors = form.Validate();
        Assert.Equal(CronFormError.TimezoneInvalid, errors[CronFormField.CronTz]);
        Assert.Equal(CronFormError.StaggerAmountInvalid, errors[CronFormField.StaggerAmount]);
        Assert.Equal(CronFormError.ActiveHoursTimeInvalid, errors[CronFormField.ActiveHoursStart]);
        Assert.Equal(CronFormError.ActiveHoursTimeInvalid, errors[CronFormField.ActiveHoursEnd]);
        Assert.Equal(CronFormError.ActiveHoursTimezoneInvalid, errors[CronFormField.ActiveHoursTimezone]);

        var ok = form with { CronTz = "Europe/Paris", StaggerAmount = "1e1", ActiveHoursStart = "22:00", ActiveHoursEnd = "24:00", ActiveHoursTimezone = "user" };
        Assert.Empty(ok.Validate());
        Assert.True(CronJobForm.IsValidTimezone("", allowAliases: false));
        Assert.False(CronJobForm.IsValidTimezone("local", allowAliases: false));
    }

    [Fact]
    public void Validate_TriggersDeliveryAndAlerts()
    {
        var trigger = (Valid with { TriggerEnabled = true, EveryAmount = "10", EveryUnit = CronDurationUnit.Seconds }).Validate();
        Assert.Equal(CronFormError.TriggerIntervalTooShort, trigger[CronFormField.EveryAmount]);
        Assert.Equal(CronFormError.TriggerScriptRequired, trigger[CronFormField.TriggerScript]);
        Assert.Equal(CronFormError.TriggerScheduleUnsupported,
            (Valid with { TriggerEnabled = true, TriggerScript = "x", ScheduleKind = CronScheduleKind.At, ScheduleAt = DateTimeOffset.Now }).Validate()[CronFormField.TriggerScript]);

        var evt = (Valid with { ScheduleKind = CronScheduleKind.Event, PayloadKind = CronPayloadKind.SystemEvent, SessionTarget = "main" }).Validate();
        Assert.Equal(CronFormError.EventAgentTurnRequired, evt[CronFormField.PayloadText]);

        Assert.Equal(CronFormError.WebhookUrlRequired, (Valid with { DeliveryMode = "webhook" }).Validate()[CronFormField.DeliveryTo]);
        Assert.Equal(CronFormError.WebhookUrlInvalid, CronJobForm.ValidateWebhookUrl("ftp://x.test"));
        Assert.Equal(CronFormError.WebhookUrlInvalid, CronJobForm.ValidateWebhookUrl("https://user:pw@x.test/hook"));
        Assert.Null(CronJobForm.ValidateWebhookUrl("https://x.test/hook"));
        Assert.Equal(CronFormError.DeliveryModeRequired, (Valid with { DeliveryMode = "" }).Validate()[CronFormField.DeliveryMode]);
        Assert.DoesNotContain(CronFormField.DeliveryMode, (Valid with { DeliveryMode = "", KeepSavedDelivery = true }).Validate().Keys);
        Assert.Equal(CronFormError.TimeoutInvalid, (Valid with { TimeoutSeconds = "-1" }).Validate()[CronFormField.TimeoutSeconds]);

        var alerts = (Valid with { FailureAlertMode = CronFailureAlertMode.Custom, FailureAlertAfter = "0", FailureAlertCooldownSeconds = "-2" }).Validate();
        Assert.Equal(CronFormError.FailureAlertAfterInvalid, alerts[CronFormField.FailureAlertAfter]);
        Assert.Equal(CronFormError.FailureAlertCooldownInvalid, alerts[CronFormField.FailureAlertCooldown]);
    }

    [Fact]
    public void Normalize_KeepsTimelineMessagesOnTheMainSession()
    {
        var form = Valid with { PayloadKind = CronPayloadKind.SystemEvent, SessionTarget = "isolated", DeliveryMode = "announce" };
        var normalized = form.Normalize();
        Assert.Equal("main", normalized.SessionTarget);
        Assert.Equal("none", normalized.DeliveryMode);
        var retargeted = form.Normalize(sessionTargetChanged: true);
        Assert.Equal(CronPayloadKind.AgentTurn, retargeted.PayloadKind);
        Assert.Equal("isolated", retargeted.SessionTarget);
    }

    [Fact]
    public void MainSessionSelectionSerializesSystemEventTextAndRetargetsWithoutLosingThePrompt()
    {
        var main = (Valid with { SessionTarget = "main", DeliveryMode = "announce" }).Normalize(sessionTargetChanged: true);
        Assert.Equal(CronPayloadKind.SystemEvent, main.PayloadKind);
        Assert.Equal("Do it", main.PayloadText);
        var request = JsonDocument.Parse(main.BuildDefinition().ToJsonString()).RootElement;
        Assert.Equal("main", request.GetProperty("sessionTarget").GetString());
        var payload = request.GetProperty("payload");
        Assert.Equal(new[] { "kind", "text" }, payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("systemEvent", payload.GetProperty("kind").GetString());
        Assert.Equal("Do it", payload.GetProperty("text").GetString());
        Assert.Equal("none", request.GetProperty("delivery").GetProperty("mode").GetString());
        var isolated = (main with { SessionTarget = "isolated" }).Normalize(sessionTargetChanged: true);
        Assert.Equal(CronPayloadKind.AgentTurn, isolated.PayloadKind);
        Assert.Equal("Do it", isolated.BuildDefinition()["payload"]!["message"]!.GetValue<string>());
        // A locked web-managed script is not converted merely because its target is Main.
        var locked = (Valid with { PayloadKind = CronPayloadKind.Script, PayloadLocked = true, SessionTarget = "main" }).Normalize(true);
        Assert.Equal(CronPayloadKind.Script, locked.PayloadKind);
    }

    [Fact]
    public void WithScheduleKind_AppliesOneShotDefaults()
    {
        var once = Valid.WithScheduleKind(CronScheduleKind.At);
        Assert.True(once.DeleteAfterRun);
        Assert.NotNull(once.ScheduleAt);
        Assert.False(once.WithScheduleKind(CronScheduleKind.Every).DeleteAfterRun);
    }

    [Fact]
    public void BuildDefinition_CreateOmitsBlanksAndBuildsEverySchedule()
    {
        var job = (Valid with
        {
            ScheduleKind = CronScheduleKind.Cron,
            CronExpr = "0 * * * *",
            CronTz = "UTC",
            DeliveryMode = "announce",
            DeliveryChannel = "slack",
            DeliveryTo = "#daily",
            FailureAlertMode = CronFailureAlertMode.Custom,
            FailureAlertAfter = "3",
            FailureAlertCooldownSeconds = "1.5",
        }).BuildDefinition();
        Assert.Equal("main", job["agentId"]!.GetValue<string>());
        Assert.False(job.ContainsKey("sessionKey"));
        Assert.False(job.ContainsKey("deleteAfterRun"));
        var schedule = job["schedule"]!.AsObject();
        Assert.Equal("cron", schedule["kind"]!.GetValue<string>());
        Assert.Equal("UTC", schedule["tz"]!.GetValue<string>());
        Assert.Equal(CronProtocol.DefaultTopOfHourStaggerMs, schedule["staggerMs"]!.GetValue<long>());
        Assert.Equal("slack", job["delivery"]!["channel"]!.GetValue<string>());
        Assert.Equal(1500, job["failureAlert"]!["cooldownMs"]!.GetValue<long>());
        var payload = job["payload"]!.AsObject();
        Assert.Equal("agentTurn", payload["kind"]!.GetValue<string>());
        Assert.False(payload.ContainsKey("model"));

        var at = new DateTimeOffset(2030, 1, 2, 3, 4, 59, TimeSpan.Zero);
        var once = (Valid with { ScheduleKind = CronScheduleKind.At, ScheduleAt = at, DeleteAfterRun = true }).BuildDefinition();
        Assert.Equal("2030-01-02T03:04:00.000Z", once["schedule"]!["at"]!.GetValue<string>());
        Assert.True(once["deleteAfterRun"]!.GetValue<bool>());
        Assert.Equal(new JsonObject { ["kind"] = "every", ["everyMs"] = 1_800_000L }.ToJsonString(), Valid.BuildSchedule()!.ToJsonString());
        Assert.Equal(0, (Valid with { ScheduleKind = CronScheduleKind.Cron, ScheduleExact = true }).BuildSchedule()!["staggerMs"]!.GetValue<long>());
    }

    [Fact]
    public void BuildDefinition_UpdateClearsRemovedValuesAndKeepsUnchangedSchedules()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r","sessionKey":"k","idleOnly":true,
         "schedule":{"kind":"cron","expr":"5 7 * * *","staggerMs":120000},
         "activeHours":{"start":"09:00","end":"17:00"},"trigger":{"script":"x"},
         "payload":{"kind":"agentTurn","message":"m","model":"p/1","thinking":"low","timeoutSeconds":5,"toolsAllow":["a"]},
         "delivery":{"mode":"announce","channel":"slack","to":"#a","accountId":"acct","threadId":"t"},
         "failureAlert":{"after":2},"pacing":{"min":"1m"}}
        """).RootElement)!;
        var form = CronJobForm.FromJob(job) with
        {
            SessionKey = "",
            IdleOnly = false,
            ActiveHoursEnabled = false,
            TriggerEnabled = false,
            PayloadModel = "",
            TimeoutSeconds = "",
            DeliveryTo = "",
            DeliveryAccountId = "",
            FailureAlertMode = CronFailureAlertMode.Inherit,
        };
        var patch = form.BuildDefinition(job);
        Assert.False(patch.ContainsKey("schedule"));
        Assert.False(patch.ContainsKey("pacing"));
        Assert.Null(patch["sessionKey"]);
        Assert.True(patch.ContainsKey("sessionKey"));
        Assert.False(patch["idleOnly"]!.GetValue<bool>());
        Assert.Null(patch["activeHours"]);
        Assert.Null(patch["trigger"]);
        Assert.True(patch.ContainsKey("failureAlert"));
        Assert.Null(patch["failureAlert"]);
        var payload = patch["payload"]!.AsObject();
        Assert.True(payload.ContainsKey("model") && payload["model"] is null);
        Assert.True(payload.ContainsKey("timeoutSeconds") && payload["timeoutSeconds"] is null);
        Assert.Equal("low", payload["thinking"]!.GetValue<string>());
        Assert.False(payload.ContainsKey("toolsAllow"));
        var delivery = patch["delivery"]!.AsObject();
        Assert.True(delivery.ContainsKey("to") && delivery["to"] is null);
        Assert.True(delivery.ContainsKey("accountId") && delivery["accountId"] is null);
        Assert.Equal("t", delivery["threadId"]!.GetValue<string>());

        var changed = (form with { CronExpr = "5 8 * * *", StaggerAmount = "" }).BuildDefinition(job);
        Assert.Equal(0, changed["schedule"]!["staggerMs"]?.GetValue<long>() ?? 0);

        // Editing only the time zone keeps the expression and its timing window.
        var zoned = (form with { CronTz = "Europe/Paris" }).BuildDefinition(job);
        Assert.Equal("5 7 * * *", zoned["schedule"]!["expr"]!.GetValue<string>());
        Assert.Equal(120_000, zoned["schedule"]!["staggerMs"]!.GetValue<long>());
        Assert.False(changed.ContainsKey("pacing"));

        var disabled = (form with { FailureAlertMode = CronFailureAlertMode.Disabled }).BuildDefinition(job);
        Assert.False(disabled["failureAlert"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildDefinition_ClonesCarryRestrictionsAndScheduleButNotRevisionNulls()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"schedule":{"kind":"every","everyMs":90000,"anchorMs":5},
         "payload":{"kind":"agentTurn","message":"m","toolsAllow":["a"],"fallbacks":["f"]},"pacing":{"min":"1m"}}
        """).RootElement)!;
        var clone = CronJobForm.FromClone(job, "J (copy)").BuildDefinition(cloneSource: job);
        Assert.Equal("J (copy)", clone["name"]!.GetValue<string>());
        Assert.Equal(5, clone["schedule"]!["anchorMs"]!.GetValue<long>());
        Assert.Equal("1m", clone["pacing"]!["min"]!.GetValue<string>());
        Assert.Equal("a", clone["payload"]!["toolsAllow"]![0]!.GetValue<string>());
        Assert.Equal("f", clone["payload"]!["fallbacks"]![0]!.GetValue<string>());
        Assert.DoesNotContain(clone, kv => kv.Value is null);
    }

    [Fact]
    public void FromJob_RoundTripsReadOnlyPayloadsWithoutRewritingThem()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"schedule":{"kind":"stream","command":["tail"]},
         "payload":{"kind":"command","argv":["echo","hi"]}}
        """).RootElement)!;
        var form = CronJobForm.FromJob(job);
        Assert.True(form.PayloadLocked);
        Assert.Equal("echo hi", form.PayloadText);
        var patch = form.BuildDefinition(job);
        Assert.False(patch.ContainsKey("payload"));
        Assert.False(patch.ContainsKey("schedule"));

        // A clone cannot recreate a command payload, so it starts as an empty assistant task.
        var clone = CronJobForm.FromClone(job, "J (copy)");
        Assert.False(clone.PayloadLocked);
        Assert.Equal(CronPayloadKind.AgentTurn, clone.PayloadKind);
        Assert.Equal("", clone.PayloadText);
    }

    [Theory]
    [InlineData("""{"kind":"event","source":"plugin-x","eventName":"ping"}""")]
    [InlineData("""{"kind":"event","source":"mcp-events","options":{"server":"github","name":"pr.opened","arguments":{"repo":"a/b"}}}""")]
    [InlineData("""{"kind":"stream","command":["tail","-f"]}""")]
    public void WebManagedSchedules_ArePreservedOnEditAndCopiedOnClone(string schedule)
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse($$$"""
        {"id":"j","name":"J","enabled":true,"configRevision":"r","schedule":{{{schedule}}},
         "payload":{"kind":"agentTurn","message":"m"}}
        """).RootElement)!;
        var form = CronJobForm.FromJob(job) with { Name = "Renamed" };
        Assert.True(form.HasUnchangedSchedule(job));
        Assert.False(form.BuildDefinition(job).ContainsKey("schedule"));
        var clone = CronJobForm.FromClone(job, "J (copy)").BuildDefinition(cloneSource: job);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(schedule), clone["schedule"]));
    }

    [Fact]
    public void BasicEdit_KeepsSettingsTheNativeEditorDoesNotShow()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r","sessionKey":"route","idleOnly":true,
         "schedule":{"kind":"every","everyMs":600000},
         "activeHours":{"start":"09:00","end":"17:00","timezone":"user"},"trigger":{"script":"x","once":true},
         "payload":{"kind":"agentTurn","message":"m","model":"p/1","thinking":"low","timeoutSeconds":5,"lightContext":true,"skipIfScratchEmpty":true,"toolsAllow":["a"]},
         "delivery":{"mode":"announce","channel":"slack","to":"#a","accountId":"work","threadId":"t","directPolicy":"block","bestEffort":true},
         "failureAlert":{"after":2,"cooldownMs":60000,"channel":"slack","to":"#ops","includeSkipped":true}}
        """).RootElement)!;
        var patch = (CronJobForm.FromJob(job) with { Name = "Renamed", PayloadText = "New prompt", EveryAmount = "15" }).BuildDefinition(job);

        Assert.Equal(900_000, patch["schedule"]!["everyMs"]!.GetValue<long>());
        Assert.False(patch.ContainsKey("trigger"));
        Assert.False(patch.ContainsKey("sessionTarget"));
        Assert.False(patch.ContainsKey("wakeMode"));
        Assert.Equal("route", patch["sessionKey"]!.GetValue<string>());
        Assert.False(patch.ContainsKey("idleOnly"));
        Assert.False(patch.ContainsKey("activeHours"));
        Assert.Empty((CronJobForm.FromJob(job) with { Name = "Renamed" }).ValidateForNative(job));
        var payload = patch["payload"]!.AsObject();
        Assert.Equal("New prompt", payload["message"]!.GetValue<string>());
        Assert.Equal("p/1", payload["model"]!.GetValue<string>());
        Assert.Equal("low", payload["thinking"]!.GetValue<string>());
        Assert.Equal(5, payload["timeoutSeconds"]!.GetValue<double>());
        Assert.True(payload["lightContext"]!.GetValue<bool>());
        Assert.True(payload["skipIfScratchEmpty"]!.GetValue<bool>());
        var delivery = patch["delivery"]!.AsObject();
        Assert.Equal("work", delivery["accountId"]!.GetValue<string>());
        Assert.Equal("t", delivery["threadId"]!.GetValue<string>());
        Assert.Equal("block", delivery["directPolicy"]!.GetValue<string>());
        Assert.True(delivery["bestEffort"]!.GetValue<bool>());
        var alert = patch["failureAlert"]!.AsObject();
        Assert.Equal(2, alert["after"]!.GetValue<int>());
        Assert.Equal(60_000, alert["cooldownMs"]!.GetValue<long>());
        Assert.True(alert["includeSkipped"]!.GetValue<bool>());
    }

    [Fact]
    public void NativeTiming_RejectsCopiesAndChangesWithoutDiscardingTheSavedValues()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r","idleOnly":false,
         "schedule":{"kind":"every","everyMs":600000},"payload":{"kind":"agentTurn","message":"m"},
         "activeHours":{"start":"09:00","end":"17:00","timezone":"user"}}
        """).RootElement)!;
        var form = CronJobForm.FromJob(job);
        Assert.Empty(form.ValidateForNative(job));
        var patch = (form with { Name = "Renamed" }).BuildDefinition(job);
        Assert.False(patch.ContainsKey("activeHours"));
        Assert.False(patch.ContainsKey("idleOnly"));
        Assert.Equal(CronFormError.TimingWebManaged,
            (form with { ActiveHoursEnabled = false }).ValidateForNative(job)[CronFormField.Timing]);
        Assert.Equal(CronFormError.TimingWebManaged,
            (form with { IdleOnly = true }).ValidateForNative(job)[CronFormField.Timing]);
        var copy = CronJobForm.FromClone(job, "Copy");
        Assert.Equal(CronFormError.TimingWebManaged, copy.ValidateForNative(null, job)[CronFormField.Timing]);
        Assert.True(copy.ActiveHoursEnabled);
        Assert.Equal("user", copy.ActiveHoursTimezone);
        Assert.False(copy.IdleOnly);
    }

    [Theory]
    [InlineData("announce")]
    [InlineData("none")]
    public void NativeOwnerDelivery_RejectsCreationCopiesAndRoutingChangesButPreservesBasicEdits(string mode)
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r",
         "schedule":{"kind":"every","everyMs":600000},
         "payload":{"kind":"agentTurn","message":"m"},
         "delivery":{"mode":"announce","target":"owner","channel":"slack","accountId":"work",
           "threadId":"saved-thread","directPolicy":"block","bestEffort":true,
           "failureDestination":{"mode":"announce","channel":"slack","to":"#ops"}}}
        """).RootElement)!;
        job = job with { Delivery = job.Delivery! with { Mode = mode } };
        var form = CronJobForm.FromJob(job);
        var renamed = form with { Name = "Renamed", PayloadText = "New prompt" };
        Assert.Empty(renamed.ValidateForNative(job));
        Assert.False(renamed.BuildDefinition(job).ContainsKey("delivery"));
        Assert.Equal("owner", form.DeliveryTarget);
        Assert.Equal("saved-thread", form.DeliveryThreadId);
        Assert.Equal("block", form.DeliveryDirectPolicy);

        var created = Valid with { DeliveryMode = mode, DeliveryTarget = "owner" };
        Assert.Equal(CronFormError.DeliveryWebManaged, created.ValidateForNative(null)[CronFormField.DeliveryRouting]);
        var copy = CronJobForm.FromClone(job, "Copied");
        Assert.Equal(CronFormError.DeliveryWebManaged, copy.ValidateForNative(null, job)[CronFormField.DeliveryRouting]);
        Assert.Equal("owner", copy.DeliveryTarget);
        foreach (var changed in new[]
        {
            form with { DeliveryTarget = "" }, form with { DeliveryChannel = "telegram" },
            form with { DeliveryTo = "different" }, form with { DeliveryAccountId = "personal" },
            form with { DeliveryMode = "webhook", DeliveryTo = "https://example.test/hook" },
            form with { DeliveryBestEffort = false }, form with { DeliveryThreadId = null },
        })
            Assert.Equal(CronFormError.DeliveryWebManaged, changed.ValidateForNative(job)[CronFormField.DeliveryRouting]);
    }

    [Fact]
    public void NativeDelivery_ChannelAndWebhookCreationRemainAvailable()
    {
        foreach (var form in new[]
        {
            Valid with { DeliveryMode = "announce", DeliveryChannel = "slack", DeliveryTo = "#daily" },
            Valid with { DeliveryMode = "webhook", DeliveryTo = "https://example.test/hook" },
        })
        {
            Assert.Empty(form.ValidateForNative(null));
            Assert.False(form.BuildDefinition()["delivery"]!.AsObject().ContainsKey("target"));
        }
    }

    [Fact]
    public void LegacyDelivery_IsKeptOnEditButMustBeChosenForACopy()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r","schedule":{"kind":"every","everyMs":600000},
         "payload":{"kind":"agentTurn","message":"m"},"delivery":{"mode":"legacy-push","to":"x"}}
        """).RootElement)!;
        var edit = CronJobForm.FromJob(job) with { Name = "Renamed" };
        Assert.Equal("", edit.DeliveryMode);
        Assert.True(edit.KeepSavedDelivery);
        Assert.Empty(edit.Validate());
        Assert.False(edit.BuildDefinition(job).ContainsKey("delivery"));

        var copy = CronJobForm.FromClone(job, "J (copy)");
        Assert.False(copy.KeepSavedDelivery);
        Assert.Equal(CronFormError.DeliveryModeRequired, copy.Validate()[CronFormField.DeliveryMode]);
    }

    [Fact]
    public void OmittedDeliveryAndTargets_AreNotRewrittenThroughGuessedDefaults()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r","schedule":{"kind":"every","everyMs":600000},
         "payload":{"kind":"agentTurn","message":"m"}}
        """).RootElement)!;
        var edit = CronJobForm.FromJob(job) with { Name = "Renamed" };
        Assert.Equal(("", true), (edit.DeliveryMode, edit.KeepSavedDelivery));
        Assert.Equal(("isolated", "now"), (edit.SessionTarget, edit.WakeMode));
        Assert.Empty(edit.Validate());
        var patch = edit.BuildDefinition(job);
        Assert.False(patch.ContainsKey("delivery"));
        Assert.False(patch.ContainsKey("sessionTarget"));
        Assert.False(patch.ContainsKey("wakeMode"));
        Assert.Equal("next-heartbeat", (edit with { WakeMode = "next-heartbeat" }).BuildDefinition(job)["wakeMode"]!.GetValue<string>());

        // A copy keeps the omitted delivery omitted, so the Gateway applies the same default plan.
        var copy = CronJobForm.FromClone(job, "J (copy)");
        Assert.True(copy.KeepSavedDelivery);
        Assert.Empty(copy.Validate());
        var created = copy.BuildDefinition(cloneSource: job);
        Assert.False(created.ContainsKey("delivery"));
        Assert.Equal("isolated", created["sessionTarget"]!.GetValue<string>());

        var timeline = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"t","name":"T","enabled":true,"configRevision":"r","schedule":{"kind":"every","everyMs":600000},
         "payload":{"kind":"systemEvent","text":"x"}}
        """).RootElement)!;
        var timelineEdit = CronJobForm.FromJob(timeline);
        Assert.Equal("main", timelineEdit.SessionTarget);
        Assert.False(timelineEdit.BuildDefinition(timeline).ContainsKey("sessionTarget"));
    }

    [Theory]
    [InlineData("account", true)]
    [InlineData("to", true)]
    [InlineData("agent", true)]
    [InlineData("same", false)]
    public void DeliveryThread_SurvivesOnlyAnUnchangedRoute(string change, bool cleared)
    {
        var before = Valid with { DeliveryMode = "announce", DeliveryChannel = "slack", DeliveryTo = "#daily", DeliveryAccountId = "work", DeliveryThreadId = "42" };
        var after = change switch
        {
            "account" => before with { DeliveryAccountId = "home" },
            "to" => before with { DeliveryTo = "#other" },
            "agent" => before with { AgentId = "ops" },
            _ => before with { DeliveryTo = " #daily " },
        };
        Assert.Equal(cleared ? null : "42", before.WithStaleThreadCleared(after).DeliveryThreadId);
    }

    [Fact]
    public void CreateDefault_LeavesUserContentBlank()
    {
        var form = CronJobForm.CreateDefault("ops");
        Assert.Empty(form.Name);
        Assert.Empty(form.Description);
        Assert.Empty(form.PayloadText);
        Assert.Empty(form.DeliveryTo);
        Assert.Equal("ops", form.AgentId);
    }
}
