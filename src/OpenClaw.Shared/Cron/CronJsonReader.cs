using System.Text.Json;

namespace OpenClaw.Shared.Cron;

/// <summary>
/// Tolerant readers for cron, MCP Events and conversation RPC responses.
/// Missing or mistyped optional fields become null rather than failing the page;
/// rows without the fields required to address them are skipped.
/// </summary>
public static class CronJsonReader
{
    public static CronListPage ParseListPage(JsonElement payload, int requestedOffset = 0)
    {
        var source = payload.ValueKind == JsonValueKind.Array
            ? payload
            : payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("jobs", out var jobsEl) ? jobsEl : default;
        var jobs = new List<CronJob>();
        if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
            {
                if (ParseJob(item) is { } job)
                    jobs.Add(job);
            }
        }

        var offset = Int(payload, "offset") ?? requestedOffset;
        var total = Int(payload, "total") ?? offset + jobs.Count;
        var hasMore = Bool(payload, "hasMore") ?? false;
        var nextOffset = Int(payload, "nextOffset") ?? (hasMore ? offset + jobs.Count : null);
        return new CronListPage(jobs, total, offset, Int(payload, "limit") ?? jobs.Count, hasMore, nextOffset);
    }

    public static CronJob? ParseJob(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        // cron.add with declarationKey answers { created, updated, job }.
        if (item.TryGetProperty("job", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            return ParseJob(wrapped);

        var id = Str(item, "id");
        if (string.IsNullOrWhiteSpace(id))
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
            SessionKey = Str(item, "sessionKey"),
            Enabled = Bool(item, "enabled") ?? false,
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
            SessionTarget = Str(item, "sessionTarget") ?? "isolated",
            WakeMode = Str(item, "wakeMode") ?? "now",
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
            ? new CronAutoDisabled(Str(ad, "reason") ?? "consecutive-failures", Long(ad, "atMs") ?? 0, Int(ad, "consecutiveErrors") ?? 0)
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
        var entries = new List<CronRunEntry>();
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("entries", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (ParseRunEntry(item) is { } entry)
                    entries.Add(entry);
            }
        }
        var offset = Int(payload, "offset") ?? requestedOffset;
        var hasMore = Bool(payload, "hasMore") ?? false;
        return new CronRunsPage(
            entries,
            Int(payload, "total") ?? offset + entries.Count,
            offset,
            Int(payload, "limit") ?? entries.Count,
            hasMore,
            Int(payload, "nextOffset") ?? (hasMore ? offset + entries.Count : null));
    }

    public static CronRunEntry? ParseRunEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Str(item, "jobId") is not { } jobId)
            return null;
        JsonElement usage = item.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        return new CronRunEntry
        {
            Ts = Long(item, "ts") ?? 0,
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
            RunId = Str(item, "runId"),
            RunAtMs = Long(item, "runAtMs"),
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

    public static CronHistoryPage ParseHistoryPage(JsonElement payload)
    {
        var messages = new List<CronTranscriptMessage>();
        var history = OpenClawGatewayClient.ParseChatHistory(payload, "cron-history");
        foreach (var message in history.Messages)
        {
            long? ts = message.Ts > 0 ? message.Ts : null;
            var isToolResult = message.Role is "toolResult" or "tool";
            var toolName = message.ToolContent.FirstOrDefault()?.ToolName;
            if (!string.IsNullOrWhiteSpace(message.Text))
                messages.Add(new CronTranscriptMessage(isToolResult ? "tool" : message.Role, message.Text, ts, isToolResult ? toolName : null));
            foreach (var tool in message.ToolContent)
            {
                if (tool.Kind == ChatToolContentKind.Call)
                    messages.Add(new CronTranscriptMessage("tool", tool.Text ?? "", ts, tool.ToolName));
                else if (isToolResult && string.IsNullOrWhiteSpace(message.Text) && !string.IsNullOrWhiteSpace(tool.Text))
                    messages.Add(new CronTranscriptMessage("tool", tool.Text!, ts, tool.ToolName));
            }
        }
        return new CronHistoryPage(messages, Str(payload, "nextCursor"));
    }

    public static CronScratchSnapshot ParseScratch(JsonElement payload)
    {
        var scratch = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("scratch", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var exists = scratch.ValueKind == JsonValueKind.Object;
        return new CronScratchSnapshot(
            exists,
            exists ? Str(scratch, "content") ?? "" : "",
            exists ? Long(scratch, "revision") ?? 0 : 0,
            exists ? Long(scratch, "updatedAtMs") : null,
            Long(payload, "currentRevision") ?? 0,
            Long(payload, "maxBytes") ?? 262_144);
    }

    public static CronScratchSetResult ParseScratchSet(JsonElement payload)
    {
        var ok = Bool(payload, "ok") != false;
        var conflict = !ok && Str(payload, "reason") == "revision-conflict";
        var current = Long(payload, "currentRevision") ?? 0;
        return new CronScratchSetResult(ok, conflict, ok ? ParseScratch(payload) : null, current);
    }

    public static McpEventsPage ParseMcpEvents(JsonElement payload)
    {
        var events = new List<McpEventDefinition>();
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Str(item, "name") is not { } name)
                    continue;
                var schema = item.TryGetProperty("inputSchema", out var sc) && sc.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False
                    ? JsonSerializer.Serialize(sc, IndentedJson)
                    : null;
                events.Add(new McpEventDefinition(name, Str(item, "description"), schema));
            }
        }
        return new McpEventsPage(Str(payload, "serverName") ?? "", events, Str(payload, "nextCursor"));
    }

    public static IReadOnlyList<ConversationTarget> ParseConversations(JsonElement payload)
    {
        var result = new List<ConversationTarget>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("conversations", out var list) || list.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Str(item, "target") is not { } target || Str(item, "channel") is not { } channel)
                continue;
            result.Add(new ConversationTarget(
                Str(item, "conversationRef") ?? $"{channel}:{target}",
                channel,
                Str(item, "accountId") ?? "",
                Str(item, "kind") ?? "direct",
                target,
                Str(item, "label"),
                Long(item, "lastSeenAt") ?? 0));
        }
        return result.OrderByDescending(c => c.LastSeenAt).ToList();
    }

    /// <summary>Enabled MCP server names from <c>config.mcp.servers</c> in a <c>config.get</c> snapshot.</summary>
    public static IReadOnlyList<string> ParseEnabledMcpServers(JsonElement configPayload)
    {
        var config = configPayload.ValueKind == JsonValueKind.Object
            && configPayload.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : configPayload;
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("mcp", out var mcp) || mcp.ValueKind != JsonValueKind.Object
            || !mcp.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        return servers.EnumerateObject()
            .Where(s => s.Value.ValueKind == JsonValueKind.Object && Bool(s.Value, "enabled") != false)
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Configured model references from <c>config.agents</c>, used as model suggestions.</summary>
    public static IReadOnlyList<string> ParseConfiguredModels(JsonElement configPayload)
    {
        var config = configPayload.ValueKind == JsonValueKind.Object
            && configPayload.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : configPayload;
        var models = new SortedSet<string>(StringComparer.Ordinal);
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Object)
            return models.ToList();
        if (agents.TryGetProperty("defaults", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
        {
            AddModelRefs(defaults, "model", models);
            if (defaults.TryGetProperty("models", out var catalog) && catalog.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in catalog.EnumerateObject())
                    if (!string.IsNullOrWhiteSpace(entry.Name)) models.Add(entry.Name.Trim());
            }
        }
        if (agents.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in entries.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object) AddModelRefs(entry.Value, "model", models);
        }
        return models.ToList();
    }

    private static void AddModelRefs(JsonElement owner, string property, ISet<string> into)
    {
        if (!owner.TryGetProperty(property, out var value))
            return;
        switch (value.ValueKind)
        {
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()):
                into.Add(value.GetString()!.Trim());
                break;
            case JsonValueKind.Object:
                if (Str(value, "primary") is { } primary && primary.Trim().Length > 0) into.Add(primary.Trim());
                foreach (var fallback in Strings(value, "fallbacks") ?? Array.Empty<string>())
                    if (fallback.Trim().Length > 0) into.Add(fallback.Trim());
                break;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

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
}
