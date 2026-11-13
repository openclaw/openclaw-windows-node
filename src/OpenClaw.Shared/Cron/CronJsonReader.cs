using System.Text.Json;

namespace OpenClaw.Shared.Cron;

/// <summary>
/// Tolerant readers for cron, MCP Events and conversation RPC responses.
/// Missing or mistyped optional fields become null rather than failing the page.
/// A job list with a missing or unreadable job fails, so it never reads as a smaller inventory;
/// run entries without an identity are skipped and leave the run total unknown.
/// </summary>
public static class CronJsonReader
{
    public const string UnreadableJobMessage = "The Gateway returned an unreadable automation.";
    public const string UnreadableListMessage = "The Gateway returned an unreadable automation list.";
    public const string UnreadableRunsMessage = "The Gateway returned an unreadable run history.";

    public static CronListPage ParseListPage(JsonElement payload, int requestedOffset = 0)
    {
        var source = payload.ValueKind == JsonValueKind.Array
            ? payload
            : payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("jobs", out var jobsEl) ? jobsEl : default;
        if (source.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(UnreadableListMessage);
        var jobs = source.EnumerateArray()
            .Select(item => ParseJob(item) ?? throw new InvalidOperationException(UnreadableJobMessage))
            .ToList();

        var offset = Int(payload, "offset") ?? requestedOffset;
        var nextOffset = Int(payload, "nextOffset");
        var hasMore = Bool(payload, "hasMore") ?? nextOffset is not null;
        // Without a Gateway total, only the final page says how many jobs match.
        var total = Int(payload, "total") ?? (hasMore ? null : offset + jobs.Count);
        return new CronListPage(jobs, total, offset, Int(payload, "limit") ?? jobs.Count, hasMore,
            nextOffset ?? (hasMore ? offset + jobs.Count : null));
    }

    public static CronJob? ParseJob(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        // cron.add with declarationKey answers { created, updated, job }.
        if (item.TryGetProperty("job", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            return ParseJob(wrapped);

        var id = Str(item, "id");
        var enabled = Bool(item, "enabled");
        // The Gateway always states both; without them a job cannot be addressed or shown as on or paused.
        if (string.IsNullOrWhiteSpace(id) || enabled is null)
            return null;

        var state = item.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.Object
            ? ParseState(stateEl)
            : CronJobState.Empty;
        // Convenience projections duplicate state fields on some Gateway versions.
        state = state with
        {
            NextRunAtMs = state.NextRunAtMs ?? Long(item, "nextRunAtMs"),
            LastRunAtMs = state.LastRunAtMs ?? Long(item, "lastRunAtMs"),
        };

        return new CronJob
        {
            Id = id!,
            Name = Str(item, "name") ?? id!,
            DisplayName = Str(item, "displayName"),
            Description = Str(item, "description"),
            AgentId = Str(item, "agentId"),
            EffectiveAgentId = Str(item, "effectiveAgentId"),
            SessionKey = Str(item, "sessionKey"),
            Enabled = enabled.Value,
            DeleteAfterRun = Bool(item, "deleteAfterRun"),
            CreatedAtMs = Long(item, "createdAtMs"),
            UpdatedAtMs = Long(item, "updatedAtMs"),
            ConfigRevision = Str(item, "configRevision"),
            Schedule = ParseSchedule(item.TryGetProperty("schedule", out var s) ? s : default),
            ActiveHours = item.TryGetProperty("activeHours", out var ah) && ah.ValueKind == JsonValueKind.Object
                && Str(ah, "start") is { } start && Str(ah, "end") is { } end
                    ? new CronActiveHours(start, end, Str(ah, "timezone"))
                    : null,
            IdleOnly = Bool(item, "idleOnly"),
            Trigger = item.TryGetProperty("trigger", out var tr) && tr.ValueKind == JsonValueKind.Object
                && Str(tr, "script") is { } script
                    ? new CronTrigger(script, Bool(tr, "once") == true)
                    : null,
            SessionTarget = Str(item, "sessionTarget"),
            WakeMode = Str(item, "wakeMode"),
            Payload = ParsePayload(item.TryGetProperty("payload", out var p) ? p : default, Str(item, "description")),
            Delivery = item.TryGetProperty("delivery", out var d) && d.ValueKind == JsonValueKind.Object ? ParseDelivery(d) : null,
            FailureAlert = item.TryGetProperty("failureAlert", out var fa) ? ParseFailureAlert(fa) : null,
            State = state,
            Pacing = item.TryGetProperty("pacing", out var pacing) && pacing.ValueKind == JsonValueKind.Object ? pacing.Clone() : null,
            DeliveryPreview = item.TryGetProperty("deliveryPreview", out var dp) && dp.ValueKind == JsonValueKind.Object
                && Str(dp, "label") is { } label
                    ? new CronDeliveryPreview(label, Str(dp, "detail"))
                    : null,
        };
    }

    public static CronSchedule ParseSchedule(JsonElement schedule)
    {
        if (schedule.ValueKind == JsonValueKind.String)
        {
            // Legacy Gateways sent bare cron expressions.
            var expr = schedule.GetString() ?? "";
            return new CronSchedule(CronScheduleKind.Cron, "cron", Expr: expr) { Raw = schedule.Clone() };
        }
        if (schedule.ValueKind != JsonValueKind.Object)
            return new CronSchedule(CronScheduleKind.Unknown, "");

        var rawKind = Str(schedule, "kind") ?? "";
        var kind = rawKind switch
        {
            "at" => CronScheduleKind.At,
            "every" => CronScheduleKind.Every,
            "cron" => CronScheduleKind.Cron,
            "event" => CronScheduleKind.Event,
            "on-exit" => CronScheduleKind.OnExit,
            "stream" => CronScheduleKind.Stream,
            _ => CronScheduleKind.Unknown,
        };
        string? server = null, name = null, args = null;
        if (kind == CronScheduleKind.Event && schedule.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            server = Str(options, "server");
            name = Str(options, "name");
            args = options.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(a, IndentedJson)
                : "{}";
        }
        string? command = Str(schedule, "command");
        if (command is null && schedule.TryGetProperty("command", out var argv) && argv.ValueKind == JsonValueKind.Array)
            command = string.Join(' ', argv.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));

        return new CronSchedule(
            kind,
            rawKind,
            At: Str(schedule, "at"),
            EveryMs: Long(schedule, "everyMs"),
            AnchorMs: Long(schedule, "anchorMs"),
            Expr: Str(schedule, "expr"),
            Tz: Str(schedule, "tz"),
            StaggerMs: Long(schedule, "staggerMs"),
            EventSource: kind == CronScheduleKind.Event ? Str(schedule, "source") : null,
            EventServer: server,
            EventName: name,
            EventArgumentsJson: args,
            Command: command,
            Cwd: Str(schedule, "cwd"))
        { Raw = schedule.Clone() };
    }

    public static CronPayload ParsePayload(JsonElement payload, string? fallbackText = null)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new CronPayload(CronPayloadKind.Unknown, "", fallbackText ?? "");

        var rawKind = Str(payload, "kind") ?? "";
        var kind = rawKind switch
        {
            "systemEvent" => CronPayloadKind.SystemEvent,
            "agentTurn" => CronPayloadKind.AgentTurn,
            "command" => CronPayloadKind.Command,
            "script" => CronPayloadKind.Script,
            _ => CronPayloadKind.Unknown,
        };
        var text = kind switch
        {
            CronPayloadKind.SystemEvent => Str(payload, "text"),
            CronPayloadKind.AgentTurn => Str(payload, "message"),
            CronPayloadKind.Command => payload.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array
                ? string.Join(' ', argv.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
                : null,
            CronPayloadKind.Script => Str(payload, "script"),
            _ => Str(payload, "message") ?? Str(payload, "text"),
        };
        return new CronPayload(
            kind,
            rawKind,
            text ?? fallbackText ?? "",
            Model: Str(payload, "model"),
            Thinking: Str(payload, "thinking"),
            TimeoutSeconds: Double(payload, "timeoutSeconds"),
            LightContext: Bool(payload, "lightContext"),
            IncludeReasoning: Bool(payload, "includeReasoning"),
            SkipIfScratchEmpty: Bool(payload, "skipIfScratchEmpty"),
            ToolsAllow: Strings(payload, "toolsAllow"),
            Fallbacks: Strings(payload, "fallbacks"));
    }

    public static CronDelivery ParseDelivery(JsonElement delivery) =>
        new(
            Str(delivery, "mode") ?? "none",
            Channel: Str(delivery, "channel"),
            To: Str(delivery, "to"),
            AccountId: Str(delivery, "accountId"),
            ThreadId: delivery.TryGetProperty("threadId", out var thread) && thread.ValueKind == JsonValueKind.Number
                ? thread.GetRawText()
                : Str(delivery, "threadId"),
            BestEffort: Bool(delivery, "bestEffort"),
            Target: Str(delivery, "target"),
            DirectPolicy: Str(delivery, "directPolicy"))
        {
            CompletionDestination = delivery.TryGetProperty("completionDestination", out var cd) && cd.ValueKind == JsonValueKind.Object ? cd.Clone() : null,
            FailureDestination = delivery.TryGetProperty("failureDestination", out var fd) && fd.ValueKind == JsonValueKind.Object ? fd.Clone() : null,
        };

    public static CronFailureAlert? ParseFailureAlert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.False => CronFailureAlert.Off,
        JsonValueKind.Object => new CronFailureAlert(
            false,
            After: Int(value, "after"),
            CooldownMs: Long(value, "cooldownMs"),
            Channel: Str(value, "channel"),
            To: Str(value, "to"),
            Mode: Str(value, "mode"),
            AccountId: Str(value, "accountId"),
            IncludeSkipped: Bool(value, "includeSkipped")),
        _ => null,
    };

    public static CronJobState ParseState(JsonElement state) => new()
    {
        NextRunAtMs = Long(state, "nextRunAtMs"),
        RunningAtMs = Long(state, "runningAtMs"),
        LastRunAtMs = Long(state, "lastRunAtMs"),
        LastRunStatus = Str(state, "lastRunStatus") ?? Str(state, "lastStatus")
            ?? (Bool(state, "lastRunOk") is { } ok ? ok ? "ok" : "error" : null),
        LastError = Str(state, "lastError"),
        LastDurationMs = Long(state, "lastDurationMs"),
        ConsecutiveErrors = Int(state, "consecutiveErrors"),
        ScheduleErrorCount = Int(state, "scheduleErrorCount"),
        AutoDisabled = state.TryGetProperty("autoDisabled", out var ad) && ad.ValueKind == JsonValueKind.Object
            ? new CronAutoDisabled(Str(ad, "reason"), Long(ad, "atMs"), Int(ad, "consecutiveErrors"))
            : null,
        LastDeliveryStatus = Str(state, "lastDeliveryStatus"),
        LastDeliveryError = Str(state, "lastDeliveryError"),
        DeliverySuppressionReason = Str(state, "deliverySuppressionReason"),
        TriggerEvalCount = Int(state, "triggerEvalCount"),
        LastTriggerEvalAtMs = Long(state, "lastTriggerEvalAtMs"),
        LastTriggerFireAtMs = Long(state, "lastTriggerFireAtMs"),
        SourceIdentity = Str(state, "sourceIdentity"),
        StreamStatus = Str(state, "streamStatus"),
        StreamError = Str(state, "streamError"),
    };

    public static CronSchedulerStatus ParseStatus(JsonElement payload) =>
        payload.ValueKind != JsonValueKind.Object
            ? new CronSchedulerStatus(null, null, null, null)
            : new CronSchedulerStatus(
                Bool(payload, "enabled"),
                Long(payload, "nextWakeAtMs"),
                Int(payload, "jobs"),
                Str(payload, "storePath"));

    public static CronRunsPage ParseRunsPage(JsonElement payload, int requestedOffset = 0)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("entries", out var list)
            || list.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(UnreadableRunsMessage);
        var entries = new List<CronRunEntry>();
        var skipped = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (ParseRunEntry(item) is { } entry)
                entries.Add(entry);
            else
                skipped++;
        }
        var offset = Int(payload, "offset") ?? requestedOffset;
        var nextOffset = Int(payload, "nextOffset");
        var hasMore = Bool(payload, "hasMore") ?? nextOffset is not null;
        return new CronRunsPage(
            entries,
            // Without a Gateway total, only a final page with every entry readable gives the count.
            Int(payload, "total") ?? (hasMore || skipped > 0 ? null : offset + entries.Count),
            offset,
            Int(payload, "limit") ?? entries.Count,
            hasMore,
            nextOffset ?? (hasMore ? offset + entries.Count : null));
    }

    public static CronRunEntry? ParseRunEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Str(item, "jobId") is not { } jobId)
            return null;
        var ts = Long(item, "ts");
        var runAtMs = Long(item, "runAtMs");
        var runId = Str(item, "runId");
        // Without a run id or any time there is no stable identity or real time to show.
        if (ts is null && runAtMs is null && string.IsNullOrWhiteSpace(runId))
            return null;
        JsonElement usage = item.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        return new CronRunEntry
        {
            Ts = ts,
            JobId = jobId,
            JobName = Str(item, "jobName"),
            Status = Str(item, "status"),
            Error = Str(item, "error"),
            Summary = Str(item, "summary"),
            DiagnosticsSummary = item.TryGetProperty("diagnostics", out var diag) && diag.ValueKind == JsonValueKind.Object
                ? Str(diag, "summary")
                : null,
            DeliveryStatus = Str(item, "deliveryStatus"),
            Delivered = Bool(item, "delivered"),
            DeliveryError = Str(item, "deliveryError"),
            DeliverySuppressionReason = Str(item, "deliverySuppressionReason"),
            SessionId = Str(item, "sessionId"),
            SessionKey = Str(item, "sessionKey"),
            RunId = runId,
            RunAtMs = runAtMs,
            DurationMs = Long(item, "durationMs"),
            NextRunAtMs = Long(item, "nextRunAtMs"),
            Model = Str(item, "model"),
            Provider = Str(item, "provider"),
            InputTokens = usage.ValueKind == JsonValueKind.Object ? Long(usage, "input_tokens") : null,
            OutputTokens = usage.ValueKind == JsonValueKind.Object ? Long(usage, "output_tokens") : null,
            TotalTokens = usage.ValueKind == JsonValueKind.Object ? Long(usage, "total_tokens") : null,
            TriggerFired = Bool(item, "triggerFired"),
            Raw = item.Clone(),
        };
    }

    internal static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static long? Long(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.TryGetInt64(out var l) ? l : (long)v.GetDouble()
            : null;

    internal static int? Int(JsonElement el, string name) =>
        Long(el, name) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    internal static double? Double(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    internal static bool? Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static IReadOnlyList<string>? Strings(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : null;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
}
