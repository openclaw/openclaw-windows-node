using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Cron;

/// <summary>Everyday calendar rules the native editor authors; every other expression is web-managed.</summary>
public enum CronCalendarPattern { Hourly, Daily, Weekdays, Weekly, Monthly }

/// <summary>
/// Maps everyday calendar choices to five-field cron expressions and back. Expressions
/// that do not match one of these exact shapes are custom rules: shown read-only and
/// written back unchanged.
/// </summary>
public static class CronCalendar
{
    public static readonly TimeSpan DefaultTime = new(9, 0, 0);

    public static string Build(CronCalendarPattern pattern, TimeSpan time, DayOfWeek day = DayOfWeek.Monday)
    {
        var minute = time.Minutes.ToString(CultureInfo.InvariantCulture);
        var hour = time.Hours.ToString(CultureInfo.InvariantCulture);
        return pattern switch
        {
            CronCalendarPattern.Hourly => "0 * * * *",
            CronCalendarPattern.Daily => $"{minute} {hour} * * *",
            CronCalendarPattern.Weekdays => $"{minute} {hour} * * 1-5",
            CronCalendarPattern.Weekly => $"{minute} {hour} * * {(int)day}",
            _ => $"{minute} {hour} 1 * *",
        };
    }

    /// <summary>The pattern and time an expression was built from, or null for a custom rule.</summary>
    public static (CronCalendarPattern Pattern, TimeSpan Time)? TryParse(string? expr)
    {
        var fields = (expr ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return null;
        if (string.Join(' ', fields) == "0 * * * *")
            return (CronCalendarPattern.Hourly, DefaultTime);
        if (!Number.IsMatch(fields[0]) || !Number.IsMatch(fields[1]) || fields[3] != "*")
            return null;
        int minute = int.Parse(fields[0], CultureInfo.InvariantCulture), hour = int.Parse(fields[1], CultureInfo.InvariantCulture);
        if (minute > 59 || hour > 23)
            return null;
        CronCalendarPattern? pattern = (fields[2], fields[4]) switch
        {
            ("*", "*") => CronCalendarPattern.Daily,
            ("*", "1-5") => CronCalendarPattern.Weekdays,
            ("*", "0" or "1" or "2" or "3" or "4" or "5" or "6" or "7") => CronCalendarPattern.Weekly,
            ("1", "*") => CronCalendarPattern.Monthly,
            _ => null,
        };
        return pattern is { } p ? (p, new TimeSpan(hour, minute, 0)) : null;
    }

    public static DayOfWeek WeeklyDay(string? expr)
    {
        var fields = (expr ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 5 && int.TryParse(fields[4], out var day) && day is >= 0 and <= 7
            ? (DayOfWeek)(day % 7) : DayOfWeek.Monday;
    }

    private static readonly Regex Number = new(@"^(0|[1-9][0-9]?)$", RegexOptions.CultureInvariant);
}
