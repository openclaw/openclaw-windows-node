using OpenClaw.Shared.Cron;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class CronAgendaTests
{
    private static CronJob Job(string id, DateTimeOffset? next = null, CronScheduleKind kind = CronScheduleKind.At) => new()
    {
        Id = id, Name = id, Enabled = true, AgentId = "main",
        Schedule = new(kind, kind.ToString().ToLowerInvariant()),
        Payload = new(CronPayloadKind.AgentTurn, "agentTurn", "Synthetic prompt"),
        State = new() { NextRunAtMs = next?.ToUnixTimeMilliseconds() },
    };

    [Fact]
    public void LocalCalendarDatesTakePrecedenceOverAnHourThatCrossesMidnight()
    {
        var now = new DateTimeOffset(2030, 3, 10, 23, 30, 0, TimeSpan.FromHours(10));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture+10", TimeSpan.FromHours(10), "Fixture+10", "Fixture+10");
        var agenda = CronAgenda.Create([
            Job("tomorrow-early", now.AddMinutes(45)),
            Job("today", now.AddMinutes(10)),
            Job("tomorrow-late", now.AddHours(24)),
            Job("day-after", now.AddHours(25)),
            Job("paused", now.AddMinutes(5)) with { Enabled = false },
            Job("waiting", kind: CronScheduleKind.Event),
            Job("stream", kind: CronScheduleKind.Stream),
            Job("process", kind: CronScheduleKind.OnExit),
            Job("interval", now.AddMinutes(3), CronScheduleKind.Every),
            Job("running") with { State = new() { RunningAtMs = now.ToUnixTimeMilliseconds() } },
            Job("earlier") with { State = new() { LastRunAtMs = now.AddHours(-2).ToUnixTimeMilliseconds(), LastRunStatus = "error" } },
            Job("yesterday") with { State = new() { LastRunAtMs = now.AddHours(-24).ToUnixTimeMilliseconds() } },
        ], now, zone);

        Assert.Equal(["Running", "Hour", "Tomorrow"], agenda.Groups.Select(g => g.Key));
        Assert.Equal(["running"], agenda.Groups[0].Jobs.Select(j => j.Id));
        Assert.Equal(["today"], agenda.Groups[1].Jobs.Select(j => j.Id));
        Assert.Equal(["tomorrow-early", "tomorrow-late"], agenda.Groups[2].Jobs.Select(j => j.Id));
        Assert.Equal("interval", Assert.Single(agenda.Repeating).Id);
        Assert.Equal(["waiting", "stream", "process"], agenda.WaitingForEvents.Select(j => j.Id));
        Assert.Equal("earlier", Assert.Single(agenda.EarlierToday).Id);
    }

    [Fact]
    public void EarlierTodayPrioritizesFailuresThenUsesReportedTimesWithinEachStatus()
    {
        var now = new DateTimeOffset(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);
        CronJob Earlier(string id, int minutes, string status) => Job(id) with
        {
            State = new() { LastRunAtMs = now.AddMinutes(-minutes).ToUnixTimeMilliseconds(), LastRunStatus = status },
        };
        var agenda = CronAgenda.Create([
            Earlier("success-new", 1, "ok"), Earlier("failure-old", 40, "error"),
            Earlier("failure-new", 20, "error"), Earlier("skipped", 5, "skipped"),
        ], now, TimeZoneInfo.Utc);
        Assert.Equal(["failure-new", "failure-old", "success-new", "skipped"], agenda.EarlierToday.Select(job => job.Id));
    }

    [Fact]
    public void ClearedNativeTimeIsInvalidWithoutChangingTheSavedSchedule()
    {
        var saved = Job("daily", kind: CronScheduleKind.Cron) with
        { Schedule = new(CronScheduleKind.Cron, "cron", Expr: "0 7 * * *", Tz: "UTC") };
        var form = CronJobForm.FromJob(saved) with { ScheduleTimeMissing = true };
        Assert.Equal(CronFormError.TimeRequired, form.Validate()[CronFormField.ScheduleTime]);
        Assert.Equal("0 7 * * *", form.CronExpr);
        var repaired = form with { ScheduleTimeMissing = false };
        Assert.Empty(repaired.Validate());
        Assert.False(repaired.BuildDefinition(saved).ContainsKey("scheduleTimeMissing"));
        Assert.False(repaired.BuildDefinition(saved).ContainsKey("schedule"));
        Assert.Equal("0 7 * * *", repaired.BuildDefinition()["schedule"]!["expr"]!.GetValue<string>());
    }

    [Fact]
    public void ClearedNativeWeekdaySurvivesEditsWithoutChangingTheSavedSchedule()
    {
        var saved = Job("weekly", kind: CronScheduleKind.Cron) with
        { Schedule = new(CronScheduleKind.Cron, "cron", Expr: "0 7 * * 1", Tz: "UTC") };
        var draft = CronJobForm.FromJob(saved) with { ScheduleDayMissing = true, Name = "Edited weekly name" };
        Assert.Equal(CronFormError.WeekdayRequired, draft.Validate()[CronFormField.ScheduleDay]);
        Assert.Equal("0 7 * * 1", draft.CronExpr);
        var repaired = draft with { ScheduleDayMissing = false };
        Assert.Empty(repaired.Validate());
        Assert.False(repaired.BuildDefinition(saved).ContainsKey("scheduleDayMissing"));
        Assert.False(repaired.BuildDefinition(saved).ContainsKey("schedule"));
        Assert.Equal("0 7 * * 1", repaired.BuildDefinition()["schedule"]!["expr"]!.GetValue<string>());
        Assert.False(draft.WithScheduleKind(CronScheduleKind.Every).ScheduleDayMissing);
    }

    [Fact]
    public void CalendarGroupsUseReportedInstantsWithoutSynthesizingMissingNextRuns()
    {
        var now = new DateTimeOffset(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);
        var agenda = CronAgenda.Create([
            Job("later", now.AddHours(3)),
            Job("soon", now.AddMinutes(60)),
            Job("missing"),
            Job("repeating-cron", now.AddMinutes(15), CronScheduleKind.Cron) with { Schedule = new(CronScheduleKind.Cron, "cron", Expr: "*/15 * * * *") },
            Job("daily", now.AddHours(2), CronScheduleKind.Cron) with { Schedule = new(CronScheduleKind.Cron, "cron", Expr: "0 14 * * *") },
            Job("disabled", now.AddMinutes(1)) with { State = new() { AutoDisabled = new("schedule-errors", 1, 5), NextRunAtMs = now.AddMinutes(1).ToUnixTimeMilliseconds() } },
        ], now, TimeZoneInfo.Utc);
        Assert.Equal(["Hour", "Today"], agenda.Groups.Select(g => g.Key));
        Assert.Equal(["daily", "later"], agenda.Groups[1].Jobs.Select(j => j.Id));
        Assert.Equal("soon", Assert.Single(agenda.Groups[0].Jobs).Id);
        Assert.Equal("repeating-cron", Assert.Single(agenda.Repeating).Id);
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, "30 7 * * 0")]
    [InlineData(DayOfWeek.Tuesday, "30 7 * * 2")]
    [InlineData(DayOfWeek.Saturday, "30 7 * * 6")]
    public void WeeklyCalendarRetainsTheSelectedDay(DayOfWeek day, string expression)
    {
        Assert.Equal(expression, CronCalendar.Build(CronCalendarPattern.Weekly, new TimeSpan(7, 30, 0), day));
        Assert.Equal(CronCalendarPattern.Weekly, CronCalendar.TryParse(expression)!.Value.Pattern);
        Assert.Equal(day, CronCalendar.WeeklyDay(expression));
    }

    [Fact]
    public void WebhooksRequireHttpsForNewDestinationsButKeepAnUntouchedLegacyHttpDestination()
    {
        var job = Job("legacy", kind: CronScheduleKind.Every) with
        {
            Schedule = new(CronScheduleKind.Every, "every", EveryMs: 60_000),
            Delivery = new("webhook", To: "http://legacy.example.test/hook"),
        };
        var edit = CronJobForm.FromJob(job) with { Name = "Renamed" };
        Assert.DoesNotContain(CronFormField.DeliveryTo, edit.Validate().Keys);
        Assert.Equal("http://legacy.example.test/hook", edit.BuildDefinition(job)["delivery"]!["to"]!.GetValue<string>());
        Assert.Equal(CronFormError.WebhookUrlInvalid, (edit with { DeliveryTo = "http://other.example.test/hook" }).Validate()[CronFormField.DeliveryTo]);
        Assert.Equal(CronFormError.WebhookUrlInvalid, CronJobForm.FromClone(job, "Copy").Validate()[CronFormField.DeliveryTo]);
        Assert.DoesNotContain(CronFormField.DeliveryTo, (edit with { DeliveryTo = "https://other.example.test/hook" }).Validate().Keys);
    }
}
