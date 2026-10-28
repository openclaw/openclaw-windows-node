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
    public void ParseSchedule_ReadsEventAndProcessKindsAsReadOnlyWhereNeeded()
    {
        var mcp = CronJsonReader.ParseSchedule(Json("""{"kind":"event","source":"mcp-events","options":{"server":"github","name":"pr.opened","arguments":{"repo":"a/b"}}}"""));
        Assert.True(mcp.IsMcpEvent);
        Assert.False(mcp.IsReadOnly);
        Assert.Equal("github", mcp.EventServer);
        Assert.Contains("\"repo\"", mcp.EventArgumentsJson);
        var plugin = CronJsonReader.ParseSchedule(Json("""{"kind":"event","source":"calendar","options":{}}"""));
        Assert.True(plugin.IsReadOnly);
        var stream = CronJsonReader.ParseSchedule(Json("""{"kind":"stream","command":["tail","-f"]}"""));
        Assert.Equal(CronScheduleKind.Stream, stream.Kind);
        Assert.Equal("tail -f", stream.Command);
        Assert.True(stream.IsReadOnly);
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
    }

    [Fact]
    public void ParseRunsAndStatusAndScratch()
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
        Assert.True(run.CanOpenTranscript);
        Assert.False(runs.Entries[1].CanOpenTranscript);
        Assert.Equal("j:20", runs.Entries[1].Key);

        var status = CronJsonReader.ParseStatus(Json("""{"enabled":false,"nextWakeAtMs":99,"jobs":4}"""));
        Assert.Equal(new CronSchedulerStatus(false, 99, 4, null), status);

        var scratch = CronJsonReader.ParseScratch(Json("""{"scratch":{"content":"x","revision":2,"updatedAtMs":3},"currentRevision":2,"maxBytes":10}"""));
        Assert.True(scratch.Exists);
        Assert.Equal(10, scratch.MaxBytes);
        var missing = CronJsonReader.ParseScratch(Json("""{"scratch":null,"currentRevision":0,"maxBytes":10}"""));
        Assert.False(missing.Exists);
        var conflict = CronJsonReader.ParseScratchSet(Json("""{"ok":false,"reason":"revision-conflict","currentRevision":4}"""));
        Assert.True(conflict.Conflict);
        Assert.Equal(4, conflict.CurrentRevision);
    }

    [Fact]
    public void ParseConfigHints_ReadsEnabledMcpServersAndModels()
    {
        var config = Json("""
        {"config":{"mcp":{"servers":{"b":{"enabled":true},"a":{},"off":{"enabled":false}}},
         "agents":{"defaults":{"model":{"primary":"p/1","fallbacks":["p/2"]},"models":{"p/3":{}}},"entries":{"x":{"model":"p/4"}}}}}
        """);
        Assert.Equal(["a", "b"], CronJsonReader.ParseEnabledMcpServers(config));
        Assert.Equal(["p/1", "p/2", "p/3", "p/4"], CronJsonReader.ParseConfiguredModels(config));
    }

    [Fact]
    public void ListQuery_AlwaysSendsTheAgentScope()
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
    }

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
    public void Validate_TriggersEventsDeliveryAndAlerts()
    {
        var trigger = (Valid with { TriggerEnabled = true, EveryAmount = "10", EveryUnit = CronDurationUnit.Seconds }).Validate();
        Assert.Equal(CronFormError.TriggerIntervalTooShort, trigger[CronFormField.EveryAmount]);
        Assert.Equal(CronFormError.TriggerScriptRequired, trigger[CronFormField.TriggerScript]);
        Assert.Equal(CronFormError.TriggerScheduleUnsupported,
            (Valid with { TriggerEnabled = true, TriggerScript = "x", ScheduleKind = CronScheduleKind.At, ScheduleAt = DateTimeOffset.Now }).Validate()[CronFormField.TriggerScript]);

        var evt = (Valid with { ScheduleKind = CronScheduleKind.Event, EventArguments = "[1]" }).Validate();
        Assert.Equal(CronFormError.EventServerRequired, evt[CronFormField.EventServer]);
        Assert.Equal(CronFormError.EventNameRequired, evt[CronFormField.EventName]);
        Assert.Equal(CronFormError.EventArgumentsInvalid, evt[CronFormField.EventArguments]);

        Assert.Equal(CronFormError.WebhookUrlRequired, (Valid with { DeliveryMode = "webhook" }).Validate()[CronFormField.DeliveryTo]);
        Assert.Equal(CronFormError.WebhookUrlInvalid, CronJobForm.ValidateWebhookUrl("ftp://x.test"));
        Assert.Equal(CronFormError.WebhookUrlInvalid, CronJobForm.ValidateWebhookUrl("https://user:pw@x.test/hook"));
        Assert.Null(CronJobForm.ValidateWebhookUrl("https://x.test/hook"));
        Assert.Equal(CronFormError.DeliveryModeRequired, (Valid with { DeliveryMode = "" }).Validate()[CronFormField.DeliveryMode]);
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
    public void WithScheduleKind_AppliesOneShotAndEventDefaults()
    {
        var once = Valid.WithScheduleKind(CronScheduleKind.At);
        Assert.True(once.DeleteAfterRun);
        Assert.NotNull(once.ScheduleAt);
        Assert.False(once.WithScheduleKind(CronScheduleKind.Every).DeleteAfterRun);
        var evt = (Valid with { TriggerEnabled = true, SessionTarget = "main", PayloadKind = CronPayloadKind.SystemEvent }).WithScheduleKind(CronScheduleKind.Event);
        Assert.False(evt.TriggerEnabled);
        Assert.Equal(CronPayloadKind.AgentTurn, evt.PayloadKind);
        Assert.Equal("isolated", evt.SessionTarget);
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

    [Fact]
    public void PluginEventSchedules_ArePreservedOnSave()
    {
        var job = CronJsonReader.ParseJob(JsonDocument.Parse("""
        {"id":"j","name":"J","enabled":true,"configRevision":"r",
         "schedule":{"kind":"event","source":"plugin-x","eventName":"ping"},
         "payload":{"kind":"agentTurn","message":"m"}}
        """).RootElement)!;
        var form = CronJobForm.FromJob(job) with { EventServer = "ignored", EventName = "ignored", Name = "Renamed" };
        Assert.True(form.HasUnchangedSchedule(job));
        Assert.False(form.BuildDefinition(job).ContainsKey("schedule"));
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
    public void FromSuggestion_PrefillsAnIsolatedAssistantTask()
    {
        var suggestion = CronSuggestion.All.Single(s => s.Id == "watchdog");
        var form = CronJobForm.FromSuggestion(suggestion, "Night watch", "Check", "ops");
        Assert.Equal(("1", CronDurationUnit.Hours), (form.EveryAmount, form.EveryUnit));
        Assert.Equal("isolated", form.SessionTarget);
        Assert.Equal("ops", form.AgentId);
        Assert.Empty(form.Validate());
    }
}
