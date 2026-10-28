using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Cron;

public enum CronFailureAlertMode { Inherit, Disabled, Custom }

public enum CronFormField
{
    Name,
    ScheduleAt,
    EveryAmount,
    CronExpr,
    CronTz,
    StaggerAmount,
    EventServer,
    EventName,
    EventArguments,
    PayloadText,
    TimeoutSeconds,
    DeliveryMode,
    DeliveryTo,
    ActiveHoursStart,
    ActiveHoursEnd,
    ActiveHoursTimezone,
    TriggerScript,
    FailureAlertAfter,
    FailureAlertCooldown,
}

/// <summary>Validation outcomes; the UI owns their localized wording.</summary>
public enum CronFormError
{
    NameRequired,
    ScheduleAtInvalid,
    EveryAmountInvalid,
    TriggerIntervalTooShort,
    CronExprRequired,
    TimezoneInvalid,
    StaggerAmountInvalid,
    EventAgentTurnRequired,
    EventServerRequired,
    EventNameRequired,
    EventArgumentsInvalid,
    TriggerScriptPayloadUnsupported,
    TriggerScheduleUnsupported,
    TriggerScriptRequired,
    SystemTextRequired,
    AgentMessageRequired,
    TimeoutInvalid,
    DeliveryModeRequired,
    WebhookUrlRequired,
    WebhookUrlInvalid,
    ActiveHoursTimeInvalid,
    ActiveHoursTimezoneInvalid,
    FailureAlertAfterInvalid,
    FailureAlertCooldownInvalid,
}

