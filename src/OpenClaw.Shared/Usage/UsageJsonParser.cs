using System.Text.Json;

namespace OpenClaw.Shared.Usage;

/// <summary>
/// Projection of Gateway usage JSON into the usage records in this namespace.
/// Missing, null or mistyped counters remain unavailable so a partial or newer payload still renders.
/// Unknown fields are ignored. Provider reads reject a missing or malformed provider collection.
/// </summary>
public static class UsageJsonParser
{
    public static SessionsUsageReport ParseSessionsUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new SessionsUsageReport { SessionsKnown = false };

        return new SessionsUsageReport
        {
            SessionsKnown = payload.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array,
            UpdatedAt = Timestamp(payload, "updatedAt"),
            StartDate = Str(payload, "startDate") ?? "",
            EndDate = Str(payload, "endDate") ?? "",
            Sessions = Array(payload, "sessions", ParseRow),
            Totals = Totals(Child(payload, "totals")),
            Aggregates = Aggregates(Child(payload, "aggregates")),
            CacheStatus = CacheStatus(Child(payload, "cacheStatus")),
            Overview = Overview(Child(payload, "overview")),
            Partial = Partial(Child(payload, "partial")),
        };
    }

    public static ProviderUsageSummary ParseProviderUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Provider usage response has no valid provider collection.");

        return new ProviderUsageSummary(
            Timestamp(payload, "updatedAt"),
            Array(payload, "providers", Provider),
            Bool(payload, "refreshing"));
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
            Channel = Str(row, "channel"),
            ModelOverride = Str(row, "modelOverride"),
            ProviderOverride = Str(row, "providerOverride"),
            ModelProvider = Str(row, "modelProvider"),
            Model = Str(row, "model"),
            Usage = usage is { } u ? SessionSummary(u) : null,
            Computing = Bool(row, "computing"),
        };
    }

    private static SessionUsageSummary SessionSummary(JsonElement usage) => new()
    {
        Totals = Totals(usage),
        FirstActivity = Timestamp(usage, "firstActivity"),
        LastActivity = Timestamp(usage, "lastActivity"),
        DurationMs = NullableLong(usage, "durationMs"),
        MessageCounts = Child(usage, "messageCounts") is { } counts ? MessageCounts(counts) : null,
        ToolUsage = Child(usage, "toolUsage") is { } tools ? ToolSummary(tools) : null,
    };

    internal static UsageTotals Totals(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } totals)
            return UsageTotals.Empty;

        return new UsageTotals
        {
            ReportedFields = totals.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Number
                && (property.Name is "input" or "output" or "cacheRead" or "cacheWrite" or "totalTokens"
                    ? property.Value.TryGetInt64(out var count) && count >= 0
                    : property.Value.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0))
                .Select(property => property.Name).ToHashSet(StringComparer.Ordinal),
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
            SessionCount = NullableInt(aggregates, "sessionCount") is >= 0 and var count ? count : null,
            Messages = Child(aggregates, "messages") is { } messages ? MessageCounts(messages) : UsageMessageCounts.Empty,
            Tools = Child(aggregates, "tools") is { } tools ? ToolSummary(tools) : UsageToolSummary.Empty,
            ByModel = Array(aggregates, "byModel", ModelBreakdown),
            ByProvider = Array(aggregates, "byProvider", ModelBreakdown),
            ByAgent = Array(aggregates, "byAgent", item => Named(item, "agentId")),
            CostDaily = Array(aggregates, "costDaily", DailyTotals),
        };
    }

    private static UsageOverview? Overview(JsonElement? element)
    {
        if (element is not { } overview || NullableInt(overview, "total") is not >= 0)
            return null;

        var filterOptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (Child(overview, "filterOptions") is { } options)
        {
            foreach (var property in options.EnumerateObject())
                filterOptions[property.Name] = Strings(options, property.Name);
        }

        return new UsageOverview
        {
            Report = Report(Child(overview, "usageReport")),
            Total = JsonReadHelpers.GetInt(overview, "total"),
            UnfilteredSessionCount = JsonReadHelpers.GetInt(overview, "unfilteredSessionCount"),
            Offset = JsonReadHelpers.GetInt(overview, "offset"),
            Limit = JsonReadHelpers.GetInt(overview, "limit"),
            QueryWarnings = Strings(overview, "queryWarnings"),
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
        JsonReadHelpers.GetInt(counts, "toolResults"), JsonReadHelpers.GetInt(counts, "errors"))
        {
            HasData = new[] { "total", "user", "assistant", "toolCalls", "toolResults", "errors" }
                .All(field => NullableInt(counts, field) is >= 0),
        };

    private static UsageToolSummary ToolSummary(JsonElement tools) => new(
        JsonReadHelpers.GetInt(tools, "totalCalls"),
        JsonReadHelpers.GetInt(tools, "uniqueTools"))
        {
            HasData = NullableInt(tools, "totalCalls") is >= 0 && NullableInt(tools, "uniqueTools") is >= 0,
        };

    private static UsageModelBreakdown? ModelBreakdown(JsonElement item) => new(
        Str(item, "provider"), Str(item, "model"), JsonReadHelpers.GetInt(item, "count"), Totals(Child(item, "totals")));

    private static UsageNamedTotals? Named(JsonElement item, string nameProperty) =>
        Str(item, nameProperty) is { Length: > 0 } name ? new UsageNamedTotals(name, Totals(Child(item, "totals"))) : null;

    private static ProviderUsageSnapshot? Provider(JsonElement item)
    {
        var provider = Str(item, "provider");
        if (string.IsNullOrWhiteSpace(provider))
            return null;
        return new ProviderUsageSnapshot
        {
            Provider = provider,
            DisplayName = Str(item, "displayName") is { Length: > 0 } name ? name : provider,
            Windows = Array(item, "windows", w => Str(w, "label") is { } label && NullableDouble(w, "usedPercent") is >= 0
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
            "balance" or "spend" when NullableDouble(item, "amount") is { } amount => new ProviderUsageBilling(type, Str(item, "label"), amount, null, unit,
                Timestamp(item, "resetAt")),
            "budget" when NullableDouble(item, "used") is >= 0 && NullableDouble(item, "limit") is >= 0 =>
                new ProviderUsageBilling(type, Str(item, "label"), Double(item, "used"), Double(item, "limit"), unit,
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
            Array(history, "daily", d => Str(d, "date") is { } date && NullableDouble(d, "amount") is { } amount
                ? new ProviderUsageCostDay(date, amount) : null));
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
