using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenClaw.Shared.Cron;

namespace OpenClawTray.Automations;

/// <summary>
/// Localized, WinUI-free wording for the Automations page. Resource lookup is
/// injected so tests can bind it to the real en-US catalog.
/// </summary>
internal sealed class AutomationsText(Func<string, string> lookup, Func<DateTimeOffset>? now = null, TimeZoneInfo? timeZone = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;

    /// <summary>Placeholder for a value the Gateway did not provide.</summary>
    public const string Unavailable = "—";

    public string this[string key] => lookup("Automations_" + key);

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>"Label (n)" when the Gateway count is known, otherwise the bare label.</summary>
    public string CountLabel(string key, int? count) => count is { } n ? Format(key + "Count", n) : this[key];

    private DateTimeOffset Local(long at) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), _timeZone);

    public string Clock(long at) => Local(at).ToString("t", CultureInfo.CurrentCulture);

    private string CalendarDate(long at, bool weekday)
    {
        var local = Local(at);
        var culture = CultureInfo.CurrentCulture;
        var sameYear = local.Year == TimeZoneInfo.ConvertTime(_now(), _timeZone).Year;
        var pattern = (sameYear ? culture.DateTimeFormat.MonthDayPattern : culture.DateTimeFormat.LongDatePattern)
            .Replace("MMMM", "MMM", StringComparison.Ordinal).Replace("dddd", "ddd", StringComparison.Ordinal);
        var date = local.ToString(pattern, culture);
        return weekday && sameYear ? $"{culture.DateTimeFormat.GetAbbreviatedDayName(local.DayOfWeek)}, {date}" : date;
    }

    public string FriendlyDateTime(long at)
    {
        var today = TimeZoneInfo.ConvertTime(_now(), _timeZone).Date;
        var date = Local(at).Date;
        if (date == today) return Format("Review_Today", Clock(at));
        if (date == today.AddDays(1)) return Format("Review_Tomorrow", Clock(at));
        return $"{CalendarDate(at, false)}, {Clock(at)}";
    }

    public string NextUpTime(long at)
    {
        var today = TimeZoneInfo.ConvertTime(_now(), _timeZone).Date;
        var local = Local(at);
        return local.Date == today ? Clock(at) : $"{CalendarDate(at, false)}, {Clock(at)}";
    }

    public string RunTimestamp(long at)
    {
        if (Local(at).Date == TimeZoneInfo.ConvertTime(_now(), _timeZone).Date) return Format("Review_Today", Clock(at));
        return $"{CalendarDate(at, false)}, {Clock(at)}";
    }

    // ─── Schedules ───

    /// <summary>Readable one-line summary for list rows and the editor preview.</summary>
    public string Schedule(CronSchedule schedule) => schedule.Kind switch
    {
        CronScheduleKind.At => CronJobForm.TryParseAt(schedule.At) is { } at
            ? Once(at)
            : Format("Schedule_Once", schedule.At ?? ""),
        CronScheduleKind.Every when schedule.EveryMs is { } ms => Every(ms),
        CronScheduleKind.Cron => Cron(schedule.Expr ?? "", schedule.Tz),
        CronScheduleKind.Event when schedule.IsMcpEvent => Format("Schedule_Event", schedule.EventName ?? "", schedule.EventServer ?? ""),
        CronScheduleKind.Event => Format("Schedule_EventSource", schedule.EventSource ?? ""),
        CronScheduleKind.OnExit => Format("Schedule_OnExit", schedule.Command ?? ""),
        CronScheduleKind.Stream => Format("Schedule_Stream", schedule.Command ?? ""),
        _ => this["Schedule_Unknown"],
    };

    /// <summary>Live summary for an unsaved native schedule; null when incomplete or web-managed.</summary>
    public string? FormSchedule(CronJobForm form) => (form.ScheduleTimeMissing && (form.ScheduleKind is CronScheduleKind.At or CronScheduleKind.Cron))
        || (form.ScheduleDayMissing && form.ScheduleKind == CronScheduleKind.Cron)
        ? null : form.ScheduleKind switch
    {
        CronScheduleKind.At => form.ScheduleAt is { } at ? Once(at) : null,
        CronScheduleKind.Every => CronDurations.ParseMs(form.EveryAmount, form.EveryUnit) is { } ms ? Every(ms) : null,
        CronScheduleKind.Cron => string.IsNullOrWhiteSpace(form.CronExpr) ? null : Cron(form.CronExpr.Trim(), form.CronTz.Trim()),
        _ => null,
    };

    private string Once(DateTimeOffset at)
    {
        var ms = at.ToUnixTimeMilliseconds();
        return Local(ms).Date == TimeZoneInfo.ConvertTime(_now(), _timeZone).Date
            ? Format("Review_OnceToday", Clock(ms))
            : Format("Review_OnceOn", CalendarDate(ms, true), Clock(ms));
    }

    public string InventorySummary(int on, int paused, int agents)
    {
        var parts = new List<string>();
        if (on > 0) parts.Add(Format(on == 1 ? "Review_OnOne" : "Review_OnMany", on));
        if (paused > 0) parts.Add(Format(paused == 1 ? "Review_PausedOne" : "Review_PausedMany", paused));
        if (agents > 1) parts.Add(Format("Review_AcrossAgents", agents));
        return parts.Count == 0 ? "" : Format("Review_SummaryEnd",
            parts.Aggregate((left, right) => Format("Review_SummaryJoin", left, right)));
    }

    public string Every(long everyMs)
    {
        var (amount, unit) = CronDurations.Split(everyMs);
        var unitKey = unit switch
        {
            CronDurationUnit.Days => "Day",
            CronDurationUnit.Hours => "Hour",
            CronDurationUnit.Minutes => "Minute",
            _ => "Second",
        };
        return amount == "1" ? this[$"Schedule_EveryOne{unitKey}"] : Format($"Schedule_EveryMany{unitKey}s", amount);
    }

    /// <summary>Humanizes common five-field expressions; anything else stays a literal expression.</summary>
    public string Cron(string expr, string? tz)
    {
        var text = HumanizeCron(expr) ?? Format("Schedule_CronLiteral", expr);
        return string.IsNullOrWhiteSpace(tz) ? text : Format("Schedule_WithTimezone", text, tz);
    }

    private string? HumanizeCron(string expr)
    {
        var f = expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
            return null;
        var (min, hour, dom, mon, dow) = (f[0], f[1], f[2], f[3], f[4]);
        if (mon != "*")
            return null;
        if (min == "*" && hour == "*" && dom == "*" && dow == "*")
            return this["Schedule_EveryOneMinute"];
        if (min.StartsWith("*/", StringComparison.Ordinal) && hour == "*" && dom == "*" && dow == "*"
            && int.TryParse(min[2..], out var everyMinutes) && everyMinutes > 0)
            return everyMinutes == 1 ? this["Schedule_EveryOneMinute"] : Format("Schedule_EveryManyMinutes", everyMinutes);
        if (int.TryParse(min, out var minute) && minute is >= 0 and < 60 && dom == "*" && dow == "*")
        {
            if (hour == "*")
                return minute == 0 ? this["Schedule_EveryOneHour"] : Format("Schedule_HourlyAt", minute.ToString("00", CultureInfo.InvariantCulture));
            if (hour.StartsWith("*/", StringComparison.Ordinal) && int.TryParse(hour[2..], out var everyHours) && everyHours > 0 && minute == 0)
                return everyHours == 1 ? this["Schedule_EveryOneHour"] : Format("Schedule_EveryManyHours", everyHours);
        }
        if (!int.TryParse(min, out minute) || !int.TryParse(hour, out var h) || minute is < 0 or > 59 || h is < 0 or > 23)
            return null;
        var time = Time(h, minute);
        if (dom == "*" && dow == "*")
            return Format("Schedule_DailyAt", time);
        if (dom == "*")
        {
            var days = ParseDays(dow);
            if (days is null) return null;
            if (days.SetEquals(new[] { 1, 2, 3, 4, 5 })) return Format("Schedule_WeekdaysAt", time);
            if (days.SetEquals(new[] { 0, 6 })) return Format("Schedule_WeekendsAt", time);
            var names = string.Join(CultureInfo.CurrentCulture.TextInfo.ListSeparator + " ",
                days.OrderBy(d => d == 0 ? 7 : d).Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)d)));
            return Format("Schedule_DaysAt", names, time);
        }
        if (dow == "*" && int.TryParse(dom, out var dayOfMonth) && dayOfMonth is >= 1 and <= 31)
            return Format("Schedule_MonthlyAt", dayOfMonth, time);
        return null;
    }

    private static HashSet<int>? ParseDays(string field)
    {
        var days = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var range = part.Split('-');
            if (range.Length == 1 && int.TryParse(range[0], out var d) && d is >= 0 and <= 7)
                days.Add(d % 7);
            else if (range.Length == 2 && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b) && a <= b && a >= 0 && b <= 7)
                for (var i = a; i <= b; i++) days.Add(i % 7);
            else
                return null;
        }
        return days.Count == 0 ? null : days;
    }

    private static string Time(int hour, int minute) =>
        new System.DateTime(2000, 1, 1, hour, minute, 0).ToString("t", CultureInfo.CurrentCulture);

    // ─── Time ───

    public string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string DateTime(long ms) => DateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms));

    /// <summary>"in 5 min", "3 h ago" style relative time.</summary>
    public string Relative(long ms)
    {
        var delta = DateTimeOffset.FromUnixTimeMilliseconds(ms) - _now();
        var future = delta > TimeSpan.Zero;
        var span = future ? delta : -delta;
        string amount;
        if (span.TotalSeconds < 45) return this["Relative_Now"];
        if (span.TotalMinutes < 60) amount = Format("Relative_Minutes", Math.Max(1, (int)Math.Round(span.TotalMinutes)));
        else if (span.TotalHours < 24) amount = Format("Relative_Hours", (int)Math.Round(span.TotalHours));
        else amount = Format("Relative_Days", (int)Math.Round(span.TotalDays));
        return Format(future ? "Relative_Future" : "Relative_Past", amount);
    }

    public string Duration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        if (span.TotalSeconds < 1) return Format("Duration_Milliseconds", ms);
        if (span.TotalMinutes < 1) return Format("Duration_Seconds", span.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture));
        if (span.TotalHours < 1)
            return span.Seconds == 0 ? Format("Relative_Minutes", (int)span.TotalMinutes) : Format("Duration_Minutes", (int)span.TotalMinutes, span.Seconds);
        return span.Minutes == 0 ? Format("Relative_Hours", (int)span.TotalHours) : Format("Duration_Hours", (int)span.TotalHours, span.Minutes);
    }

    // ─── Status ───

    public string JobState(CronJob job, bool running) =>
        running || job.IsRunning ? this["State_Running"]
        : job.State.AutoDisabled is { } auto ? AutoDisabled(auto)
        : job.IsFailing ? this["State_Failing"]
        : job.IsCompletedOneShot ? this["State_Completed"]
        : job.Enabled ? this["State_Active"]
        : this["State_Paused"];

    /// <summary>Falls back to the generic notice when the Gateway omitted the reason or count.</summary>
    public string AutoDisabled(CronAutoDisabled auto) =>
        auto is { Reason: not null, ConsecutiveErrors: { } count }
            ? Format(auto.IsScheduleErrors ? "State_AutoDisabledSchedule" : "State_AutoDisabledRuns", count)
            : this["Redesign_AutoDisabled"];

    public string RunStatus(string? status) => status switch
    {
        "ok" => this["RunStatus_Ok"],
        "error" => this["RunStatus_Error"],
        "skipped" => this["RunStatus_Skipped"],
        _ => this["Unknown"],
    };

    public string DeliveryStatus(string? status) => status switch
    {
        "delivered" => this["Delivery_Delivered"],
        "not-delivered" => this["Delivery_NotDelivered"],
        "not-requested" => this["Delivery_NotRequested"],
        _ => this["Delivery_Unknown"],
    };

    public string DeliveryMode(string mode) => mode switch
    {
        "announce" => this["DeliveryMode_Announce"],
        "webhook" => this["DeliveryMode_Webhook"],
        "none" => this["DeliveryMode_None"],
        _ => this["DeliveryMode_Legacy"],
    };

    public string SessionTarget(string target) => target switch
    {
        "main" => this["Session_Main"],
        "isolated" => this["Session_Isolated"],
        "current" => this["Session_Current"],
        _ when target.StartsWith("session:", StringComparison.Ordinal) => Format("Session_Named", target["session:".Length..]),
        _ => target,
    };

    public string WakeMode(string mode) => mode == "next-heartbeat" ? this["Wake_NextHeartbeat"] : this["Wake_Now"];

    public string PayloadKind(CronPayloadKind kind) => kind switch
    {
        CronPayloadKind.SystemEvent => this["Payload_SystemEvent"],
        CronPayloadKind.AgentTurn => this["Payload_AgentTurn"],
        CronPayloadKind.Command => this["Field_Command"],
        CronPayloadKind.Script => this["Payload_Script"],
        _ => this["Payload_Unknown"],
    };

    /// <summary>Label for the payload's content, named after the Gateway field it holds.</summary>
    public string PayloadField(CronPayloadKind kind) => kind switch
    {
        CronPayloadKind.AgentTurn => this["Field_Prompt"],
        CronPayloadKind.SystemEvent => this["Field_TimelineMessage"],
        CronPayloadKind.Command => this["Field_Command"],
        CronPayloadKind.Script => this["Payload_Script"],
        _ => this["Field_Payload"],
    };

    public string RunNotStarted(string? reason) => reason switch
    {
        "not-due" => this["RunNotStarted_NotDue"],
        "already-running" => this["RunNotStarted_AlreadyRunning"],
        "recovery-pending" => this["RunNotStarted_RecoveryPending"],
        "invalid-spec" => this["RunNotStarted_InvalidSpec"],
        "stopped" => this["RunNotStarted_Stopped"],
        _ => this["RunNotStarted_Unknown"],
    };

    public (string Title, string Message) Notice(AutomationsNotice notice) => notice.Kind switch
    {
        AutomationsNoticeKind.JobCompleted => (this["Notice_JobCompletedTitle"], Format("Notice_JobCompleted", notice.JobName)),
        AutomationsNoticeKind.RunQueued => (this["Toast_RunQueued"], string.IsNullOrEmpty(notice.RunId)
            ? this["Toast_RunQueued"]
            : Format("Toast_RunQueuedWithId", notice.RunId)),
        AutomationsNoticeKind.RunNotStarted when notice.Reason == "already-running" && string.IsNullOrWhiteSpace(notice.Detail) =>
            (this["Notice_RunNotStartedTitle"], Format("Toast_AlreadyRunning", notice.JobName)),
        AutomationsNoticeKind.RunNotStarted when notice.IsError && notice.Detail?.Contains("operator.admin", StringComparison.Ordinal) == true =>
            (this["Notice_RunNotStartedTitle"], Format("Review_RunPermissionError", notice.JobName)),
        AutomationsNoticeKind.RunNotStarted => (this["Notice_RunNotStartedTitle"], Format("Notice_JobMessage", notice.JobName,
            string.IsNullOrWhiteSpace(notice.Detail) ? RunNotStarted(notice.Reason) : $"{RunNotStarted(notice.Reason)} {notice.Detail}")),
        AutomationsNoticeKind.RunFailed => (this["Notice_RunFailedTitle"], Format("Notice_JobMessage", notice.JobName, notice.Detail ?? this["RunNotStarted_Unknown"])),
        AutomationsNoticeKind.Saved => (this["Notice_SavedTitle"], Format("Notice_Saved", notice.JobName)),
        AutomationsNoticeKind.Created => (this["Notice_CreatedTitle"], Format("Notice_Created", notice.JobName)),
        AutomationsNoticeKind.Removed => (this["Notice_RemovedTitle"], Format("Toast_Removed", notice.JobName)),
        AutomationsNoticeKind.Paused => (this["Notice_PausedTitle"], Format("Notice_Paused", notice.JobName)),
        AutomationsNoticeKind.Resumed => (this["Notice_ResumedTitle"], Format("Notice_Resumed", notice.JobName)),
        AutomationsNoticeKind.Conflict => (this["Notice_ConflictTitle"], this["Notice_Conflict"]),
        AutomationsNoticeKind.ConflictUnloadable => (this["Notice_ConflictTitle"], this["Notice_ConflictUnloadable"]),
        AutomationsNoticeKind.MissingRevision => (this["Notice_ConflictTitle"], this["Notice_MissingRevision"]),
        AutomationsNoticeKind.JobUnavailable => (JobUnavailableMessage, JobUnavailableMessage),
        _ => (this["Notice_FailedTitle"], Format("Notice_JobMessage", notice.JobName, notice.Detail ?? "")),
    };

    /// <summary>Shares the Hub search wording, so it is also the heading: the page shows only the message.</summary>
    private string JobUnavailableMessage => lookup("Search_TargetUnavailable");

    public string FormError(CronFormError error) =>
        error == CronFormError.WebhookUrlInvalid ? this["Error_WebhookUrlRequired"] : this["Error_" + error];

    /// <summary>"All agents" or "Agent: id"; a status filter never implies every agent.</summary>
    public string AgentScope(string? agentFilter) =>
        agentFilter is null ? this["Filter_AllAgents"] : Format("AgentScope", agentFilter);

    /// <summary>
    /// Saved settings the native editor keeps but does not show. Schedules and actions it
    /// cannot edit have their own notes in place, so they are not repeated here.
    /// </summary>
    public IReadOnlyList<string> PreservedSettings(CronJob job)
    {
        var names = new List<string>();
        var schedule = job.Schedule;
        var payload = job.Payload;
        var delivery = job.Delivery;
        if (schedule.Kind == CronScheduleKind.Cron && schedule.StaggerMs is { } stagger
            && stagger != CronDurations.DefaultStaggerMs(schedule.Expr ?? ""))
            names.Add(this["Field_Stagger"]);
        if (job.ActiveHours is not null) names.Add(this["Field_ActiveHours"]);
        if (job.Trigger is not null) names.Add(this["Field_Condition"]);
        if (!string.IsNullOrWhiteSpace(payload.Model)) names.Add(this["Field_Model"]);
        if (!string.IsNullOrWhiteSpace(payload.Thinking)) names.Add(this["Field_Thinking"]);
        if (payload.TimeoutSeconds is not null) names.Add(this["Field_Timeout"]);
        if (payload.LightContext == true || payload.IncludeReasoning == true || payload.SkipIfScratchEmpty == true || job.IdleOnly == true)
            names.Add(this["Field_Options"]);
        if (payload.ToolsAllow is { Count: > 0 }) names.Add(this["Field_Tools"]);
        if (!string.IsNullOrWhiteSpace(job.SessionKey)) names.Add(this["Field_SessionKey"]);
        if (delivery is not null && (delivery.AccountId is not null || delivery.DirectPolicy is not null || delivery.ThreadId is not null
            || delivery.BestEffort == true || delivery.CompletionDestination is not null || delivery.FailureDestination is not null))
            names.Add(this["Field_DeliveryRouting"]);
        if (job.FailureAlert is not null) names.Add(this["Field_FailureAlerts"]);
        return names;
    }
}