/// <summary>
/// Editable automation definition. Every field is plain text or a flag so the native
/// editor can bind it directly; <see cref="BuildDefinition"/> converts it to the
/// Gateway's add/update shape, including explicit nulls that clear saved values.
/// </summary>
public sealed record CronJobForm
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string SessionKey { get; init; } = "";
    public bool ClearAgent { get; init; }
    public bool Enabled { get; init; } = true;
    public bool DeleteAfterRun { get; init; }

    public CronScheduleKind ScheduleKind { get; init; } = CronScheduleKind.Every;
    public DateTimeOffset? ScheduleAt { get; init; }
    public string EveryAmount { get; init; } = "30";
    public CronDurationUnit EveryUnit { get; init; } = CronDurationUnit.Minutes;
    public string CronExpr { get; init; } = "0 7 * * *";
    public string CronTz { get; init; } = "";
    public bool ScheduleExact { get; init; }
    public string StaggerAmount { get; init; } = "";
    public CronDurationUnit StaggerUnit { get; init; } = CronDurationUnit.Seconds;
    public string EventSource { get; init; } = CronProtocol.McpEventsSource;
    public string EventServer { get; init; } = "";
    public string EventName { get; init; } = "";
    public string EventArguments { get; init; } = "{}";

    public bool ActiveHoursEnabled { get; init; }
    public string ActiveHoursStart { get; init; } = "09:00";
    public string ActiveHoursEnd { get; init; } = "17:00";
    public string ActiveHoursTimezone { get; init; } = "";
    public bool IdleOnly { get; init; }

    public bool TriggerEnabled { get; init; }
    public string TriggerScript { get; init; } = "";
    public bool TriggerOnce { get; init; }

    public string SessionTarget { get; init; } = "isolated";
    public string WakeMode { get; init; } = "now";

    public CronPayloadKind PayloadKind { get; init; } = CronPayloadKind.AgentTurn;
    public bool PayloadLocked { get; init; }
    public string PayloadText { get; init; } = "";
    public string PayloadModel { get; init; } = "";
    public string PayloadThinking { get; init; } = "";
    public bool PayloadLightContext { get; init; }
    public bool PayloadIncludeReasoning { get; init; }
    public bool PayloadSkipIfScratchEmpty { get; init; }
    public string TimeoutSeconds { get; init; } = "";

    /// <summary>"none", "announce", "webhook", or empty when a legacy job has no canonical mode.</summary>
    public string DeliveryMode { get; init; } = "none";
    public string DeliveryChannel { get; init; } = CronProtocol.ChannelLast;
    public string DeliveryTo { get; init; } = "";
    public string DeliveryAccountId { get; init; } = "";
    public bool DeliveryBestEffort { get; init; }
    public string? DeliveryThreadId { get; init; }
    /// <summary>"" for a channel destination, or "owner" for the owner's direct message.</summary>
    public string DeliveryTarget { get; init; } = "";
    /// <summary>"", "allow", or "block".</summary>
    public string DeliveryDirectPolicy { get; init; } = "";
    public JsonElement? DeliveryCompletionDestination { get; init; }
    public JsonElement? DeliveryFailureDestination { get; init; }

    public CronFailureAlertMode FailureAlertMode { get; init; } = CronFailureAlertMode.Inherit;
    public string FailureAlertAfter { get; init; } = "";
    public string FailureAlertCooldownSeconds { get; init; } = "";
    public string FailureAlertChannel { get; init; } = CronProtocol.ChannelLast;
    public string FailureAlertTo { get; init; } = "";
    public string FailureAlertDeliveryMode { get; init; } = "";
    public string FailureAlertAccountId { get; init; } = "";

    public static CronJobForm CreateDefault(string? agentId) => new() { AgentId = agentId ?? "" };

    /// <summary>Starter automations reuse the editor; nothing is saved until the user submits.</summary>
    public static CronJobForm FromSuggestion(CronSuggestion suggestion, string name, string prompt, string? agentId) =>
        CreateDefault(agentId) with
        {
            Name = name,
            PayloadText = prompt,
            PayloadKind = CronPayloadKind.AgentTurn,
            SessionTarget = "isolated",
            WakeMode = "now",
            DeleteAfterRun = false,
            Enabled = true,
            ScheduleKind = suggestion.ScheduleKind,
            CronExpr = suggestion.CronExpr ?? "0 7 * * *",
            EveryAmount = suggestion.EveryAmount ?? "30",
            EveryUnit = suggestion.EveryUnit ?? CronDurationUnit.Minutes,
        };

    public static CronJobForm FromJob(CronJob job)
    {
        var payload = job.Payload;
        var agentTurn = payload.Kind == CronPayloadKind.AgentTurn ? payload : null;
        var delivery = job.Delivery;
        var alert = job.FailureAlert is { Disabled: false } a ? a : null;
        var form = new CronJobForm
        {
            Name = job.Name,
            Description = job.Description ?? "",
            AgentId = job.AgentId ?? "",
            SessionKey = job.SessionKey ?? "",
            Enabled = job.Enabled,
            DeleteAfterRun = job.DeleteAfterRun ?? job.Schedule.Kind == CronScheduleKind.At,
            ScheduleKind = job.Schedule.Kind,
            ActiveHoursEnabled = job.ActiveHours is not null,
            ActiveHoursStart = job.ActiveHours?.Start ?? "09:00",
            ActiveHoursEnd = job.ActiveHours?.End ?? "17:00",
            ActiveHoursTimezone = job.ActiveHours?.Timezone ?? "",
            IdleOnly = job.IdleOnly == true,
            PayloadSkipIfScratchEmpty = agentTurn?.SkipIfScratchEmpty == true,
            DeliveryTarget = delivery is { Mode: not "webhook" } ? delivery.Target ?? "" : "",
            DeliveryDirectPolicy = delivery is { Mode: not "webhook" } ? delivery.DirectPolicy ?? "" : "",
            TriggerEnabled = job.Trigger is not null,
            TriggerScript = job.Trigger?.Script ?? "",
            TriggerOnce = job.Trigger?.Once == true,
            SessionTarget = job.SessionTarget,
            WakeMode = job.WakeMode,
            PayloadKind = payload.Kind,
            PayloadLocked = payload.IsReadOnly,
            PayloadText = payload.Text,
            PayloadModel = agentTurn?.Model ?? "",
            PayloadThinking = agentTurn?.Thinking ?? "",
            PayloadLightContext = agentTurn?.LightContext == true,
            PayloadIncludeReasoning = agentTurn?.IncludeReasoning == true,
            TimeoutSeconds = agentTurn?.TimeoutSeconds is { } timeout ? timeout.ToString(CultureInfo.InvariantCulture) : "",
            DeliveryMode = delivery is null ? "none" : IsCanonicalDeliveryMode(delivery.Mode) ? delivery.Mode : "",
            DeliveryChannel = delivery?.Channel ?? CronProtocol.ChannelLast,
            DeliveryTo = delivery?.To ?? "",
            DeliveryAccountId = delivery?.AccountId ?? "",
            DeliveryBestEffort = delivery?.BestEffort ?? false,
            DeliveryThreadId = delivery?.ThreadId,
            DeliveryCompletionDestination = delivery?.Mode == "announce" ? delivery.CompletionDestination : null,
            DeliveryFailureDestination = delivery?.FailureDestination,
            FailureAlertMode = job.FailureAlert is { Disabled: true }
                ? CronFailureAlertMode.Disabled
                : alert is not null ? CronFailureAlertMode.Custom : CronFailureAlertMode.Inherit,
            FailureAlertAfter = alert?.After?.ToString(CultureInfo.InvariantCulture) ?? "",
            FailureAlertCooldownSeconds = alert?.CooldownMs is { } cooldown ? CronDurations.MsToSecondsText(cooldown) : "",
            FailureAlertChannel = alert?.Channel ?? CronProtocol.ChannelLast,
            FailureAlertTo = alert?.To ?? "",
            FailureAlertDeliveryMode = alert?.Mode ?? "",
            FailureAlertAccountId = alert?.AccountId ?? "",
        };

        var schedule = job.Schedule;
        switch (schedule.Kind)
        {
            case CronScheduleKind.Event:
                form = form with
                {
                    EventSource = schedule.EventSource ?? CronProtocol.McpEventsSource,
                    EventServer = schedule.EventServer ?? "",
                    EventName = schedule.EventName ?? "",
                    EventArguments = schedule.EventArgumentsJson ?? "{}",
                };
                break;
            case CronScheduleKind.At:
                form = form with { ScheduleAt = TryParseAt(schedule.At) };
                break;
            case CronScheduleKind.Every when schedule.EveryMs is { } everyMs:
                var (amount, unit) = CronDurations.Split(everyMs);
                form = form with { EveryAmount = amount, EveryUnit = unit };
                break;
            case CronScheduleKind.Cron:
                var (exact, staggerAmount, staggerUnit) = CronDurations.SplitStagger(schedule.StaggerMs);
                form = form with
                {
                    CronExpr = schedule.Expr ?? "",
                    CronTz = schedule.Tz ?? "",
                    ScheduleExact = exact,
                    StaggerAmount = staggerAmount,
                    StaggerUnit = staggerUnit,
                };
                break;
        }
        return form.Normalize();
    }

    /// <summary>
    /// A clone keeps the definition but must be saved as a new automation. Command and
    /// script payloads cannot be created here, so a clone of one starts as an empty
    /// assistant task (as on the web page).
    /// </summary>
    public static CronJobForm FromClone(CronJob job, string copyName)
    {
        var form = FromJob(job) with { Name = copyName };
        return form.PayloadLocked
            ? (form with { PayloadLocked = false, PayloadKind = CronPayloadKind.AgentTurn, PayloadText = "" }).Normalize()
            : form;
    }

    /// <summary>
    /// Main-session jobs post timeline messages; other targets run assistant tasks.
    /// <paramref name="sessionTargetChanged"/> keeps the user's target and switches the action instead.
    /// </summary>
    public CronJobForm Normalize(bool sessionTargetChanged = false)
    {
        var form = this;
        if (!form.PayloadLocked && form.PayloadKind == CronPayloadKind.SystemEvent && form.SessionTarget != "main")
        {
            form = sessionTargetChanged
                ? form with { PayloadKind = CronPayloadKind.AgentTurn }
                : form with { SessionTarget = "main" };
        }
        if (form.DeliveryMode != "announce"
            || form.PayloadKind == CronPayloadKind.AgentTurn
            || (form.SessionTarget != "main" && form.PayloadLocked))
        {
            return form;
        }
        return form with { DeliveryMode = "none" };
    }

    /// <summary>
    /// A thread (topic) belongs to one exact delivery route. Keeps it only when the route
    /// is unchanged or <paramref name="next"/> supplies its own thread (upstream
    /// <c>invalidateStaleDeliveryRoute</c>). Called with this as the previous form.
    /// </summary>
    public CronJobForm WithStaleThreadCleared(CronJobForm next)
    {
        static string Agent(CronJobForm f) => f.ClearAgent ? "" : f.AgentId.Trim();
        var routeChanged = next.DeliveryMode != DeliveryMode
            || next.DeliveryTarget != DeliveryTarget
            || next.DeliveryChannel.Trim() != DeliveryChannel.Trim()
            || next.DeliveryAccountId.Trim() != DeliveryAccountId.Trim()
            || next.DeliveryTo.Trim() != DeliveryTo.Trim()
            || Agent(next) != Agent(this);
        return routeChanged && next.DeliveryThreadId == DeliveryThreadId ? next with { DeliveryThreadId = null } : next;
    }

    /// <summary>Applies the schedule-kind defaults the Gateway expects when switching kinds.</summary>
    public CronJobForm WithScheduleKind(CronScheduleKind kind)
    {
        if (kind == ScheduleKind)
            return this;
        var form = this with { ScheduleKind = kind };
        if (kind == CronScheduleKind.At)
            form = form with { DeleteAfterRun = true, ScheduleAt = ScheduleAt ?? NextWholeHour(DateTimeOffset.Now) };
        else if (ScheduleKind == CronScheduleKind.At)
            form = form with { DeleteAfterRun = false };
        if (kind == CronScheduleKind.Event)
            form = form with
            {
                TriggerEnabled = false,
                DeleteAfterRun = false,
                PayloadKind = form.PayloadLocked ? form.PayloadKind : CronPayloadKind.AgentTurn,
                SessionTarget = form.SessionTarget == "main" ? "isolated" : form.SessionTarget,
            };
        return form;
    }

    public bool SupportsAnnounce => PayloadKind == CronPayloadKind.AgentTurn || (SessionTarget == "main" && PayloadLocked);

    public IReadOnlyDictionary<CronFormField, CronFormError> Validate()
    {
        var errors = new Dictionary<CronFormField, CronFormError>();
        if (string.IsNullOrWhiteSpace(Name))
            errors[CronFormField.Name] = CronFormError.NameRequired;

        if (ScheduleKind == CronScheduleKind.Event && PayloadKind != CronPayloadKind.AgentTurn)
            errors[CronFormField.PayloadText] = CronFormError.EventAgentTurnRequired;

        if (ScheduleKind == CronScheduleKind.Event && EventSource == CronProtocol.McpEventsSource)
        {
            if (string.IsNullOrWhiteSpace(EventServer)) errors[CronFormField.EventServer] = CronFormError.EventServerRequired;
            if (string.IsNullOrWhiteSpace(EventName)) errors[CronFormField.EventName] = CronFormError.EventNameRequired;
            if (TryParseEventArguments(EventArguments) is null) errors[CronFormField.EventArguments] = CronFormError.EventArgumentsInvalid;
        }
        else if (ScheduleKind == CronScheduleKind.At)
        {
            if (ScheduleAt is null) errors[CronFormField.ScheduleAt] = CronFormError.ScheduleAtInvalid;
        }
        else if (ScheduleKind == CronScheduleKind.Every)
        {
            var everyMs = CronDurations.ParseMs(EveryAmount, EveryUnit);
            if (everyMs is null) errors[CronFormField.EveryAmount] = CronFormError.EveryAmountInvalid;
            else if (TriggerEnabled && everyMs < CronProtocol.TriggerMinIntervalMs) errors[CronFormField.EveryAmount] = CronFormError.TriggerIntervalTooShort;
        }
        else if (ScheduleKind == CronScheduleKind.Cron)
        {
            if (string.IsNullOrWhiteSpace(CronExpr)) errors[CronFormField.CronExpr] = CronFormError.CronExprRequired;
            if (!IsValidTimezone(CronTz, allowAliases: false)) errors[CronFormField.CronTz] = CronFormError.TimezoneInvalid;
            if (!ScheduleExact && StaggerAmount.Trim().Length > 0 && ParseStagger(StaggerAmount, StaggerUnit) is null)
                errors[CronFormField.StaggerAmount] = CronFormError.StaggerAmountInvalid;
        }

        if (TriggerEnabled)
        {
            if (PayloadKind == CronPayloadKind.Script)
                errors[CronFormField.TriggerScript] = CronFormError.TriggerScriptPayloadUnsupported;
            else if (ScheduleKind is not (CronScheduleKind.Every or CronScheduleKind.Cron or CronScheduleKind.Stream))
                errors[CronFormField.TriggerScript] = CronFormError.TriggerScheduleUnsupported;
            else if (string.IsNullOrWhiteSpace(TriggerScript))
                errors[CronFormField.TriggerScript] = CronFormError.TriggerScriptRequired;
        }

        if (!PayloadLocked && string.IsNullOrWhiteSpace(PayloadText))
            errors[CronFormField.PayloadText] = PayloadKind == CronPayloadKind.SystemEvent
                ? CronFormError.SystemTextRequired
                : CronFormError.AgentMessageRequired;

        if (!PayloadLocked && PayloadKind == CronPayloadKind.AgentTurn && TimeoutSeconds.Trim().Length > 0
            && (!double.TryParse(TimeoutSeconds.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout)
                || !double.IsFinite(timeout) || timeout < 0))
            errors[CronFormField.TimeoutSeconds] = CronFormError.TimeoutInvalid;

        if (string.IsNullOrEmpty(DeliveryMode))
            errors[CronFormField.DeliveryMode] = CronFormError.DeliveryModeRequired;

        if (ActiveHoursEnabled)
        {
            if (!HourMinute.IsMatch(ActiveHoursStart)) errors[CronFormField.ActiveHoursStart] = CronFormError.ActiveHoursTimeInvalid;
            if (!HourMinute.IsMatch(ActiveHoursEnd) && ActiveHoursEnd != "24:00") errors[CronFormField.ActiveHoursEnd] = CronFormError.ActiveHoursTimeInvalid;
            if (!IsValidTimezone(ActiveHoursTimezone, allowAliases: true)) errors[CronFormField.ActiveHoursTimezone] = CronFormError.ActiveHoursTimezoneInvalid;
        }

        if (DeliveryMode == "webhook" && ValidateWebhookUrl(DeliveryTo) is { } webhookError)
            errors[CronFormField.DeliveryTo] = webhookError;

        if (FailureAlertMode == CronFailureAlertMode.Custom)
        {
            var after = FailureAlertAfter.Trim();
            if (after.Length > 0 && (!double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) || !double.IsFinite(count) || count < 1))
                errors[CronFormField.FailureAlertAfter] = CronFormError.FailureAlertAfterInvalid;
            var cooldown = FailureAlertCooldownSeconds.Trim();
            if (cooldown.Length > 0 && CronDurations.ParseCooldownMs(cooldown) is null)
                errors[CronFormField.FailureAlertCooldown] = CronFormError.FailureAlertCooldownInvalid;
        }
        return errors;
    }

    public static CronFormError? ValidateWebhookUrl(string? value)
    {
        var target = (value ?? "").Trim();
        if (target.Length == 0)
            return CronFormError.WebhookUrlRequired;
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return CronFormError.WebhookUrlInvalid;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Host.Contains('%') || uri.Host.Length == 0)
            return CronFormError.WebhookUrlInvalid;
        return null;
    }

    /// <summary>Blank uses the Gateway host zone. Active hours also accept "local" and "user".</summary>
    public static bool IsValidTimezone(string? value, bool allowAliases)
    {
        var tz = (value ?? "").Trim();
        if (tz.Length == 0 || (allowAliases && tz is "local" or "user"))
            return true;
        return TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _);
    }

    /// <summary>
    /// Builds the definition sent as <c>cron.add</c> params or as the <c>cron.update</c> patch.
    /// For updates, cleared optional values are sent as explicit nulls; for creates they are omitted.
    /// </summary>
    public JsonObject BuildDefinition(CronJob? editing = null, CronJob? cloneSource = null)
    {
        var form = Normalize();
        var source = editing ?? cloneSource;
        var isUpdate = editing is not null;
        var job = new JsonObject
        {
            ["name"] = form.Name.Trim(),
            ["description"] = form.Description.Trim(),
        };

        var agentId = form.ClearAgent ? null : form.AgentId.Trim();
        if (agentId is null) job["agentId"] = null;
        else if (agentId.Length > 0) job["agentId"] = agentId;

        var sessionKey = form.SessionKey.Trim();
        if (sessionKey.Length > 0) job["sessionKey"] = sessionKey;
        else if (!string.IsNullOrEmpty(editing?.SessionKey)) job["sessionKey"] = null;

        job["enabled"] = form.Enabled;
        if (form.ScheduleKind is CronScheduleKind.At or CronScheduleKind.OnExit)
            job["deleteAfterRun"] = form.DeleteAfterRun;
        job["sessionTarget"] = form.SessionTarget;
        job["wakeMode"] = form.WakeMode;

        var triggerScript = form.TriggerScript.Trim();
        if (form.TriggerEnabled)
        {
            if (!(editing?.Trigger is { } existing && existing.Script == triggerScript && existing.Once == form.TriggerOnce))
                job["trigger"] = new JsonObject { ["script"] = triggerScript, ["once"] = form.TriggerOnce };
        }
        else if (editing?.Trigger is not null)
        {
            job["trigger"] = null;
        }

        if (form.ActiveHoursEnabled)
        {
            var hours = new JsonObject { ["start"] = form.ActiveHoursStart, ["end"] = form.ActiveHoursEnd };
            if (form.ActiveHoursTimezone.Trim().Length > 0) hours["timezone"] = form.ActiveHoursTimezone.Trim();
            job["activeHours"] = hours;
        }
        else if (editing?.ActiveHours is not null)
        {
            job["activeHours"] = null;
        }

        if (form.IdleOnly || editing?.IdleOnly is not null)
            job["idleOnly"] = form.IdleOnly;

        if (form.BuildDelivery(editing) is { } delivery)
            job["delivery"] = delivery;

        var (writeAlert, alertValue) = form.BuildFailureAlert(source?.FailureAlert, isUpdate);
        if (writeAlert)
            job["failureAlert"] = alertValue;

        // Unchanged schedules are omitted on update so form-unrepresentable precision survives.
        JsonNode? schedule = null;
        if (source is not null && form.HasUnchangedSchedule(source))
            schedule = isUpdate ? null : ToNode(source.Schedule.Raw);
        else
            schedule = form.BuildSchedule(editing?.Schedule);
        if (schedule is not null)
            job["schedule"] = schedule;

        if (source?.Pacing is { } pacing)
        {
            var kind = schedule?["kind"]?.GetValue<string>();
            if (kind is "every" or "cron")
            {
                if (!isUpdate) job["pacing"] = ToNode(pacing);
            }
            else if (isUpdate && schedule is not null)
            {
                job["pacing"] = null;
            }
        }

        if (!(isUpdate && form.PayloadLocked && editing!.Payload.IsReadOnly))
            job["payload"] = form.BuildPayload(source?.Payload, isUpdate);

        return job;
    }

    public bool HasUnchangedSchedule(CronJob job)
    {
        var schedule = job.Schedule;
        if (ScheduleKind != schedule.Kind)
            return false;
        switch (schedule.Kind)
        {
            case CronScheduleKind.Event:
                if (!schedule.IsMcpEvent)
                    return true;
                var args = TryParseEventArguments(EventArguments);
                var saved = TryParseEventArguments(schedule.EventArgumentsJson ?? "{}");
                return EventServer.Trim() == schedule.EventServer
                    && EventName.Trim() == schedule.EventName
                    && args is not null && saved is not null && JsonNode.DeepEquals(args, saved);
            case CronScheduleKind.At:
                return ScheduleAt is { } at && TryParseAt(schedule.At) is { } savedAt
                    && TruncateToMinute(at) == TruncateToMinute(savedAt);
            case CronScheduleKind.Every:
                return CronDurations.ParseMs(EveryAmount, EveryUnit) == schedule.EveryMs;
            case CronScheduleKind.Cron:
                var stagger = CronDurations.SplitStagger(schedule.StaggerMs);
                return CronExpr.Trim() == schedule.Expr
                    && CronTz.Trim() == (schedule.Tz ?? "")
                    && ScheduleExact == stagger.Exact
                    && StaggerAmount.Trim() == stagger.Amount
                    && StaggerUnit == stagger.Unit;
            default:
                return true;
        }
    }

    public JsonObject? BuildSchedule(CronSchedule? previous = null)
    {
        switch (ScheduleKind)
        {
            case CronScheduleKind.Event:
                var args = TryParseEventArguments(EventArguments) ?? throw new FormatException("Event arguments must be a JSON object.");
                return new JsonObject
                {
                    ["kind"] = "event",
                    ["source"] = CronProtocol.McpEventsSource,
                    ["options"] = new JsonObject
                    {
                        ["server"] = EventServer.Trim(),
                        ["name"] = EventName.Trim(),
                        ["arguments"] = args,
                    },
                };
            case CronScheduleKind.At:
                var at = ScheduleAt ?? throw new FormatException("Run time is required.");
                return new JsonObject
                {
                    ["kind"] = "at",
                    ["at"] = TruncateToMinute(at).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                };
            case CronScheduleKind.Every:
                var everyMs = CronDurations.ParseMs(EveryAmount, EveryUnit) ?? throw new FormatException("Invalid interval amount.");
                return new JsonObject { ["kind"] = "every", ["everyMs"] = everyMs };
            case CronScheduleKind.Cron:
                var expr = CronExpr.Trim();
                if (expr.Length == 0) throw new FormatException("Cron expression required.");
                long? staggerMs;
                if (ScheduleExact)
                {
                    staggerMs = 0;
                }
                else if (StaggerAmount.Trim().Length == 0)
                {
                    // Same-expression updates preserve an omitted window; new defaults stay unspecified.
                    var clearsSavedWindow = previous is { Kind: CronScheduleKind.Cron, StaggerMs: not null } && previous.Expr == expr;
                    staggerMs = CronDurations.DefaultStaggerMs(expr) ?? (clearsSavedWindow ? 0 : null);
                }
                else
                {
                    staggerMs = ParseStagger(StaggerAmount, StaggerUnit) ?? throw new FormatException("Invalid stagger amount.");
                }
                var schedule = new JsonObject { ["kind"] = "cron", ["expr"] = expr };
                if (CronTz.Trim().Length > 0) schedule["tz"] = CronTz.Trim();
                if (staggerMs is not null) schedule["staggerMs"] = staggerMs;
                return schedule;
            default:
                return null;
        }
    }

    private JsonObject BuildPayload(CronPayload? source, bool isUpdate)
    {
        // Clones carry public restrictions; updates omit them so the Gateway keeps existing authority.
        var toolsAllow = !isUpdate && source?.ToolsAllow is { } tools ? tools : null;
        if (PayloadKind == CronPayloadKind.SystemEvent)
        {
            var systemEvent = new JsonObject { ["kind"] = "systemEvent", ["text"] = PayloadText.Trim() };
            if (toolsAllow is not null) systemEvent["toolsAllow"] = ToArray(toolsAllow);
            return systemEvent;
        }
        if (PayloadKind != CronPayloadKind.AgentTurn)
            throw new InvalidOperationException("Command and script payloads are read-only.");

        var original = source?.Kind == CronPayloadKind.AgentTurn ? source : null;
        var payload = new JsonObject { ["kind"] = "agentTurn", ["message"] = PayloadText.Trim() };
        SetOrClear(payload, "model", PayloadModel, isUpdate && original?.Model is not null);
        SetOrClear(payload, "thinking", PayloadThinking, isUpdate && original?.Thinking is not null);
        var timeoutRaw = TimeoutSeconds.Trim();
        if (timeoutRaw.Length > 0 && double.TryParse(timeoutRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout)
            && double.IsFinite(timeout) && timeout >= 0)
            payload["timeoutSeconds"] = timeout;
        else if (isUpdate && original?.TimeoutSeconds is not null)
            payload["timeoutSeconds"] = null;
        if (PayloadLightContext || original?.LightContext is not null) payload["lightContext"] = PayloadLightContext;
        if (PayloadSkipIfScratchEmpty || original?.SkipIfScratchEmpty is not null) payload["skipIfScratchEmpty"] = PayloadSkipIfScratchEmpty;
        if (toolsAllow is not null) payload["toolsAllow"] = ToArray(toolsAllow);
        if (!isUpdate && original?.Fallbacks is { } fallbacks) payload["fallbacks"] = ToArray(fallbacks);
        if (PayloadIncludeReasoning || original?.IncludeReasoning is not null) payload["includeReasoning"] = PayloadIncludeReasoning;
        return payload;
    }

    private JsonObject? BuildDelivery(CronJob? editing)
    {
        var mode = DeliveryMode;
        if (string.IsNullOrEmpty(mode))
            return null;
        var accountId = DeliveryAccountId.Trim();
        var ownerTarget = mode == "announce" && DeliveryTarget == "owner";
        var previous = editing?.Delivery is { Mode: not "webhook" } prior ? prior : null;
        var ownerChannel = DeliveryChannel != CronProtocol.ChannelLast && DeliveryChannel.Trim().Length > 0 ? DeliveryChannel.Trim() : null;
        var delivery = new JsonObject { ["mode"] = mode };

        if (mode == "none")
        {
            if (DeliveryBestEffort) delivery["bestEffort"] = true;
            if (DeliveryTarget == "owner")
            {
                delivery["target"] = "owner";
                if (ownerChannel is not null) delivery["channel"] = ownerChannel;
                if (accountId.Length > 0) delivery["accountId"] = accountId;
            }
            if (DeliveryDirectPolicy.Length > 0) delivery["directPolicy"] = DeliveryDirectPolicy;
        }
        else
        {
            if (ownerTarget)
            {
                if (ownerChannel is not null) delivery["channel"] = ownerChannel;
                else if (editing?.Delivery?.Channel is not null) delivery["channel"] = null;
            }
            else if (mode == "announce")
            {
                var channel = DeliveryChannel.Trim();
                var preserveLast = !string.IsNullOrEmpty(editing?.Delivery?.Channel);
                if (channel.Length > 0 && (channel != CronProtocol.ChannelLast || preserveLast))
                    delivery["channel"] = channel;
            }

            var to = ownerTarget ? "" : DeliveryTo.Trim();
            if (to.Length > 0) delivery["to"] = to;
            else if (mode == "announce" && !string.IsNullOrEmpty(editing?.Delivery?.To)) delivery["to"] = null;

            if (ownerTarget) delivery["target"] = "owner";
            else if (previous?.Target is not null) delivery["target"] = null;

            if (mode == "announce")
            {
                if (DeliveryDirectPolicy.Length > 0) delivery["directPolicy"] = DeliveryDirectPolicy;
                else if (previous?.DirectPolicy is not null) delivery["directPolicy"] = null;

                if (accountId.Length > 0) delivery["accountId"] = accountId;
                else if (!string.IsNullOrEmpty(editing?.Delivery?.AccountId)) delivery["accountId"] = null;
            }
            delivery["bestEffort"] = DeliveryBestEffort;
        }

        if (!ownerTarget && DeliveryThreadId is not null) delivery["threadId"] = DeliveryThreadId;
        else if (editing?.Delivery?.ThreadId is not null) delivery["threadId"] = null;
        if (mode == "announce" && DeliveryCompletionDestination is { } completion) delivery["completionDestination"] = ToNode(completion);
        if (DeliveryFailureDestination is { } failure) delivery["failureDestination"] = ToNode(failure);
        return delivery;
    }

    /// <summary>Returns whether to write <c>failureAlert</c>, and its value: false (off), null (clear to inherit) or an object.</summary>
    private (bool Write, JsonNode? Value) BuildFailureAlert(CronFailureAlert? source, bool isUpdate)
    {
        if (FailureAlertMode == CronFailureAlertMode.Disabled)
            return (true, JsonValue.Create(false));
        if (FailureAlertMode != CronFailureAlertMode.Custom)
            return (isUpdate && source is not null, null);

        var sourceConfig = source is { Disabled: false } ? source : null;
        var existing = isUpdate ? sourceConfig : null;
        var alert = new JsonObject();
        if (double.TryParse(FailureAlertAfter.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var after) && after > 0)
            alert["after"] = (int)Math.Floor(after);
        else if (existing?.After is not null)
            alert["after"] = null;

        var channel = FailureAlertChannel.Trim();
        if (channel.Length > 0 && (channel != CronProtocol.ChannelLast || !string.IsNullOrEmpty(sourceConfig?.Channel)))
            alert["channel"] = channel;

        var to = FailureAlertTo.Trim();
        if (to.Length > 0) alert["to"] = to;
        else if (!string.IsNullOrEmpty(existing?.To)) alert["to"] = null;

        if (CronDurations.ParseCooldownMs(FailureAlertCooldownSeconds) is { } cooldownMs) alert["cooldownMs"] = cooldownMs;
        else if (existing?.CooldownMs is not null) alert["cooldownMs"] = null;

        if (FailureAlertDeliveryMode.Length > 0) alert["mode"] = FailureAlertDeliveryMode;
        else if (existing?.Mode is not null) alert["mode"] = null;

        var account = FailureAlertAccountId.Trim();
        if (account.Length > 0) alert["accountId"] = account;
        else if (!string.IsNullOrEmpty(existing?.AccountId)) alert["accountId"] = null;

        if (sourceConfig?.IncludeSkipped is { } includeSkipped) alert["includeSkipped"] = includeSkipped;
        return (true, alert);
    }

    public static JsonObject? TryParseEventArguments(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? TryParseAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime()
            : null;

    private static long? ParseStagger(string amount, CronDurationUnit unit)
    {
        var text = amount.Trim();
        // Stagger also accepts number syntax such as "1e1".
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            && !Regex.IsMatch(text, @"^(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)$"))
            text = number.ToString("R", CultureInfo.InvariantCulture);
        return CronDurations.ParseMs(text, unit);
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    private static DateTimeOffset NextWholeHour(DateTimeOffset now) =>
        TruncateToMinute(now).AddMinutes(-now.Minute).AddHours(1);

    private static bool IsCanonicalDeliveryMode(string mode) => mode is "none" or "announce" or "webhook";

    private static void SetOrClear(JsonObject target, string name, string value, bool clearWhenBlank)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0) target[name] = trimmed;
        else if (clearWhenBlank) target[name] = null;
    }

    private static JsonArray ToArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonNode? ToNode(JsonElement? element) =>
        element is { } value ? JsonNode.Parse(value.GetRawText()) : null;

    private static readonly Regex HourMinute = new(@"^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);
}

/// <summary>Starter automation shown on an empty, manageable list.</summary>
public sealed record CronSuggestion(
    string Id,
    string Glyph,
    CronScheduleKind ScheduleKind,
    string? CronExpr = null,
    string? EveryAmount = null,
    CronDurationUnit? EveryUnit = null)
{
    public static IReadOnlyList<CronSuggestion> All { get; } =
    [
        new("repoPulse", "\uE943", CronScheduleKind.Cron, CronExpr: "0 9 * * 1-5"),
        new("standupGhostwriter", "\uE8BD", CronScheduleKind.Cron, CronExpr: "0 9 * * 1-5"),
        new("hackerNewsScout", "\uE774", CronScheduleKind.Cron, CronExpr: "0 8 * * *"),
        new("dependencyRadar", "\uE7B8", CronScheduleKind.Cron, CronExpr: "0 9 * * 1"),
        new("watchdog", "\uE9D9", CronScheduleKind.Every, EveryAmount: "1", EveryUnit: CronDurationUnit.Hours),
        new("polyglotMinute", "\uF2B7", CronScheduleKind.Cron, CronExpr: "0 8 * * *"),
    ];
}
