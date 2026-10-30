using System.Text.Json;

namespace OpenClaw.Shared.Usage;

/// <summary>
/// Total (never-throwing) projection of Gateway usage JSON into the usage records in this namespace.
/// Missing, null or mistyped fields fall back to empty values so a partial or newer payload still
/// renders. Unknown fields are ignored.
/// </summary>
public static class UsageJsonParser
{
    private const int MaxTimeseriesPoints = 200;
    private const int MaxLogEntries = 1000;

    public static SessionsUsageReport ParseSessionsUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new SessionsUsageReport();

        return new SessionsUsageReport
        {
            UpdatedAt = Timestamp(payload, "updatedAt"),
            StartDate = Str(payload, "startDate") ?? "",
            EndDate = Str(payload, "endDate") ?? "",
            Sessions = Array(payload, "sessions", ParseRow),
            Totals = Totals(Child(payload, "totals")),
            Aggregates = Aggregates(Child(payload, "aggregates")),
            CreatorOptions = Array(payload, "creatorOptions", Creator),
            CacheStatus = CacheStatus(Child(payload, "cacheStatus")),
            Overview = Overview(Child(payload, "overview")),
            Partial = Partial(Child(payload, "partial")),
            Raw = payload.Clone(),
        };
    }

    public static SessionUsageTimeseries ParseTimeseries(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new SessionUsageTimeseries(UsageReportState.Unknown, null, System.Array.Empty<UsageTimePoint>());

        var points = Array(payload, "points", point =>
        {
            var timestamp = Timestamp(point, "timestamp");
            return timestamp is null
                ? null
                : new UsageTimePoint(
                    timestamp.Value,
                    Long(point, "input"), Long(point, "output"), Long(point, "cacheRead"), Long(point, "cacheWrite"),
                    Long(point, "totalTokens"), Double(point, "cost"),
                    Long(point, "cumulativeTokens"), Double(point, "cumulativeCost"));
        });
        if (points.Count > MaxTimeseriesPoints)
            points = points.Take(MaxTimeseriesPoints).ToArray();
        return new SessionUsageTimeseries(Report(Child(payload, "usageReport")), Str(payload, "sessionId"), points);
    }

    public static IReadOnlyList<SessionUsageLogEntry> ParseLogs(JsonElement payload)
    {
        var logs = Array(payload, "logs", entry =>
        {
            var timestamp = Timestamp(entry, "timestamp");
            var role = Str(entry, "role");
            if (timestamp is null || string.IsNullOrEmpty(role))
                return null;
            return new SessionUsageLogEntry(
                timestamp.Value, role, Str(entry, "content") ?? "",
                NullableLong(entry, "tokens"), NullableDouble(entry, "cost"));
        });
        return logs.Count > MaxLogEntries ? logs.Take(MaxLogEntries).ToArray() : logs;
    }

    public static ProviderUsageSummary ParseProviderUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new ProviderUsageSummary(null, System.Array.Empty<ProviderUsageSnapshot>(), false);

        return new ProviderUsageSummary(
            Timestamp(payload, "updatedAt"),
            Array(payload, "providers", Provider),
            Bool(payload, "refreshing"));
    }

    /// <summary>
    /// Projects a legacy <c>usage.cost</c> summary into a session-less report so Gateways without
    /// <c>sessions.usage</c> still render totals and the daily chart.
    /// </summary>
    public static SessionsUsageReport ParseCostSummary(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new SessionsUsageReport();
        var daily = Array(payload, "daily", DailyTotals);
        return new SessionsUsageReport
        {
            UpdatedAt = Timestamp(payload, "updatedAt"),
            StartDate = daily.Count > 0 ? daily[0].Date : "",
            EndDate = daily.Count > 0 ? daily[^1].Date : "",
            Totals = Totals(Child(payload, "totals")),
            Aggregates = new UsageAggregates { CostDaily = daily },
            CacheStatus = CacheStatus(Child(payload, "cacheStatus")),
        };
    }

    internal static SessionUsageRow? ParseRow(JsonElement row)
    {
        var key = Str(row, "key");
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var usage = Child(row, "usage");
        return new SessionUsageRow
        {
            Key = key,
            Label = Str(row, "label"),
            Scope = Str(row, "scope"),
            HistoricalInstanceCount = NullableInt(row, "historicalInstanceCount"),
            UpdatedAt = Timestamp(row, "updatedAt"),
            AgentId = Str(row, "agentId"),
            CreatedActor = Actor(Child(row, "createdActor")),
            CreatorKey = Str(row, "creatorKey"),
            Channel = Str(row, "channel"),
            ModelOverride = Str(row, "modelOverride"),
            ProviderOverride = Str(row, "providerOverride"),
            ModelProvider = Str(row, "modelProvider"),
            Model = Str(row, "model"),
            Usage = usage is { } u ? SessionSummary(u) : null,
            Computing = Bool(row, "computing"),
            ContextWeight = ContextWeight(Child(row, "contextWeight")),
        };
    }

    private static SessionUsageSummary SessionSummary(JsonElement usage) => new()
    {
        Totals = Totals(usage),
        Refreshing = Bool(usage, "refreshing"),
        FirstActivity = Timestamp(usage, "firstActivity"),
        LastActivity = Timestamp(usage, "lastActivity"),
        DurationMs = NullableLong(usage, "durationMs"),
        MessageCounts = Child(usage, "messageCounts") is { } counts ? MessageCounts(counts) : null,
        ToolUsage = Child(usage, "toolUsage") is { } tools ? ToolSummary(tools) : null,
        ModelUsage = Array(usage, "modelUsage", ModelBreakdown),
        Latency = Latency(Child(usage, "latency")),
    };

    internal static UsageTotals Totals(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } totals)
            return UsageTotals.Empty;

        return new UsageTotals
        {
            Report = Report(Child(totals, "usageReport")),
            Input = Long(totals, "input"),
            Output = Long(totals, "output"),
            CacheRead = Long(totals, "cacheRead"),
            CacheWrite = Long(totals, "cacheWrite"),
            TotalTokens = Long(totals, "totalTokens"),
            TotalCost = Double(totals, "totalCost"),
            InputCost = Double(totals, "inputCost"),
            OutputCost = Double(totals, "outputCost"),
            CacheReadCost = Double(totals, "cacheReadCost"),
            CacheWriteCost = Double(totals, "cacheWriteCost"),
            MissingCostEntries = JsonReadHelpers.GetInt(totals, "missingCostEntries"),
        };
    }

    private static UsageAggregates Aggregates(JsonElement? element)
    {
        if (element is not { } aggregates)
            return UsageAggregates.Empty;

        return new UsageAggregates
        {
            SessionCount = NullableInt(aggregates, "sessionCount"),
            Messages = Child(aggregates, "messages") is { } messages ? MessageCounts(messages) : UsageMessageCounts.Empty,
            Tools = Child(aggregates, "tools") is { } tools ? ToolSummary(tools) : UsageToolSummary.Empty,
            ByModel = Array(aggregates, "byModel", ModelBreakdown),
            ByProvider = Array(aggregates, "byProvider", ModelBreakdown),
            ByAgent = Array(aggregates, "byAgent", item => Named(item, "agentId")),
            ByChannel = Array(aggregates, "byChannel", item => Named(item, "channel")),
            ByCreator = Array(aggregates, "byCreator", CreatorAggregate),
            CostDaily = Array(aggregates, "costDaily", DailyTotals),
            ModelDaily = Array(aggregates, "modelDaily", item =>
                Str(item, "date") is { } date
                    ? new UsageDailyModel(date, Str(item, "provider"), Str(item, "model"), Long(item, "tokens"),
                        Double(item, "cost"), JsonReadHelpers.GetInt(item, "count"), Report(Child(item, "usageReport")))
                    : null),
            Daily = Array(aggregates, "daily", item =>
                Str(item, "date") is { } date
                    ? new UsageDailyActivity(date, Long(item, "tokens"), Double(item, "cost"),
                        JsonReadHelpers.GetInt(item, "messages"), JsonReadHelpers.GetInt(item, "toolCalls"),
                        JsonReadHelpers.GetInt(item, "errors"), Report(Child(item, "usageReport")))
                    : null),
        };
    }

    private static UsageOverview? Overview(JsonElement? element)
    {
        if (element is not { } overview)
            return null;

        var filterOptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (Child(overview, "filterOptions") is { } options)
        {
            foreach (var property in options.EnumerateObject())
                filterOptions[property.Name] = Strings(options, property.Name);
        }

        var table = Child(overview, "tableTotals");
        return new UsageOverview
        {
            Report = Report(Child(overview, "usageReport")),
            Total = JsonReadHelpers.GetInt(overview, "total"),
            UnfilteredSessionCount = JsonReadHelpers.GetInt(overview, "unfilteredSessionCount"),
            SelectedUsageComplete = NullableBool(overview, "selectedUsageComplete"),
            SelectedRows = Array(overview, "selectedRows", ParseRow),
            TableSessionCount = JsonReadHelpers.GetInt(overview, "tableSessionCount"),
            TableTotals = table is { } t
                ? new UsageTableTotals(Long(t, "tokens"), Double(t, "cost"), JsonReadHelpers.GetInt(t, "errors"),
                    Report(Child(t, "usageReport")))
                : new UsageTableTotals(0, 0, 0, UsageReportState.Unknown),
            Offset = JsonReadHelpers.GetInt(overview, "offset"),
            Limit = JsonReadHelpers.GetInt(overview, "limit"),
            QueryWarnings = Strings(overview, "queryWarnings"),
            HourTokens = Numbers(overview, "hourTokens", 24),
            WeekdayTokens = Numbers(overview, "weekdayTokens", 7),
            HasTimelineData = Bool(overview, "hasTimelineData"),
            DurationMs = Long(overview, "durationMs"),
            DurationCount = JsonReadHelpers.GetInt(overview, "durationCount"),
            HourlyMessages = Numbers(overview, "hourlyMessages", 24),
            HourlyErrors = Numbers(overview, "hourlyErrors", 24),
            FilterOptions = filterOptions,
        };
    }

    private static UsageCacheStatus? CacheStatus(JsonElement? element)
    {
        if (element is not { } status || Str(status, "status") is not { } value)
            return null;
        return new UsageCacheStatus(
            value,
            JsonReadHelpers.GetInt(status, "pendingFiles"),
            JsonReadHelpers.GetInt(status, "staleFiles"),
            Partial(Child(status, "partial")));
    }

    private static UsagePartial? Partial(JsonElement? element) =>
        element is { } partial && Str(partial, "reason") is { } reason
            ? new UsagePartial(reason, Str(partial, "message") ?? "")
            : null;

    private static UsageDailyTotals? DailyTotals(JsonElement item) =>
        Str(item, "date") is { } date ? new UsageDailyTotals(date, Totals(item)) : null;

    private static UsageMessageCounts MessageCounts(JsonElement counts) => new(
        JsonReadHelpers.GetInt(counts, "total"), JsonReadHelpers.GetInt(counts, "user"),
        JsonReadHelpers.GetInt(counts, "assistant"), JsonReadHelpers.GetInt(counts, "toolCalls"),
        JsonReadHelpers.GetInt(counts, "toolResults"), JsonReadHelpers.GetInt(counts, "errors"));

    private static UsageToolSummary ToolSummary(JsonElement tools) => new(
        JsonReadHelpers.GetInt(tools, "totalCalls"),
        JsonReadHelpers.GetInt(tools, "uniqueTools"),
        Array(tools, "tools", item =>
            Str(item, "name") is { Length: > 0 } name ? new UsageToolCount(name, JsonReadHelpers.GetInt(item, "count")) : null));

    private static UsageModelBreakdown? ModelBreakdown(JsonElement item) => new(
        Str(item, "provider"), Str(item, "model"), JsonReadHelpers.GetInt(item, "count"), Totals(Child(item, "totals")));

    private static UsageNamedTotals? Named(JsonElement item, string nameProperty) =>
        Str(item, nameProperty) is { Length: > 0 } name ? new UsageNamedTotals(name, Totals(Child(item, "totals"))) : null;

    private static UsageCreator? Creator(JsonElement item) =>
        Str(item, "key") is { Length: > 0 } key ? new UsageCreator(key, Actor(Child(item, "actor"))) : null;

    private static UsageCreatorAggregate? CreatorAggregate(JsonElement item) =>
        Creator(item) is { } creator
            ? new UsageCreatorAggregate(
                creator,
                Totals(Child(item, "totals")),
                JsonReadHelpers.GetInt(item, "sessionCount"),
                Array(item, "daily", DailyTotals))
            : null;

    private static UsageCreatedActor? Actor(JsonElement? element) =>
        element is { } actor && Str(actor, "type") is { } type
            ? new UsageCreatedActor(type, Str(actor, "id"), Str(actor, "label"))
            : null;

    private static UsageLatencyStats? Latency(JsonElement? element) =>
        element is { } latency && JsonReadHelpers.GetInt(latency, "count") > 0
            ? new UsageLatencyStats(JsonReadHelpers.GetInt(latency, "count"), Double(latency, "avgMs"), Double(latency, "p95Ms"))
            : null;

    private static UsageContextWeight? ContextWeight(JsonElement? element)
    {
        if (element is not { } report)
            return null;
        var systemPrompt = Child(report, "systemPrompt");
        var skills = Child(report, "skills");
        var tools = Child(report, "tools");
        return new UsageContextWeight
        {
            Source = Str(report, "source") ?? "",
            Provider = Str(report, "provider"),
            Model = Str(report, "model"),
            SystemPromptChars = systemPrompt is { } sp ? Long(sp, "chars") : 0,
            SkillsPromptChars = skills is { } s ? Long(s, "promptChars") : 0,
            Skills = skills is { } se
                ? Array(se, "entries", e => Str(e, "name") is { } name ? new UsageContextSkill(name, Long(e, "blockChars")) : null)
                : System.Array.Empty<UsageContextSkill>(),
            ToolListChars = tools is { } tl ? Long(tl, "listChars") : 0,
            ToolSchemaChars = tools is { } ts ? Long(ts, "schemaChars") : 0,
            Tools = tools is { } te
                ? Array(te, "entries", e => Str(e, "name") is { } name
                    ? new UsageContextTool(name, Str(e, "source"), Long(e, "summaryChars"), Long(e, "schemaChars"))
                    : null)
                : System.Array.Empty<UsageContextTool>(),
            Files = Array(report, "injectedWorkspaceFiles", f => Str(f, "name") is { } name
                ? new UsageContextFile(name, NullableLong(f, "injectedChars"))
                : null),
        };
    }

    private static ProviderUsageSnapshot? Provider(JsonElement item)
    {
        var provider = Str(item, "provider");
        if (string.IsNullOrWhiteSpace(provider))
            return null;
        return new ProviderUsageSnapshot
        {
            Provider = provider,
            DisplayName = Str(item, "displayName") is { Length: > 0 } name ? name : provider,
            Windows = Array(item, "windows", w => Str(w, "label") is { } label
                ? new ProviderUsageWindow(label, Str(w, "groupLabel"), Double(w, "usedPercent"), Timestamp(w, "resetAt"))
                : null),
            Billing = Array(item, "billing", Billing),
            CostHistory = CostHistory(Child(item, "costHistory")),
            Summary = Str(item, "summary"),
            Plan = Str(item, "plan"),
            AccountEmail = Str(item, "accountEmail"),
            Error = Str(item, "error"),
        };
    }

    private static ProviderUsageBilling? Billing(JsonElement item)
    {
        var type = Str(item, "type");
        var unit = Str(item, "unit");
        if (type is null || unit is null)
            return null;
        return type switch
        {
            "balance" or "spend" => new ProviderUsageBilling(type, Str(item, "label"), Double(item, "amount"), null, unit,
                Timestamp(item, "resetAt")),
            "budget" => new ProviderUsageBilling(type, Str(item, "label"), Double(item, "used"), Double(item, "limit"), unit,
                Timestamp(item, "resetAt")),
            _ => null,
        };
    }

    private static ProviderUsageCostHistory? CostHistory(JsonElement? element)
    {
        if (element is not { } history || Str(history, "unit") is not { } unit)
            return null;
        return new ProviderUsageCostHistory(
            unit,
            JsonReadHelpers.GetInt(history, "periodDays"),
            Array(history, "daily", d => Str(d, "date") is { } date ? new ProviderUsageCostDay(date, Double(d, "amount")) : null));
    }

    private static UsageReportState Report(JsonElement? element) =>
        element is { } report
            ? Str(report, "state") switch
            {
                "available" => UsageReportState.Available,
                "unavailable" => UsageReportState.Unavailable,
                _ => UsageReportState.Unknown,
            }
            : UsageReportState.Unknown;

    // ── primitive readers ──

    private static JsonElement? Child(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? Str(JsonElement parent, string property) => JsonReadHelpers.GetString(parent, property);

    private static long Long(JsonElement parent, string property) => NullableLong(parent, property) ?? 0;

    private static double Double(JsonElement parent, string property) => NullableDouble(parent, property) ?? 0;

    private static bool Bool(JsonElement parent, string property) => NullableBool(parent, property) == true;

    private static bool? NullableBool(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static double? NullableDouble(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? number
            : null;

    private static long? NullableLong(JsonElement parent, string property)
    {
        if (NullableDouble(parent, property) is not { } number)
            return null;
        if (number >= long.MaxValue) return long.MaxValue;
        if (number <= long.MinValue) return long.MinValue;
        return (long)Math.Round(number, MidpointRounding.AwayFromZero);
    }

    private static int? NullableInt(JsonElement parent, string property) =>
        NullableLong(parent, property) is { } value ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : null;

    private static DateTimeOffset? Timestamp(JsonElement parent, string property)
    {
        if (NullableDouble(parent, property) is not { } ms)
            return null;
        const double min = -62135596800000d;
        const double max = 253402300799999d;
        return ms is < min or > max ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return System.Array.Empty<string>();
        return array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToArray();
    }

    /// <summary>Reads a fixed-length numeric vector, padding or truncating to <paramref name="length"/>.</summary>
    private static IReadOnlyList<long> Numbers(JsonElement parent, string property, int length)
    {
        var values = new long[length];
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (index >= length) break;
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number))
                    values[index] = (long)Math.Round(Math.Max(0, number), MidpointRounding.AwayFromZero);
                index++;
            }
        }
        return values;
    }

    private static IReadOnlyList<T> Array<T>(JsonElement parent, string property, Func<JsonElement, T?> map)
        where T : class
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return System.Array.Empty<T>();
        var result = new List<T>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && map(item) is { } mapped)
                result.Add(mapped);
        }
        return result;
    }
}
