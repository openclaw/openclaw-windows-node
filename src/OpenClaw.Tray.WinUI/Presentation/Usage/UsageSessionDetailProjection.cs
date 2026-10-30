using System.Text.RegularExpressions;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsageContextCategory
{
    System,
    Skills,
    Tools,
    Files,
}

internal sealed record UsageContextEntry(string Name, long? Chars);

internal sealed record UsageContextGroup(UsageContextCategory Category, long Tokens, double Share, IReadOnlyList<UsageContextEntry> Entries);

/// <summary>Approximate system-prompt weight. <see cref="InputShare"/> is null when input usage is unknown.</summary>
internal sealed record UsageContextBreakdown(long TotalTokens, double? InputShare, IReadOnlyList<UsageContextGroup> Groups);

internal enum UsageTimelineMode
{
    PerTurn,
    Cumulative,
}

internal sealed record UsageTimelineBar(DateTimeOffset Timestamp, double Total, IReadOnlyList<UsageChartSegment> Segments, long Tokens, double Cost);

internal sealed record UsageTimeline(IReadOnlyList<UsageTimelineBar> Bars, double Ceiling, bool LowerBound);

internal sealed record UsageModelMixRow(string? Provider, string? Model, int Count, long Tokens, double Cost, double? Share, bool LowerBound);

/// <summary>A conversation entry with tool markers removed and counted, as the web page does.</summary>
internal sealed record UsageLogView(
    DateTimeOffset Timestamp, string Role, string Content, IReadOnlyList<(string Name, int Count)> Tools, long? Tokens, double? Cost);

internal sealed record UsageLogFilter(string? Role = null, string? Tool = null, bool HasToolsOnly = false, string Search = "")
{
    public bool IsActive => Role is not null || Tool is not null || HasToolsOnly || !string.IsNullOrWhiteSpace(Search);
}

/// <summary>WinUI-free projection of one session's detail reads (openclaw/openclaw@5089aa71 view-details.tsx, view-timeseries.tsx, helpers.ts).</summary>
internal static partial class UsageSessionDetailProjection
{
    private const int CharsPerToken = 4;

    public static long CharsToTokens(long chars) => (long)Math.Round(chars / (double)CharsPerToken, MidpointRounding.AwayFromZero);

    public static SessionUsageRow? SummaryRow(SessionUsageRow? overview, SessionUsageRow? detail, bool filtered) =>
        filtered
            ? overview is null ? null : overview with { ContextWeight = detail?.ContextWeight ?? overview.ContextWeight }
            : detail ?? overview;

    public static IReadOnlyList<UsageModelMixRow> ModelMix(SessionUsageRow row)
    {
        var models = row.Usage?.ModelUsage ?? Array.Empty<UsageModelBreakdown>();
        var totalCost = models.Sum(m => m.Totals.TotalCost);
        var totalTokens = models.Sum(m => (double)m.Totals.TotalTokens);
        var incomplete = row.Usage?.Totals.IsLowerBound == true || models.Any(m => m.Totals.IsLowerBound);
        return models
            .OrderByDescending(m => m.Totals.TotalCost).ThenByDescending(m => m.Totals.TotalTokens)
            .Select(m => new UsageModelMixRow(m.Provider, m.Model, m.Count, m.Totals.TotalTokens, m.Totals.TotalCost,
                incomplete ? null : totalCost > 0 ? m.Totals.TotalCost / totalCost : totalTokens > 0 ? m.Totals.TotalTokens / totalTokens : 0,
                m.Totals.IsLowerBound))
            .ToList();
    }

