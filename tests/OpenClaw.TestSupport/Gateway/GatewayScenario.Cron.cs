using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    private readonly Dictionary<string, JsonObject>? _cronJobs;
    private readonly bool _cronManagement;
    private readonly Dictionary<string, (string? Content, long Revision)> _cronScratch = new(StringComparer.Ordinal);
    public const string CronFixtureRunId = "fixture:listed:1";
    private const long CronFixtureRunAtMs = 1_787_227_200_000;
    private const int CronScratchMaxBytes = 262144;

    /// <summary>Opt-in, in-memory cron editing only. Jobs are never scheduled or executed.</summary>
    public static GatewayScenario CreateCronEditing()
        => CreateCronScenario(false);

    /// <summary>Full synthetic cron protocol. Manual runs are acknowledged but never executed.</summary>
    public static GatewayScenario CreateCronManagement()
        => CreateCronScenario(true);

    private static GatewayScenario CreateCronScenario(bool management)
    {
        var browse = CreateBrowse();
        var listed = CreateCronJob("listed", new { kind = "cron", expr = "0 1 * * *", tz = "America/Chicago" });
        if (management)
            listed["state"] = JsonSerializer.SerializeToNode(new
            {
                lastRunAtMs = CronFixtureRunAtMs, lastRunStatus = "ok", nextRunAtMs = 0
            });
        return new GatewayScenario(browse._sessions, browse._reads, cronJobs:
        [
            listed,
            CreateCronJob("absent", new { kind = "cron", expr = "0 2 * * *" }),
            CreateCronJob("unlisted", new { kind = "cron", expr = "0 3 * * *", tz = "Pacific/Auckland" }),
            CreateCronJob("every", new { kind = "every", everyMs = 1_800_000 })
        ], cronManagement: management);
    }

    public JsonElement[] GetCronJobs()
    {
        var jobs = _cronJobs ?? throw new InvalidOperationException("Cron editing is not enabled.");
        lock (jobs) return jobs.Values.Select(job => JsonSerializer.SerializeToElement(job)).ToArray();
    }

    private static JsonObject CreateCronJob(string id, object schedule) =>
        JsonSerializer.SerializeToNode(new
        {
            id, name = $"Fixture cron {id}", enabled = true, schedule,
            createdAtMs = CronFixtureRunAtMs, updatedAtMs = CronFixtureRunAtMs,
            sessionTarget = "isolated", wakeMode = "now",
            payload = new { kind = "agentTurn", message = "Synthetic cron proof. Never executed." },
            delivery = new { mode = "none" },
            state = new { runningAtMs = 0, nextRunAtMs = 0 }
        })!.AsObject();

    private object RespondCron(string method, JsonElement parameters)
    {
        var jobs = _cronJobs!;
        lock (jobs)
        {
            if (method == "cron.list")
            {
                ValidateProperties(parameters, "includeDisabled");
                return new { jobs = GetCronJobs(), total = jobs.Count, offset = 0, limit = jobs.Count, hasMore = false };
            }
            if (method == "cron.status")
            {
                ValidateProperties(parameters);
                return new { enabled = false, jobs = jobs.Count, storePath = "synthetic-memory-only" };
            }
            if (method is "cron.get" or "cron.scratch.get" or "cron.scratch.set" or "cron.remove"
                or "cron.run" or "cron.runs" or "cron.history")
                return RespondCronManagement(method, parameters, jobs);

            var id = $"created-{jobs.Count}";
            var patch = parameters;
            if (method == "cron.update")
            {
                ValidateProperties(parameters, "id", "patch");
                id = RequiredString(parameters, "id");
                if (!jobs.ContainsKey(id) || !parameters.TryGetProperty("patch", out patch))
                    throw new FixtureRequestException("INVALID_PARAMS", "Unknown fixture job or missing patch.");
            }
            ValidateProperties(patch, "name", "enabled", "schedule", "sessionTarget", "wakeMode",
                "payload", "delivery", "deleteAfterRun");
            if (method == "cron.add" || patch.TryGetProperty("name", out _))
                _ = RequiredString(patch, "name");
            _ = OptionalBoolean(patch, "enabled");
            var hasSchedule = patch.TryGetProperty("schedule", out var schedule);
            if (hasSchedule) ValidateCronSchedule(schedule);
            else if (method == "cron.add")
                throw new FixtureRequestException("INVALID_PARAMS", "schedule is required.");
            if (_cronManagement)
            {
                foreach (var field in new[] { "sessionTarget", "wakeMode", "payload" })
                    if (method == "cron.add" && !patch.TryGetProperty(field, out _))
                        throw new FixtureRequestException("INVALID_PARAMS", $"{field} is required.");
                if (patch.TryGetProperty("sessionTarget", out _)
                    && RequiredString(patch, "sessionTarget") is not ("main" or "isolated" or "current"))
                    throw new FixtureRequestException("INVALID_PARAMS", "Unsupported fixture session target.");
                if (patch.TryGetProperty("wakeMode", out _) && RequiredString(patch, "wakeMode") is not ("now" or "next-heartbeat"))
                    throw new FixtureRequestException("INVALID_PARAMS", "Invalid wake mode.");
                if (patch.TryGetProperty("payload", out var payload))
                {
                    var kind = RequiredString(payload, "kind");
                    var textField = kind switch
                    {
                        "agentTurn" => "message",
                        "systemEvent" => "text",
                        _ => throw new FixtureRequestException("INVALID_PARAMS", "Unsupported fixture payload.")
                    };
                    ValidateProperties(payload, "kind", textField);
                    _ = RequiredString(payload, textField);
                }
                if (patch.TryGetProperty("delivery", out var delivery))
                {
                    ValidateProperties(delivery, "mode", "channel");
                    if (RequiredString(delivery, "mode") is not ("none" or "announce"))
                        throw new FixtureRequestException("INVALID_PARAMS", "Unsupported fixture delivery mode.");
                    _ = OptionalString(delivery, "channel");
                }
                _ = OptionalBoolean(patch, "deleteAfterRun");
            }

            var job = method == "cron.update" ? jobs[id].DeepClone().AsObject() : CreateCronJob(id, schedule);
            foreach (var field in patch.EnumerateObject())
                job[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            jobs[id] = job;
            return JsonSerializer.SerializeToElement(job);
        }
    }

    private object RespondCronManagement(string method, JsonElement p, Dictionary<string, JsonObject> jobs)
    {
        var idProperty = method == "cron.run" ? "jobId" : "id";
        ValidateProperties(p, method switch
        {
            "cron.scratch.set" => ["id", "content", "expectedRevision"],
            "cron.run" => ["jobId", "mode"],
            "cron.runs" => ["id", "limit", "offset"],
            "cron.history" => ["id", "runId", "runAtMs", "cursor", "limit"],
            _ => ["id"]
        });
        var id = RequiredString(p, idProperty);
        if (!jobs.TryGetValue(id, out var job))
            throw new FixtureRequestException("INVALID_PARAMS", "Unknown fixture cron job.");
        if (method == "cron.get") return JsonSerializer.SerializeToElement(job);
        if (method == "cron.remove")
        {
            jobs.Remove(id);
            _cronScratch.Remove(id);
            return new { ok = true, removed = true };
        }
        if (method == "cron.run")
        {
            var mode = OptionalString(p, "mode");
            if (mode is not (null or "force" or "due"))
                throw new FixtureRequestException("INVALID_PARAMS", "Invalid run mode.");
            return mode == "due"
                ? (object)new { ok = true, ran = false, reason = "not-due" }
                : new { ok = true, enqueued = true, runId = $"fixture:{id}:manual" };
        }
        if (method.StartsWith("cron.scratch.", StringComparison.Ordinal))
        {
            var current = _cronScratch.GetValueOrDefault(id);
            if (method == "cron.scratch.set")
            {
                if (!p.TryGetProperty("content", out var content) || content.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
                    || !p.TryGetProperty("expectedRevision", out var expected) || !expected.TryGetInt64(out var revision) || revision < 0)
                    throw new FixtureRequestException("INVALID_PARAMS", "Scratch content and expected revision required.");
                var value = content.GetString();
                if (value is not null && Encoding.UTF8.GetByteCount(value) > CronScratchMaxBytes)
                    throw new FixtureRequestException("INVALID_PARAMS", "Scratch exceeds byte limit.");
                if (revision != current.Revision)
                    return new { ok = false, reason = "revision-conflict", currentRevision = current.Revision };
                current = (value, current.Revision + 1);
                _cronScratch[id] = current;
            }
            var scratch = current.Content is null ? null : new
            {
                content = current.Content, revision = current.Revision, updatedAtMs = CronFixtureRunAtMs
            };
            return method == "cron.scratch.get"
                ? (object)new { currentRevision = current.Revision, maxBytes = CronScratchMaxBytes, scratch }
                : new { ok = true, currentRevision = current.Revision, maxBytes = CronScratchMaxBytes, scratch };
        }
        if (method == "cron.runs")
        {
            var limit = PositiveInt(p, "limit", 20);
            var offset = p.TryGetProperty("offset", out var offsetEl) && offsetEl.TryGetInt32(out var value) ? value : 0;
            if (offset < 0 || limit > 200)
                throw new FixtureRequestException("INVALID_PARAMS", "Invalid runs pagination.");
            object[] entries = id == "listed" && offset == 0 ? [new
            {
                jobId = id, runId = CronFixtureRunId, runAtMs = CronFixtureRunAtMs, ts = CronFixtureRunAtMs,
                action = "finished", status = "ok", summary = "Synthetic cron summary.", durationMs = 12
            }] : [];
            return new { entries, total = id == "listed" ? 1 : 0, offset, limit, hasMore = false, nextOffset = (int?)null };
        }
        if (id != "listed"
            || (OptionalString(p, "runId") != CronFixtureRunId
                && !(p.TryGetProperty("runAtMs", out var runAt) && runAt.TryGetInt64(out var timestamp) && timestamp == CronFixtureRunAtMs)))
            throw new FixtureRequestException("UNAVAILABLE", "The recorded cron transcript is unavailable.");
        var cursor = OptionalString(p, "cursor");
        if (cursor is not (null or "fixture-listed-page-2"))
            throw new FixtureRequestException("INVALID_PARAMS", "Invalid cron history cursor.");
        if (PositiveInt(p, "limit", 100) > 200)
            throw new FixtureRequestException("INVALID_PARAMS", "Invalid cron history limit.");
        return cursor is null
            ? (object)new { messages = new[] { new { role = "user", content = "Synthetic cron request." } }, nextCursor = "fixture-listed-page-2" }
            : new { messages = new[] { new { role = "assistant", content = "Synthetic cron response." } } };
    }

    private static void ValidateCronSchedule(JsonElement schedule)
    {
        if (schedule.ValueKind != JsonValueKind.Object)
            throw new FixtureRequestException("INVALID_PARAMS", "schedule must be an object.");
        switch (RequiredString(schedule, "kind"))
        {
            case "cron":
                ValidateProperties(schedule, "kind", "expr", "tz");
                _ = RequiredString(schedule, "expr");
                _ = OptionalString(schedule, "tz");
                break;
            case "every":
                ValidateProperties(schedule, "kind", "everyMs");
                if (!schedule.TryGetProperty("everyMs", out var interval)
                    || interval.ValueKind != JsonValueKind.Number
                    || !interval.TryGetInt64(out var milliseconds) || milliseconds <= 0)
                    throw new FixtureRequestException("INVALID_PARAMS", "everyMs must be positive.");
                break;
            case "at":
                ValidateProperties(schedule, "kind", "at");
                if (!DateTimeOffset.TryParse(RequiredString(schedule, "at"), out _))
                    throw new FixtureRequestException("INVALID_PARAMS", "at must be a timestamp.");
                break;
            default:
                throw new FixtureRequestException("INVALID_PARAMS", "Unknown schedule kind.");
        }
    }
}
