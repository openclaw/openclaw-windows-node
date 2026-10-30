using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsageExportFormat
{
    SessionsCsv,
    DailyCsv,
    Json,
}

/// <summary>
/// Builds user-requested Usage exports with the upstream columns and file names
/// (openclaw/openclaw@5089aa71 ui/src/pages/usage/query.ts and export.ts). Never used for telemetry.
/// </summary>
internal static class UsageExport
{
    public static string FileName(UsageExportFormat format, DateOnly today)
    {
        var stamp = UsageFilters.Iso(today);
        return format switch
        {
            UsageExportFormat.Json => $"openclaw-usage-{stamp}.json",
            UsageExportFormat.SessionsCsv => $"openclaw-usage-sessions-{stamp}.csv",
            _ => $"openclaw-usage-daily-{stamp}.csv",
        };
    }

    /// <summary>Exports refuse incomplete data unless the Gateway confirms the selected usage is complete.</summary>
    public static bool IsExportable(SessionsUsageReport report) =>
        report.CacheStatus?.IsIncomplete != true || report.Overview?.SelectedUsageComplete == true;

    /// <summary>
    /// True when a sessions export would silently drop rows. Full-row reads return at most the row limit, while
    /// <c>aggregates.sessionCount</c> counts every matching session.
    /// </summary>
    public static bool IsTruncated(SessionsUsageReport report, IReadOnlyList<string> selectedSessions)
    {
        var rows = report.Sessions;
        if (selectedSessions.Count > 0 && selectedSessions.All(key => rows.Any(r => r.Key == key)))
            return false;
        return report.Aggregates.SessionCount is { } count
            ? count > rows.Count
            : rows.Count >= SessionsUsageQuery.FullRowLimit;
    }

    public static string Build(UsageExportFormat format, SessionsUsageReport report, IReadOnlyList<string> selectedSessions)
    {
        var selected = selectedSessions.ToHashSet(StringComparer.Ordinal);
        var sessions = selected.Count == 0 ? report.Sessions : report.Sessions.Where(s => selected.Contains(s.Key)).ToList();
        return format switch
        {
            UsageExportFormat.SessionsCsv => SessionsCsv(sessions),
            UsageExportFormat.DailyCsv => DailyCsv(report.Aggregates.CostDaily),
            _ => Json(report, selected),
        };
    }

    public static string SessionsCsv(IReadOnlyList<SessionUsageRow> sessions)
    {
        var builder = new StringBuilder();
        Row(builder, ["key", "label", "agentId", "channel", "provider", "model", "updatedAt", "durationMs", "messages", "errors",
            "toolCalls", "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "totalTokens", "totalCost"]);
        foreach (var s in sessions)
        {
            var usage = s.Usage;
            var totals = usage?.Totals;
            var known = totals is { IsLowerBound: false };
            Row(builder,
            [
                s.Key, s.Label, s.AgentId, s.Channel, s.ModelProvider ?? s.ProviderOverride, s.Model ?? s.ModelOverride,
                s.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                Num(usage?.DurationMs), Num(usage?.MessageCounts?.Total), Num(usage?.MessageCounts?.Errors),
                Num(usage?.MessageCounts?.ToolCalls),
                known ? Num(totals!.Input) : null, known ? Num(totals!.Output) : null, known ? Num(totals!.CacheRead) : null,
                known ? Num(totals!.CacheWrite) : null, known ? Num(totals!.TotalTokens) : null, known ? Num(totals!.TotalCost) : null,
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
            Row(builder,
            [
                day.Date,
                known ? Num(t.Input) : null, known ? Num(t.Output) : null, known ? Num(t.CacheRead) : null,
                known ? Num(t.CacheWrite) : null, known ? Num(t.TotalTokens) : null,
                known ? Num(t.InputCost) : null, known ? Num(t.OutputCost) : null, known ? Num(t.CacheReadCost) : null,
                known ? Num(t.CacheWriteCost) : null, known ? Num(t.TotalCost) : null,
            ]);
        }
        return builder.ToString().TrimEnd('\n');
    }

    private static string Json(SessionsUsageReport report, HashSet<string> selected)
    {
        if (report.Raw is not { ValueKind: JsonValueKind.Object } raw)
            return "{}";
        var source = JsonNode.Parse(raw.GetRawText())!.AsObject();
        var sessions = source["sessions"] is JsonArray all
            ? new JsonArray(all.Where(s => selected.Count == 0
                    || (s?["key"] is JsonValue value && value.TryGetValue<string>(out var key) && selected.Contains(key)))
                .Select(s => s?.DeepClone()).ToArray())
            : [];
        var aggregates = source["aggregates"]?.DeepClone();
        var export = new JsonObject
        {
            ["sessions"] = sessions,
            ["totals"] = source["totals"]?.DeepClone(),
            ["aggregates"] = aggregates,
            ["daily"] = aggregates?["costDaily"]?.DeepClone() ?? new JsonArray(),
        };
        return export.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);

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
