using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    private readonly Dictionary<string, JsonObject>? _cronJobs;

    /// <summary>Opt-in, in-memory cron editing only. Jobs are never scheduled or executed.</summary>
    public static GatewayScenario CreateCronEditing()
    {
        var browse = CreateBrowse();
        return new(browse._sessions, browse._reads, cronJobs:
        [
            CreateCronJob("listed", new { kind = "cron", expr = "0 1 * * *", tz = "America/Chicago" }),
            CreateCronJob("absent", new { kind = "cron", expr = "0 2 * * *" }),
            CreateCronJob("unlisted", new { kind = "cron", expr = "0 3 * * *", tz = "Pacific/Auckland" }),
            CreateCronJob("every", new { kind = "every", everyMs = 1_800_000 })
        ]);
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

            var job = method == "cron.update" ? jobs[id].DeepClone().AsObject() : CreateCronJob(id, schedule);
            foreach (var field in patch.EnumerateObject())
                job[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            jobs[id] = job;
            return JsonSerializer.SerializeToElement(job);
        }
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
