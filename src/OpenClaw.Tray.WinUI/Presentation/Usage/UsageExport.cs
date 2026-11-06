using System.Globalization;
using System.Text;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsageExportFormat
{
    SessionsCsv,
    DailyCsv,
}

/// <summary>
/// Builds user-requested CSV exports with the upstream web columns and explicit reporting-range file names
/// (openclaw/openclaw@5089aa71 ui/src/pages/usage/query.ts and export.ts). Never used for telemetry.
/// </summary>
internal static class UsageExport
{
    public static string FileName(UsageExportFormat format, UsageFilters filters)
    {
        var stamp = filters.Start == filters.End ? UsageFilters.Iso(filters.Start)
            : $"{UsageFilters.Iso(filters.Start)}_to_{UsageFilters.Iso(filters.End)}";
        return format == UsageExportFormat.SessionsCsv
            ? $"usage-sessions-{stamp}.csv"
            : $"usage-daily-{stamp}.csv";
    }

    /// <summary>Exports refuse results the Gateway marks as still loading or cut short.</summary>
    public static bool IsExportable(SessionsUsageReport report) =>
        report.CacheStatus?.IsIncomplete != true && report.Partial is null && !report.Totals.IsLowerBound
        && report.SessionsKnown && report.Totals.HasValue("totalTokens") && report.Totals.HasValue("totalCost")
        && !report.Sessions.Any(session => session.Computing);

    /// <summary>
    /// True when a sessions export would silently drop rows. Full-row reads return at most the row limit, while
    /// <c>aggregates.sessionCount</c> counts every matching session.
    /// </summary>
    public static bool IsTruncated(SessionsUsageReport report) =>
        (report.Aggregates.SessionCount ?? report.Overview?.Total) is { } count
            ? count > report.Sessions.Count
            : report.Sessions.Count >= SessionsUsageQuery.FullRowLimit;

    public static string Build(UsageExportFormat format, SessionsUsageReport report) =>
        format == UsageExportFormat.SessionsCsv ? SessionsCsv(report.Sessions) : DailyCsv(report.Aggregates.CostDaily);

    public static string SessionsCsv(IReadOnlyList<SessionUsageRow> sessions)
    {
        var builder = new StringBuilder();
        Row(builder, ["key", "label", "agentId", "channel", "provider", "model", "updatedAt", "durationMs", "messages", "errors",
            "toolCalls", "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "totalTokens", "totalCost"]);
        foreach (var s in sessions)
        {
            var usage = s.Usage;
            var totals = usage?.Totals;
            // Lower-bound counters are left blank rather than exported as if they were complete.
            var known = totals is { IsLowerBound: false };
            Row(builder,
            [
                s.Key, s.Label, s.AgentId, s.Channel, s.EffectiveProvider, s.EffectiveModel,
                s.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                Num(usage?.DurationMs), usage?.MessageCounts is { HasData: true } messages ? Num(messages.Total) : null,
                usage?.MessageCounts is { HasData: true } errors ? Num(errors.Errors) : null,
                usage?.MessageCounts is { HasData: true } calls ? Num(calls.ToolCalls) : null,
                known ? ReportedNum(totals!, "input", totals!.Input) : null,
                known ? ReportedNum(totals!, "output", totals!.Output) : null,
                known ? ReportedNum(totals!, "cacheRead", totals!.CacheRead) : null,
                known ? ReportedNum(totals!, "cacheWrite", totals!.CacheWrite) : null,
                known ? ReportedNum(totals!, "totalTokens", totals!.TotalTokens) : null,
                known && totals!.MissingCostEntries == 0 ? ReportedNum(totals, "totalCost", totals.TotalCost) : null,
            ]);
        }
        return builder.ToString().TrimEnd('\n');
    }

    public static string DailyCsv(IReadOnlyList<UsageDailyTotals> daily)
    {
        var builder = new StringBuilder();
        Row(builder, ["date", "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "totalTokens",
            "inputCost", "outputCost", "cacheReadCost", "cacheWriteCost", "totalCost"]);
        foreach (var day in daily)
        {
            var t = day.Totals;
            var known = !t.IsLowerBound;
            var priced = known && t.MissingCostEntries == 0;
            Row(builder,
            [
                day.Date,
                known ? ReportedNum(t, "input", t.Input) : null, known ? ReportedNum(t, "output", t.Output) : null,
                known ? ReportedNum(t, "cacheRead", t.CacheRead) : null,
                known ? ReportedNum(t, "cacheWrite", t.CacheWrite) : null, known ? ReportedNum(t, "totalTokens", t.TotalTokens) : null,
                priced ? ReportedNum(t, "inputCost", t.InputCost) : null, priced ? ReportedNum(t, "outputCost", t.OutputCost) : null,
                priced ? ReportedNum(t, "cacheReadCost", t.CacheReadCost) : null,
                priced ? ReportedNum(t, "cacheWriteCost", t.CacheWriteCost) : null, priced ? ReportedNum(t, "totalCost", t.TotalCost) : null,
            ]);
        }
        return builder.ToString().TrimEnd('\n');
    }

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string? ReportedNum(UsageTotals totals, string field, double value) =>
        totals.HasValue(field) ? Num(value) : null;
    private static string? ReportedNum(UsageTotals totals, string field, long value) =>
        totals.HasValue(field) ? Num(value) : null;

    private static string? Num(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Num(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);

    private static void Row(StringBuilder builder, string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(Cell(values[i]));
        }
        builder.Append('\n');
    }

    /// <summary>Quotes CSV delimiters and neutralizes spreadsheet formula prefixes, as upstream does.</summary>
    internal static string Cell(string? value)
    {
        if (value is null) return "";
        var safe = value.TrimStart(' ', '\t', '\r', '\n') is { Length: > 0 } trimmed
            && "=+-@\uFF0B\uFF0D\uFF1D\uFF20".Contains(trimmed[0])
                ? "'" + value
                : value;
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }
}
