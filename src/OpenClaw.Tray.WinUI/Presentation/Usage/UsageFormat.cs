using System.Globalization;

namespace OpenClawTray.Presentation.Usage;

/// <summary>
/// Culture-aware Usage number formatting. Follows the upstream web rules (compact K/M/B tokens with one
/// optional decimal, USD with 2/4/6 decimals by magnitude) but uses the user's number and date conventions.
/// </summary>
internal static class UsageFormat
{
    public static string Tokens(long tokens, CultureInfo culture)
    {
        var magnitude = Math.Abs((double)tokens);
        return magnitude switch
        {
            >= 1_000_000_000 => (tokens / 1_000_000_000d).ToString("0.#", culture) + "B",
            >= 1_000_000 => (tokens / 1_000_000d).ToString("0.#", culture) + "M",
            >= 1_000 => (tokens / 1_000d).ToString("0.#", culture) + "K",
            _ => tokens.ToString("N0", culture),
        };
    }

    public static string Count(long value, CultureInfo culture) => value.ToString("N0", culture);

    /// <summary>US dollars with fixed decimals in the user's number format.</summary>
    public static string Cost(double value, CultureInfo culture, int decimals = 2)
    {
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = "$";
        format.CurrencyDecimalDigits = decimals;
        return value.ToString("C", format);
    }

    /// <summary>Keeps small amounts readable: 2 decimals from one cent, then 4, then 6.</summary>
    public static string AnalysisCost(double value, CultureInfo culture)
    {
        var magnitude = Math.Abs(value);
        var decimals = magnitude == 0 || magnitude >= 0.01 ? 2 : magnitude >= 0.0001 ? 4 : 6;
        return Cost(value, culture, decimals);
    }

    public static string Percent(double ratio, CultureInfo culture, int decimals = 1) =>
        (ratio * 100).ToString("F" + decimals, culture) + culture.NumberFormat.PercentSymbol;

    // Gateway durations are untrusted; TimeSpan throws past about 29,000 years.
    private static readonly double MaxDurationMs = TimeSpan.FromDays(36_500).TotalMilliseconds;

    /// <summary>Two most significant units, e.g. <c>2d 3h</c>, <c>1h 30m</c>, <c>45s</c>.</summary>
    public static string Duration(double milliseconds)
    {
        if (milliseconds <= 0 || double.IsNaN(milliseconds)) return "0s";
        var span = TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDurationMs));
        if (span.TotalDays >= 1) return span.Hours > 0 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return span.Minutes > 0 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalHours}h";
        if (span.TotalMinutes >= 1) return span.Seconds > 0 && span.TotalMinutes < 10 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{(int)span.TotalMinutes}m";
        return $"{Math.Max(1, (int)span.TotalSeconds)}s";
    }

    public static string DayLabel(DateOnly date, CultureInfo culture) =>
        date.ToString(culture.DateTimeFormat.MonthDayPattern.Contains("MMMM", StringComparison.Ordinal)
            ? culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal)
            : culture.DateTimeFormat.MonthDayPattern, culture);

    public static string FullDate(DateOnly date, CultureInfo culture) => date.ToString("D", culture);

    /// <summary>Compact range for the mast; crossing a year keeps both years explicit.</summary>
    public static string CompactRange(DateOnly start, DateOnly end, CultureInfo culture, string rangeFormat)
    {
        var crossesYear = start.Year != end.Year;
        return string.Format(culture, rangeFormat,
            crossesYear ? start.ToString("d", culture) : DayLabel(start, culture),
            crossesYear ? end.ToString("d", culture) : DayLabel(end, culture));
    }

    public static string FriendlyDate(DateTime local, DateTime now, CultureInfo culture, string todayLabel, bool olderTime = false)
    {
        var days = (local.Date - now.Date).Days;
        var day = days == 0 ? todayLabel
            : Math.Abs(days) < 7 ? local.ToString("ddd", culture)
            : DayLabel(DateOnly.FromDateTime(local), culture);
        return days == 0 || Math.Abs(days) < 7 || olderTime ? $"{day} {local.ToString("t", culture)}" : day;
    }

    public static bool TryParseIso(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Upstream's "nice" axis ceiling: 1, 2 or 5 times a power of ten.</summary>
    public static double NiceCeiling(double max)
    {
        if (max <= 0 || double.IsNaN(max)) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1d, 2d, 5d, 10d })
        {
            if (max <= step * power) return step * power;
        }
        return 10 * power;
    }
}
