namespace OpenClaw.Shared.Cron;

public sealed record CronAgendaGroup(string Key, IReadOnlyList<CronJob> Jobs);

/// <summary>Only Gateway-reported instants, grouped by the user's exact local calendar dates.</summary>
public sealed record CronAgenda(
    IReadOnlyList<CronAgendaGroup> Groups,
    IReadOnlyList<CronJob> Repeating,
    IReadOnlyList<CronJob> WaitingForEvents,
    IReadOnlyList<CronJob> EarlierToday)
{
    public static CronAgenda Create(IEnumerable<CronJob> inventory, DateTimeOffset now, TimeZoneInfo zone)
    {
        var jobs = inventory.ToArray();
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var tomorrow = today.AddDays(1);
        var end = today.AddDays(2);
        var live = jobs.Where(j => j.Enabled && !j.IsAutoDisabled).ToArray();
        var running = live.Where(j => j.IsRunning).ToArray();
        var timed = live.Where(j => !j.IsRunning && j.Schedule.Kind is not
            (CronScheduleKind.Event or CronScheduleKind.Stream or CronScheduleKind.OnExit)
            && !IsRepeating(j.Schedule)
            && j.State.NextRunAtMs is not null).OrderBy(j => j.State.NextRunAtMs).ToArray();
        DateTime Local(CronJob j) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(j.State.NextRunAtMs!.Value), zone).DateTime;
        var hour = now.AddHours(1).ToUnixTimeMilliseconds();
        var groups = new[]
        {
            new CronAgendaGroup("Running", running),
            new CronAgendaGroup("Hour", timed.Where(j => Local(j).Date == today && j.State.NextRunAtMs <= hour).ToArray()),
            new CronAgendaGroup("Today", timed.Where(j => Local(j).Date == today && j.State.NextRunAtMs > hour).ToArray()),
            new CronAgendaGroup("Tomorrow", timed.Where(j => Local(j) >= tomorrow && Local(j) < end).ToArray()),
        };
        var earlier = jobs.Where(j => j.State.LastRunAtMs is { } at && at <= now.ToUnixTimeMilliseconds()
            && TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), zone).Date == today)
            .OrderByDescending(j => j.State.LastRunStatus == "error")
            .ThenByDescending(j => j.State.LastRunAtMs).ToArray();
        return new(groups.Where(g => g.Jobs.Count > 0).ToArray(),
            live.Where(j => !j.IsRunning && IsRepeating(j.Schedule)).ToArray(),
            live.Where(j => !j.IsRunning && j.Schedule.Kind is CronScheduleKind.Event or CronScheduleKind.Stream or CronScheduleKind.OnExit).ToArray(),
            earlier);
    }

    private static bool IsRepeating(CronSchedule schedule)
    {
        if (schedule.Kind == CronScheduleKind.Every) return true;
        if (schedule.Kind != CronScheduleKind.Cron) return false;
        var fields = (schedule.Expr ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 5 && fields[2] == "*" && fields[3] == "*" && fields[4] == "*"
            && (fields[1] == "*" || fields[1].StartsWith("*/", StringComparison.Ordinal));
    }
}