    public static UsageContextBreakdown? Context(SessionUsageRow row)
    {
        if (row.ContextWeight is not { } weight) return null;
        var fileChars = weight.Files.Sum(f => f.InjectedChars ?? 0);
        static IReadOnlyList<UsageContextEntry> Sorted(IEnumerable<UsageContextEntry> entries) =>
            entries.OrderBy(e => e.Chars is null).ThenByDescending(e => e.Chars ?? 0).ToList();

        var raw = new (UsageContextCategory Category, long Tokens, IReadOnlyList<UsageContextEntry> Entries)[]
        {
            (UsageContextCategory.System,
                CharsToTokens(Math.Max(0, weight.SystemPromptChars - fileChars - weight.SkillsPromptChars - weight.ToolListChars)),
                Array.Empty<UsageContextEntry>()),
            (UsageContextCategory.Skills, CharsToTokens(weight.SkillsPromptChars),
                Sorted(weight.Skills.Select(s => new UsageContextEntry(s.Name, s.BlockChars)))),
            (UsageContextCategory.Tools, CharsToTokens(weight.ToolListChars + weight.ToolSchemaChars),
                Sorted(weight.Tools.Select(t => new UsageContextEntry(t.Name, t.SummaryChars + t.SchemaChars)))),
            (UsageContextCategory.Files, CharsToTokens(fileChars),
                Sorted(weight.Files.Select(f => new UsageContextEntry(f.Name, f.InjectedChars)))),
        };
        var total = raw.Sum(g => g.Tokens);
        var usage = row.Usage?.Totals;
        var input = usage is { IsLowerBound: false, TotalTokens: > 0 } ? usage.Input + usage.CacheRead : 0;
        return new UsageContextBreakdown(
            total,
            input > 0 ? Math.Min((double)total / input, 1) : null,
            raw.Select(g => new UsageContextGroup(g.Category, g.Tokens, total > 0 ? (double)g.Tokens / total : 0, g.Entries)).ToList());
    }

    public static UsageTimeline Timeline(SessionUsageTimeseries series, UsageMetric metric, UsageTimelineMode mode, bool byType)
    {
        var bars = series.Points.Select(p =>
        {
            if (mode == UsageTimelineMode.Cumulative)
            {
                var cumulative = metric == UsageMetric.Cost ? p.CumulativeCost : p.CumulativeTokens;
                return new UsageTimelineBar(p.Timestamp, cumulative, [new UsageChartSegment("total", cumulative)], p.CumulativeTokens, p.CumulativeCost);
            }
            if (byType && metric == UsageMetric.Tokens)
            {
                return new UsageTimelineBar(p.Timestamp, p.TotalTokens,
                [
                    new("output", p.Output), new("input", p.Input), new("cacheWrite", p.CacheWrite), new("cacheRead", p.CacheRead),
                ], p.TotalTokens, p.Cost);
            }
            var value = metric == UsageMetric.Cost ? p.Cost : p.TotalTokens;
            return new UsageTimelineBar(p.Timestamp, value, [new UsageChartSegment("total", value)], p.TotalTokens, p.Cost);
        }).ToList();
        var max = bars.Count == 0 ? 0 : bars.Max(b => b.Segments.Sum(s => s.Value));
        return new UsageTimeline(bars, UsageFormat.NiceCeiling(max), series.Report == UsageReportState.Unavailable);
    }

    public static UsageLogView ParseLog(SessionUsageLogEntry entry)
    {
        var tools = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var line in entry.Content.Split('\n'))
        {
            var trimmed = line.Trim();
            var match = ToolMarker().Match(trimmed);
            if (match.Success && match.Groups[1].Value.Trim() is { Length: > 0 } name)
            {
                tools[name] = tools.GetValueOrDefault(name) + 1;
                continue;
            }
            if (trimmed.StartsWith("[Tool Result]", StringComparison.Ordinal)) continue;
            lines.Add(line);
        }
        var clean = string.Join('\n', lines).Trim();
        return new UsageLogView(
            entry.Timestamp, entry.Role, clean.Length > 0 ? clean : entry.Content,
            tools.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => (t.Key, t.Value)).ToList(),
            entry.Tokens, entry.Cost);
    }

    public static IReadOnlyList<UsageLogView> FilterLogs(IReadOnlyList<UsageLogView> logs, UsageLogFilter filter)
    {
        IEnumerable<UsageLogView> result = logs;
        if (filter.Role is { } role) result = result.Where(l => l.Role == role);
        if (filter.Tool is { } tool) result = result.Where(l => l.Tools.Any(t => t.Name == tool));
        if (filter.HasToolsOnly) result = result.Where(l => l.Tools.Count > 0);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            result = result.Where(l => l.Content.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        return result.ToList();
    }

    [GeneratedRegex(@"^\[Tool:\s*([^\]]+)\]")]
    private static partial Regex ToolMarker();
}
