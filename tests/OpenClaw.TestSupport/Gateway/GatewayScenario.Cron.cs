using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    public const string AutomationsName = "automations";
    public const string AutomationsOtherAgentJobId = "ops-backup";
    private const string FixtureRunSummary = "Synthetic fixture run. Nothing was executed.";

    private static readonly string[] CronMethods =
    [
        "cron.list", "cron.status", "cron.get", "cron.add", "cron.update", "cron.remove", "cron.run", "cron.runs",
    ];

    private readonly Dictionary<string, JsonObject>? _cronJobs;
    private readonly Dictionary<string, List<JsonObject>> _cronRuns = new(StringComparer.Ordinal);
    private int _cronRunCount;
    private int _cronCreated;
    private readonly Dictionary<string, string> _queuedCronRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// Opt-in, in-memory Automations data for the native page. Jobs are never
    /// scheduled or executed: <c>cron.run</c> only appends a synthetic run record.
    /// Jobs belong to <c>main</c> and <c>ops</c>; <c>research</c> owns none.
    /// </summary>
    public static GatewayScenario CreateAutomations(int additionalJobs = 0, bool sparse = false)
    {
        var browse = CreateBrowse();
        var anchor = DateTimeOffset.UtcNow;
        anchor = new DateTimeOffset(anchor.Year, anchor.Month, anchor.Day, anchor.Hour, anchor.Minute, 0, TimeSpan.Zero);
        var now = anchor.ToUnixTimeMilliseconds();
        const long Minute = 60_000, Hour = 60 * Minute, Day = 24 * Hour;

        var reads = new Dictionary<string, JsonElement>(browse._reads, StringComparer.Ordinal);

        JsonObject[] jobs =
        [
            Job("morning-brief", "Morning brief", "main", true,
                new { kind = "cron", expr = "0 7 * * 1-5", tz = "America/Los_Angeles", staggerMs = 300_000 },
                new { kind = "agentTurn", message = "Summarize overnight email, calendar changes and anything urgent in three bullets.", model = "fixture/browse", thinking = "low", timeoutSeconds = 120 },
                new { mode = "announce", channel = "slack", to = "#daily", accountId = "work", bestEffort = true },
                new { nextRunAtMs = now + 9 * Hour, lastRunAtMs = now - 15 * Hour, lastRunStatus = "ok", lastDurationMs = 41_200, lastDeliveryStatus = "delivered" },
                description: "Weekday digest posted to the team channel."),
            Job("inbox-sweep", "Inbox sweep", "main", true,
                new { kind = "every", everyMs = 30 * Minute },
                new { kind = "agentTurn", message = "Triage new email: archive newsletters, flag anything from my manager.", lightContext = true },
                new { mode = "none" },
                new { nextRunAtMs = now + 12 * Minute, lastRunAtMs = now - 18 * Minute, lastRunStatus = "ok", lastDurationMs = 8_400 }),
            Job("deploy-watch", "Deploy watch", "main", true,
                new { kind = "cron", expr = "*/15 * * * *" },
                new { kind = "agentTurn", message = "A production deploy finished. Check error rates and summarize anything unusual.", model = "fixture/fast" },
                new { mode = "webhook", to = "https://hooks.example.test/deploy", bestEffort = false },
                new { nextRunAtMs = now + 7 * Minute, lastRunAtMs = now - 2 * Day, lastRunStatus = "ok", lastDurationMs = 12_000, triggerEvalCount = 42, lastTriggerEvalAtMs = now - 8 * Minute, lastTriggerFireAtMs = now - 2 * Day },
                trigger: new { script = "const r = await fetch('https://status.example.test'); return json({ fire: r.deployed });", once = false },
                failureAlert: new { after = 3, cooldownMs = 3_600_000, mode = "announce", channel = "slack", to = "#oncall" }),
            Job("flaky-report", "Vendor report", "main", false,
                new { kind = "cron", expr = "30 6 * * *" },
                new { kind = "agentTurn", message = "Download the vendor report and post the totals." },
                new { mode = "announce" },
                new { lastRunAtMs = now - 6 * Hour, lastRunStatus = "error", lastError = "Model provider rejected the request (fixture).", consecutiveErrors = 5, autoDisabled = new { reason = "consecutive-failures", atMs = now - 6 * Hour, consecutiveErrors = 5 } }),
            Job("dentist", "Dentist reminder", "main", true,
                new { kind = "at", at = DateTimeOffset.FromUnixTimeMilliseconds(now + 2 * Day + 3 * Hour).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") },
                new { kind = "systemEvent", text = "Dentist appointment in one hour. Leave by 2:15." },
                null,
                new { nextRunAtMs = now + 2 * Day + 3 * Hour },
                sessionTarget: "main", deleteAfterRun: true),
            Job("pr-opened", "New pull request triage", "main", true,
                new { kind = "event", source = "mcp-events", options = new { server = "github", name = "pull_request.opened", arguments = new { repo = "openclaw/openclaw" } } },
                new { kind = "agentTurn", message = "Review the new pull request, label it and suggest reviewers." },
                new { mode = "announce", channel = "telegram", to = "@owner" },
                new { lastRunAtMs = now - 3 * Hour, lastRunStatus = "ok", lastDurationMs = 23_000, sourceIdentity = "github:pull_request.opened" }),
            Job("weekly-digest", "Weekly digest", "main", false,
                new { kind = "cron", expr = "0 9 * * 1" },
                new { kind = "agentTurn", message = "Write a short digest of last week's notes and meetings." },
                new { mode = "announce", channel = "last" },
                new { lastRunAtMs = now - 6 * Day, lastRunStatus = "ok", lastDurationMs = 64_000 }),
            Job("log-tail", "Error log tail", "main", true,
                new { kind = "stream", command = new[] { "journalctl", "-f", "-p", "err" }, mode = "line" },
                new { kind = "command", argv = new[] { "notify-send", "OpenClaw", "New error in system log" }, timeoutSeconds = 10 },
                new { mode = "none" },
                new { streamStatus = "running" }),
            Job(AutomationsOtherAgentJobId, "Ops backup (other agent)", "ops", true,
                new { kind = "every", everyMs = Day },
                new { kind = "agentTurn", message = "Verify last night's backups." },
                new { mode = "none" },
                new { nextRunAtMs = now + 5 * Hour }),
        ];

        jobs[0]["sessionKey"] = "agent:main:main";
        if (sparse) jobs = [jobs[0]];
        if (additionalJobs > 0)
            jobs = jobs.Concat(Enumerable.Range(0, additionalJobs).Select(i =>
                Job($"scheduled-{i:D3}", $"Scheduled fixture {i:D3}", "main", true,
                    new { kind = "at", at = DateTimeOffset.FromUnixTimeMilliseconds(now + (i + 1) * Minute).ToString("O") },
                    new { kind = "agentTurn", message = "Synthetic agenda pagination fixture." }, new { mode = "none" },
                    new { nextRunAtMs = now + (i + 1) * Minute }))).ToArray();
        var scenario = new GatewayScenario(browse._sessions, reads, cronJobs: jobs);
        scenario.SeedRuns(now);
        return scenario;
    }

    public JsonElement[] GetCronJobs()
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs) return jobs.Values.Select(job => JsonSerializer.SerializeToElement(job)).ToArray();
    }

    /// <summary>
    /// Set before connecting: the operator is granted <c>operator.read</c> only, so automations
    /// can be browsed while every change is rejected with <c>FORBIDDEN</c>, as on the Gateway.
    /// </summary>
    public bool CronReadOnly { get; set; }

    /// <summary>Every automation read and change is rejected with <c>FORBIDDEN</c>.</summary>
    public bool CronReadDenied { get; set; }
    public bool CronSchedulerEnabled { get; set; } = true;
    public string? CronReadError { get; set; }
    public string? CronStatusReadError { get; set; }
    /// <summary>Models older list responses that omit resolved-owner metadata.</summary>
    public bool OmitCronEffectiveAgentId { get; set; }
    public bool RejectUnsupportedCronTimingFields { get; set; }
    public bool RejectUnsupportedCronDeliveryTarget { get; set; }

    /// <summary>Supplies explicit agenda instants without implementing client grouping or layout.</summary>
    public void SetCronJobNextRun(string id, long atMs)
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs) jobs[id]["state"]!["nextRunAtMs"] = atMs;
    }

    /// <summary>When set, <c>cron.run</c> is accepted but stays queued: no history or last-run state yet.</summary>
    public bool QueueCronRunsOnly { get; set; }

    /// <summary>Pending synthetic invocation IDs, for explicit completion during owned proof.</summary>
    public string[] GetQueuedCronRunIds(string jobId)
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs) return _queuedCronRuns.Where(run => run.Value == jobId).Select(run => run.Key).ToArray();
    }

    /// <summary>Completes one previously accepted synthetic invocation. Optional list lag models stale inventory.</summary>
    public void CompleteQueuedCronRun(string jobId, string runId, long runAtMs, bool updateListState = true)
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs)
        {
            if (!_queuedCronRuns.TryGetValue(runId, out var owner) || owner != jobId)
                throw new InvalidOperationException("No matching synthetic queued invocation.");
            var job = jobs[jobId];
            var run = Run(jobId, job["name"]!.GetValue<string>(), runAtMs, "ok", FixtureRunSummary, null,
                "not-requested", 1_000, "browse", "fixture", 10, 5);
            run["runId"] = runId;
            if (!_cronRuns.TryGetValue(jobId, out var runs)) _cronRuns[jobId] = runs = [];
            runs.Insert(0, run);
            _queuedCronRuns.Remove(runId);
            if (updateListState)
            {
                job["state"]!["lastRunAtMs"] = runAtMs;
                job["state"]!["lastRunStatus"] = "ok";
                job["state"]!.AsObject().Remove("runningAtMs");
            }
        }
    }

    public void SetCronJobOwner(string id, string? agentId)
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs) jobs[id]["agentId"] = agentId;
    }

    /// <summary>Simulates a malformed Gateway row by omitting a field from its responses.</summary>
    public void RemoveCronJobField(string id, string field)
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs) jobs[id].Remove(field);
    }

    /// <summary>Simulates a run started outside the app (a schedule or another client).</summary>
    public void SetCronJobRunning(string id, bool running)
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Automations are not enabled.");
        lock (jobs)
        {
            var state = (JsonObject)jobs[id]["state"]!;
            if (running) state["runningAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            else state.Remove("runningAtMs");
        }
    }

    private static JsonObject Job(
        string id, string name, string agentId, bool enabled, object schedule, object payload, object? delivery, object state,
        string? description = null, object? trigger = null, object? failureAlert = null, string sessionTarget = "isolated", bool? deleteAfterRun = null)
    {
        var job = JsonSerializer.SerializeToNode(new
        {
            id, agentId, name, enabled, schedule, sessionTarget, wakeMode = "now", payload,
            createdAtMs = 1_780_000_000_000, updatedAtMs = 1_780_000_000_000, configRevision = "rev-1",
            state,
        })!.AsObject();
        if (description is not null) job["description"] = description;
        if (delivery is not null) job["delivery"] = JsonSerializer.SerializeToNode(delivery);
        if (trigger is not null) job["trigger"] = JsonSerializer.SerializeToNode(trigger);
        if (failureAlert is not null) job["failureAlert"] = JsonSerializer.SerializeToNode(failureAlert);
        if (deleteAfterRun is not null) job["deleteAfterRun"] = deleteAfterRun;
        return job;
    }

    private void SeedRuns(long now)
    {
        const long Hour = 3_600_000, Day = 24 * Hour;
        var brief = new List<JsonObject>();
        for (var i = 0; i < 14; i++)
        {
            var at = now - 15 * Hour - i * Day;
            var status = i == 3 ? "error" : i == 6 ? "skipped" : "ok";
            brief.Add(Run("morning-brief", "Morning brief", at, status,
                status == "ok" ? $"Three things today: design review at 10, two invoices to approve, and a reply owed to Sam. (day {i + 1})"
                : status == "error" ? null : "Skipped: outside active hours.",
                status == "error" ? "Model provider timed out after 120 seconds (fixture)." : null,
                status == "ok" ? "delivered" : status == "error" ? "not-delivered" : "not-requested",
                durationMs: 30_000 + i * 1_000, model: "browse", provider: "fixture", input: 1_800 + i * 10, output: 220 + i));
        }
        brief[0]["nextRunAtMs"] = now + 9 * Hour;
        foreach (var run in brief)
            run["sessionKey"] = $"agent:main:cron:morning-brief:run:{run["runId"]!.GetValue<string>()}";
        _cronRuns["morning-brief"] = brief;
        _cronRuns["inbox-sweep"] = Enumerable.Range(0, 6).Select(i => Run("inbox-sweep", "Inbox sweep", now - 18 * 60_000 - i * 30 * 60_000, "ok",
            $"Archived {4 + i} newsletters, flagged {i % 2} message.", null, "not-requested", 8_000 + i * 200, "browse", "fixture", 900, 60)).ToList();
        _cronRuns["deploy-watch"] = [Run("deploy-watch", "Deploy watch", now - 2 * Day, "ok",
            "Deploy 4812 is healthy. Error rate unchanged at 0.02%.", null, "delivered", 12_000, "fast", "fixture", 1_200, 90, triggerFired: true)];
        var flaky = Enumerable.Range(0, 5).Select(i => Run("flaky-report", "Vendor report", now - 6 * Hour - i * Day, "error",
            null, "Model provider rejected the request (fixture).", "not-delivered", 2_000, "browse", "fixture", 300, 0)).ToList();
        flaky[0]["deliverySuppressionReason"] = "run failed";
        _cronRuns["flaky-report"] = flaky;
        _cronRuns["pr-opened"] = [Run("pr-opened", "New pull request triage", now - 3 * Hour, "ok",
            "Labeled #4021 as area/ui and suggested two reviewers.", null, "delivered", 23_000, "browse", "fixture", 2_400, 310)];
        _cronRuns["weekly-digest"] = [Run("weekly-digest", "Weekly digest", now - 6 * Day, "ok",
            "Last week: 3 design reviews, the Q3 plan, and a new hire onboarding.", null, "delivered", 64_000, "browse", "fixture", 5_100, 640)];
        _cronRuns[AutomationsOtherAgentJobId] = [Run(AutomationsOtherAgentJobId, "Ops backup (other agent)", now - 19 * Hour, "ok",
            "Backups verified.", null, "not-requested", 4_000, "browse", "fixture", 100, 10)];
    }

    private JsonObject Run(string jobId, string jobName, long at, string status, string? summary, string? error, string deliveryStatus,
        long durationMs, string model, string provider, long input, long output, bool? triggerFired = null)
    {
        var run = new JsonObject
        {
            ["ts"] = at + durationMs,
            ["jobId"] = jobId,
            ["jobName"] = jobName,
            ["action"] = "finished",
            ["status"] = status,
            ["runId"] = $"run-{jobId}-{++_cronRunCount}",
            ["runAtMs"] = at,
            ["durationMs"] = durationMs,
            ["deliveryStatus"] = deliveryStatus,
            ["model"] = model,
            ["provider"] = provider,
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output, ["total_tokens"] = input + output },
        };
        if (summary is not null) run["summary"] = summary;
        if (error is not null) run["error"] = error;
        if (triggerFired is not null) run["triggerFired"] = triggerFired;
        return run;
    }

    private bool IsCronMethod(string method) => _cronJobs is not null && CronMethods.Contains(method, StringComparer.Ordinal);

    private object RespondCron(string method, JsonElement p)
    {
        if (CronReadDenied)
            throw new FixtureRequestException("FORBIDDEN", "missing scope: operator.read");
        if (CronReadError is { } readError && method is "cron.list" or "cron.get" or "cron.status" or "cron.runs")
            throw new FixtureRequestException("UNAVAILABLE", readError);
        if (CronStatusReadError is { } statusError && method == "cron.status")
            throw new FixtureRequestException("UNAVAILABLE", statusError);
        if ((_inboxFeed is not null || CronReadOnly) && method is "cron.add" or "cron.update" or "cron.remove" or "cron.run")
            throw new FixtureRequestException("FORBIDDEN", "missing scope: operator.admin");
        var jobs = _cronJobs!;
        lock (jobs)
        {
            switch (method)
            {
                case "cron.status":
                    ValidateProperties(p);
                    return new
                    {
                        enabled = CronSchedulerEnabled,
                        jobs = jobs.Count,
                        storePath = "synthetic-memory-only",
                        nextWakeAtMs = jobs.Values.Select(j => j["state"]?["nextRunAtMs"]?.GetValue<long>()).Where(v => v is not null).Min(),
                    };
                case "cron.list":
                    return ListJobs(jobs, p);
                case "cron.get":
                    ValidateProperties(p, "id", "jobId");
                    return FindJob(jobs, p);
                case "cron.add":
                    return AddJob(jobs, p);
                case "cron.update":
                    return UpdateJob(jobs, p);
                case "cron.remove":
                {
                    // Released Gateways validate remove against a closed id-only schema; the revision guard exists only on cron.update.
                    ValidateProperties(p, "id", "jobId");
                    var job = FindJob(jobs, p);
                    var id = job["id"]!.GetValue<string>();
                    jobs.Remove(id);
                    _cronRuns.Remove(id);
                    return new { ok = true, removed = true };
                }
                case "cron.run":
                    return RunJob(jobs, p);
                case "cron.runs":
                    return ListRuns(jobs, p);
                default:
                    throw new FixtureRequestException("INVALID_REQUEST", "Unknown automations method.", unexpected: true);
            }
        }
    }

    private object ListJobs(Dictionary<string, JsonObject> jobs, JsonElement p)
    {
        ValidateProperties(p, "agentId", "includeDisabled", "includeDeliveryPreviews", "limit", "offset", "query", "enabled",
            "scheduleKind", "lastRunStatus", "trigger", "sortBy", "sortDir", "compact", "sessionKey", "sessionAgentId");
        IEnumerable<JsonObject> rows = jobs.Values;
        // Like the Gateway, a job without an explicit owner belongs to the default agent.
        static string Owner(JsonObject job) => job["agentId"]?.GetValue<string>() ?? "main";
        if (OptionalString(p, "agentId") is { } agent)
            rows = rows.Where(j => Owner(j) == agent);
        var enabled = OptionalString(p, "enabled") ?? (OptionalBoolean(p, "includeDisabled") == false ? "enabled" : "all");
        // A row without a state (RemoveCronJobField) is returned under either filter, so readers see it.
        rows = enabled switch
        {
            "enabled" => rows.Where(j => j["enabled"]?.GetValue<bool>() != false),
            "disabled" => rows.Where(j => j["enabled"]?.GetValue<bool>() != true),
            _ => rows,
        };
        if (OptionalString(p, "query") is { } query)
            rows = rows.Where(j => j.ToJsonString().Contains(query, StringComparison.OrdinalIgnoreCase));
        if (OptionalString(p, "scheduleKind") is { } kind && kind != "all")
            rows = rows.Where(j => j["schedule"]!["kind"]!.GetValue<string>() == kind);
        if (OptionalString(p, "lastRunStatus") is { } last && last != "all")
            rows = rows.Where(j => (j["state"]?["lastRunStatus"]?.GetValue<string>() ?? "unknown") == last);
        if (OptionalString(p, "trigger") is { } trigger && trigger != "all")
            rows = rows.Where(j => (j["trigger"] is not null) == (trigger == "conditional"));
        var descending = OptionalString(p, "sortDir") == "desc";
        rows = (OptionalString(p, "sortBy") ?? "nextRunAtMs") switch
        {
            "name" => descending ? rows.OrderByDescending(j => j["name"]!.GetValue<string>()) : rows.OrderBy(j => j["name"]!.GetValue<string>()),
            "updatedAtMs" => descending ? rows.OrderByDescending(j => j["updatedAtMs"]!.GetValue<long>()) : rows.OrderBy(j => j["updatedAtMs"]!.GetValue<long>()),
            _ => descending
                ? rows.OrderByDescending(j => j["state"]?["nextRunAtMs"]?.GetValue<long>() ?? long.MinValue)
                : rows.OrderBy(j => j["state"]?["nextRunAtMs"]?.GetValue<long>() ?? long.MaxValue),
        };
        var all = rows.ToList();
        var offset = OptionalInt(p, "offset") ?? 0;
        var limit = OptionalInt(p, "limit") ?? 50;
        if (offset < 0 || limit is < 1 or > 200)
            throw new FixtureRequestException("INVALID_PARAMS", "Expected bounded cron pagination.");
        var page = all.Skip(offset).Take(limit).Select(j =>
        {
            var row = j.DeepClone().AsObject();
            if (!OmitCronEffectiveAgentId) row["effectiveAgentId"] = Owner(j);
            return JsonSerializer.SerializeToElement(row);
        }).ToArray();
        var hasMore = offset + page.Length < all.Count;
        var result = new { jobs = page, total = all.Count, offset, limit, hasMore, nextOffset = hasMore ? offset + page.Length : (int?)null };
        if (_inboxFeed is null)
            return result;
        _inboxFeed.RecordCronPage(p);
        var snapshot = JsonSerializer.SerializeToNode(result)!.AsObject();
        // One revision describes the entire inventory, independent of filter or page.
        snapshot["snapshotRevision"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jobs))));
        return snapshot;
    }

    private static JsonObject FindJob(Dictionary<string, JsonObject> jobs, JsonElement p)
    {
        var id = OptionalString(p, "id") ?? RequiredString(p, "jobId");
        return jobs.TryGetValue(id, out var job) ? job : throw new FixtureRequestException("INVALID_REQUEST", "Automation not found.", detailCode: "CRON_JOB_NOT_FOUND");
    }

    private static void CheckRevision(JsonObject job, JsonElement p)
    {
        if (OptionalString(p, "expectedConfigRevision") is { } expected && expected != job["configRevision"]!.GetValue<string>())
            throw new FixtureRequestException("CRON_JOB_CHANGED", "The automation changed since it was loaded.");
    }

    private static readonly string[] JobFields =
    [
        "name", "displayName", "description", "agentId", "sessionKey", "enabled", "deleteAfterRun", "schedule", "pacing", "activeHours",
        "idleOnly", "trigger", "sessionTarget", "wakeMode", "payload", "delivery", "failureAlert",
    ];

    private JsonObject AddJob(Dictionary<string, JsonObject> jobs, JsonElement p)
    {
        ValidateProperties(p, JobFields);
        ValidateCronCompatibilityProperties(p);
        _ = RequiredString(p, "name");
        if (!p.TryGetProperty("schedule", out var schedule)) throw new FixtureRequestException("INVALID_REQUEST", "schedule is required.");
        ValidateSchedule(schedule);
        if (!p.TryGetProperty("payload", out _)) throw new FixtureRequestException("INVALID_REQUEST", "payload is required.");
        var id = $"created-{++_cronCreated}";
        var job = JsonNode.Parse(p.GetRawText())!.AsObject();
        job["id"] = id;
        job["enabled"] ??= true;
        job["agentId"] ??= "main";
        job["sessionTarget"] ??= "isolated";
        job["wakeMode"] ??= "now";
        job["createdAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        job["updatedAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        job["configRevision"] = "rev-1";
        job["state"] = new JsonObject { ["nextRunAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000 };
        RemoveNulls(job);
        ValidateSupportedJobSpec(job);
        jobs[id] = job;
        return job;
    }

    private JsonObject UpdateJob(Dictionary<string, JsonObject> jobs, JsonElement p)
    {
        ValidateProperties(p, "id", "jobId", "expectedConfigRevision", "patch");
        var job = FindJob(jobs, p);
        CheckRevision(job, p);
        if (!p.TryGetProperty("patch", out var patch)) throw new FixtureRequestException("INVALID_REQUEST", "Missing patch.");
        ValidateProperties(patch, [.. JobFields, "state"]);
        ValidateCronCompatibilityProperties(patch);
        if (patch.TryGetProperty("schedule", out var schedule)) ValidateSchedule(schedule);
        var updated = job.DeepClone().AsObject();
        foreach (var field in patch.EnumerateObject())
        {
            var value = JsonNode.Parse(field.Value.GetRawText());
            // Nested payload/delivery patches merge; a null value clears.
            if (field.Name is "payload" or "delivery" && value is JsonObject nested && updated[field.Name] is JsonObject existing)
            {
                foreach (var (key, inner) in nested)
                    existing[key] = inner?.DeepClone();
                RemoveNulls(existing);
            }
            else if (value is null)
            {
                updated.Remove(field.Name);
            }
            else
            {
                updated[field.Name] = value;
            }
        }
        ValidateSupportedJobSpec(updated);
        if (patch.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
            updated["state"]!.AsObject().Remove("autoDisabled");
        var revision = int.Parse(job["configRevision"]!.GetValue<string>()["rev-".Length..]) + 1;
        updated["configRevision"] = $"rev-{revision}";
        updated["updatedAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        jobs[job["id"]!.GetValue<string>()] = updated;
        return updated;
    }

    private static void ValidateSupportedJobSpec(JsonObject job)
    {
        // Independent released Gateway invariant (v2026.11.2 jobs-validation.ts).
        var target = job["sessionTarget"]?.GetValue<string>();
        var kind = job["payload"]?["kind"]?.GetValue<string>();
        if ((target == "main" && kind == "agentTurn") || (target != "main" && kind == "systemEvent"))
            throw new FixtureRequestException("INVALID_REQUEST", "Session target and payload kind are incompatible.");
    }

    private void ValidateCronCompatibilityProperties(JsonElement parameters)
    {
        if (RejectUnsupportedCronTimingFields &&
            (parameters.TryGetProperty("activeHours", out _) || parameters.TryGetProperty("idleOnly", out _)))
            throw new FixtureRequestException("INVALID_PARAMS", "Unsupported automation timing property.");
        // v2026.11.2 closed create/update delivery schemas do not contain target, including null clears.
        if (RejectUnsupportedCronDeliveryTarget && parameters.TryGetProperty("delivery", out var delivery) &&
            delivery.ValueKind == JsonValueKind.Object && delivery.TryGetProperty("target", out _))
            throw new FixtureRequestException("INVALID_PARAMS", "Unsupported automation delivery target.");
    }

    private object RunJob(Dictionary<string, JsonObject> jobs, JsonElement p)
    {
        ValidateProperties(p, "id", "jobId", "mode", "force");
        var job = FindJob(jobs, p);
        var id = job["id"]!.GetValue<string>();
        if (OptionalString(p, "mode") == "due")
            return new { ok = true, ran = false, reason = "not-due" };
        if (job["state"]?["runningAtMs"]?.GetValue<long>() is > 0)
            return new { ok = true, ran = false, reason = "already-running" };
        if (QueueCronRunsOnly)
        {
            var queuedId = $"queued-{id}-{++_cronRunCount}";
            _queuedCronRuns.Add(queuedId, id);
            return new { ok = true, enqueued = true, runId = queuedId };
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var run = Run(id, job["name"]!.GetValue<string>(), now, "ok", FixtureRunSummary, null, "not-requested", 1_000, "browse", "fixture", 10, 5);
        if (!_cronRuns.TryGetValue(id, out var runs)) _cronRuns[id] = runs = [];
        runs.Insert(0, run);
        var state = job["state"]!.AsObject();
        state["lastRunAtMs"] = now;
        state["lastRunStatus"] = "ok";
        state["lastDurationMs"] = 1_000;
        return new { ok = true, enqueued = true, runId = run["runId"]!.GetValue<string>() };
    }

    private object ListRuns(Dictionary<string, JsonObject> jobs, JsonElement p)
    {
        // Released Gateways validate cron.runs against a closed schema without the newer "view" option.
        ValidateProperties(p, "agentId", "scope", "id", "jobId", "runId", "limit", "offset", "statuses", "status",
            "deliveryStatuses", "deliveryStatus", "query", "sortDir");
        var jobId = OptionalString(p, "id") ?? OptionalString(p, "jobId");
        IEnumerable<JsonObject> rows;
        if (OptionalString(p, "scope") == "job" || jobId is not null)
        {
            rows = jobId is not null && _cronRuns.TryGetValue(jobId, out var runs) ? runs : [];
        }
        else
        {
            var agent = OptionalString(p, "agentId");
            rows = _cronRuns
                .Where(kv => agent is null || (jobs.TryGetValue(kv.Key, out var owner) && owner["agentId"]?.GetValue<string>() == agent))
                .SelectMany(kv => kv.Value);
        }
        if (OptionalString(p, "runId") is { } runId) rows = rows.Where(r => r["runId"]!.GetValue<string>() == runId);
        var statuses = StringArray(p, "statuses");
        if (statuses.Length > 0) rows = rows.Where(r => statuses.Contains(r["status"]!.GetValue<string>()));
        var deliveries = StringArray(p, "deliveryStatuses");
        if (deliveries.Length > 0) rows = rows.Where(r => deliveries.Contains(r["deliveryStatus"]!.GetValue<string>()));
        if (OptionalString(p, "query") is { } query) rows = rows.Where(r => r.ToJsonString().Contains(query, StringComparison.OrdinalIgnoreCase));
        rows = OptionalString(p, "sortDir") == "asc"
            ? rows.OrderBy(r => r["runAtMs"]!.GetValue<long>())
            : rows.OrderByDescending(r => r["runAtMs"]!.GetValue<long>());
        var all = rows.ToList();
        var offset = OptionalInt(p, "offset") ?? 0;
        var limit = OptionalInt(p, "limit") ?? 50;
        var page = all.Skip(offset).Take(limit).Select(r => JsonSerializer.SerializeToElement(r)).ToArray();
        var hasMore = offset + page.Length < all.Count;
        return new { entries = page, total = all.Count, offset, limit, hasMore, nextOffset = hasMore ? offset + page.Length : (int?)null };
    }

    private static void ValidateSchedule(JsonElement schedule)
    {
        if (schedule.ValueKind != JsonValueKind.Object)
            throw new FixtureRequestException("INVALID_REQUEST", "schedule must be an object.");
        switch (RequiredString(schedule, "kind"))
        {
            case "cron":
                ValidateProperties(schedule, "kind", "expr", "tz", "staggerMs");
                _ = RequiredString(schedule, "expr");
                break;
            case "every":
                ValidateProperties(schedule, "kind", "everyMs", "anchorMs");
                if (OptionalLong(schedule, "everyMs") is not > 0)
                    throw new FixtureRequestException("INVALID_REQUEST", "everyMs must be positive.");
                break;
            case "at":
                ValidateProperties(schedule, "kind", "at");
                if (!DateTimeOffset.TryParse(RequiredString(schedule, "at"), out _))
                    throw new FixtureRequestException("INVALID_REQUEST", "at must be a timestamp.");
                break;
            case "event":
                ValidateProperties(schedule, "kind", "source", "options");
                break;
            default:
                throw new FixtureRequestException("INVALID_REQUEST", "Unsupported schedule kind for this fixture.");
        }
    }

    private static void RemoveNulls(JsonObject node)
    {
        foreach (var key in node.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            node.Remove(key);
    }

    private static int? OptionalInt(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? OptionalLong(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static string[] StringArray(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];
}
