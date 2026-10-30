using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public enum UsageAnalyticsMode
{
    Populated,
    Empty,
    Partial,
    /// <summary>A Gateway older than <c>sessions.usage</c>: the method is unknown.</summary>
    Legacy,
    /// <summary>
    /// Released Gateway 2026.10.7: <c>sessions.usage</c> exists but rejects the server-side overview
    /// parameters (<c>projection</c>, <c>query</c>, selections, paging and sort) and never returns <c>overview</c>.
    /// </summary>
    Released,
}

/// <summary>
/// Deterministic synthetic <c>sessions.usage*</c> and <c>usage.status</c> replies. Rows are anchored
/// to the requested date range so any preset renders populated charts. Shapes follow
/// openclaw/openclaw@5089aa71 src/shared/usage-types.ts. This is a browse fixture: it applies a
/// small, documented subset of the Gateway's server-side filtering and is not a usage engine.
/// </summary>
public sealed class UsageAnalyticsFixture
{
    public const string Name = "usage-analytics";
    public const int MaxFixtureDays = 180;

    private static readonly string[] SessionsUsageParameters =
    [
        "key", "agentId", "agentScope", "creatorKey", "startDate", "endDate", "mode", "range", "groupBy",
        "includeHistorical", "utcOffset", "timeZone", "limit", "projection", "offset", "query", "selectedDays",
        "selectedHours", "selectedSessions", "recentKeys", "sort", "sortDirection", "includeContextWeight",
    ];

    // SessionsUsageParamsSchema in the published openclaw@2026.10.7 package (closed object).
    private static readonly string[] ReleasedSessionsUsageParameters =
    [
        "key", "agentId", "agentScope", "creatorKey", "startDate", "endDate", "mode", "range", "groupBy",
        "includeHistorical", "utcOffset", "timeZone", "limit", "includeContextWeight",
    ];

    private readonly object _sync = new();
    private UsageAnalyticsMode _mode;

    public UsageAnalyticsFixture(bool richProviders = false) => RichProviders = richProviders;

    /// <summary>When true, <c>usage.status</c> returns synthetic provider plans and quotas.</summary>
    public bool RichProviders { get; }

    /// <summary>Parameters <see cref="UsageAnalyticsMode.Released"/> accepts. Narrow it to model an older schema.</summary>
    public IReadOnlyCollection<string> ReleasedParameters { get; set; } = ReleasedSessionsUsageParameters;

    public UsageAnalyticsMode Mode
    {
        get { lock (_sync) return _mode; }
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_sync) _mode = value;
        }
    }

    internal static bool Handles(string method) =>
        method is "sessions.usage" or "sessions.usage.timeseries" or "sessions.usage.logs";

    internal object Respond(string method, JsonElement parameters)
    {
        var mode = Mode;
        if (mode == UsageAnalyticsMode.Legacy)
            throw new FixtureRequestException("INVALID_REQUEST", $"unknown method: {method}");
        return method switch
        {
            "sessions.usage" => SessionsUsage(parameters, mode),
            "sessions.usage.timeseries" => Timeseries(parameters, mode),
            "sessions.usage.logs" => Logs(parameters, mode),
            _ => throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown fixture Gateway method.", unexpected: true),
        };
    }

    // ── synthetic population ──

    private sealed record Spec(
        string Key, string Label, string Agent, string Channel, string Provider, string Model,
        string CreatorKey, string CreatorType, string CreatorLabel, string[] Tools,
        double Weight, int ActivityPercent, int Hour, bool Errors = false);

    private static readonly Spec[] Specs =
    [
        new("agent:main:webchat:main", "Main conversation", "main", "webchat", "anthropic", "claude-sonnet-4.5",
            "profile:ada", "human", "Ada Lovelace", ["read", "edit", "exec"], 1.6, 75, 10),
        new("agent:research:discord:guild:42", "Research digest", "research", "discord", "google", "gemini-2.5-pro",
            "agent:research", "agent", "Research agent", ["web_search", "web_fetch"], 1.2, 55, 14),
        new("agent:main:telegram:dm:ada", "Trip planning with Ada", "main", "telegram", "openai", "gpt-5.1",
            "profile:ada", "human", "Ada Lovelace", ["web_search", "read", "calendar"], 1.0, 45, 20),
        new("agent:ops:slack:C01", "Incident review", "ops", "slack", "anthropic", "claude-opus-4.1",
            "profile:grace", "human", "Grace Hopper", ["exec", "read", "browser"], 0.9, 30, 16, Errors: true),
        new("agent:main:discord:dm:linus", "Code review help", "main", "discord", "anthropic", "claude-sonnet-4.5",
            "profile:linus", "human", "Linus", ["read", "edit", "exec"], 0.8, 40, 11),
        new("agent:research:webchat:papers", "Paper reading list", "research", "webchat", "anthropic", "claude-sonnet-4.5",
            "profile:ada", "human", "Ada Lovelace", ["web_fetch", "read"], 0.7, 35, 21),
        new("agent:ops:webchat:deploy", "Deploy runbook", "ops", "webchat", "openai", "gpt-5.1",
            "profile:grace", "human", "Grace Hopper", ["exec", "read"], 0.6, 25, 9, Errors: true),
        new("agent:research:slack:C02", "Market scan", "research", "slack", "google", "gemini-2.5-flash",
            "agent:research", "agent", "Research agent", ["web_search"], 0.5, 50, 8),
        new("agent:main:cron:daily-summary", "Daily summary", "main", "cron", "openai", "gpt-5-mini",
            "system", "system", "System", ["read", "message"], 0.4, 95, 6),
        new("agent:main:webchat:voice-notes", "Voice notes", "main", "webchat", "openai", "gpt-realtime",
            "profile:grace", "human", "Grace Hopper", [], 0.3, 20, 19),
        new("agent:main:whatsapp:dm:grace", "Groceries with Grace", "main", "whatsapp", "openai", "gpt-5-mini",
            "profile:grace", "human", "Grace Hopper", ["message"], 0.25, 30, 18),
        new("agent:ops:cron:backup-check", "Backup check", "ops", "cron", "ollama", "llama3.3",
            "system", "system", "System", ["exec"], 0.2, 90, 3),
    ];

    // USD per million tokens: input, output, cache read, cache write.
    private static (double In, double Out, double Read, double Write) Price(string model) => model switch
    {
        "claude-sonnet-4.5" => (3, 15, 0.3, 3.75),
        "claude-opus-4.1" => (15, 75, 1.5, 18.75),
        "gpt-5.1" => (1.25, 10, 0.125, 0),
        "gpt-5-mini" => (0.25, 2, 0.025, 0),
        "gpt-realtime" => (4, 16, 0.4, 0),
        "gemini-2.5-pro" => (1.25, 10, 0.31, 0),
        "gemini-2.5-flash" => (0.3, 2.5, 0.075, 0),
        _ => (0, 0, 0, 0),
    };

    private sealed class Day
    {
        public required string Date { get; init; }
        public long Input, Output, CacheRead, CacheWrite;
        public double InputCost, OutputCost, CacheReadCost, CacheWriteCost;
        public int User, Assistant, ToolCalls, ToolResults, Errors;
        public bool LowerBound;
        public long Tokens => Input + Output + CacheRead + CacheWrite;
        public double Cost => InputCost + OutputCost + CacheReadCost + CacheWriteCost;
    }

    private sealed record Row(Spec Spec, int Index, List<Day> Days, bool Computing);

    private static uint Hash(string a, string b)
    {
        uint h = 2166136261;
        foreach (var c in a + "|" + b) h = (h ^ c) * 16777619;
        return h;
    }

    private static List<Row> Population(DateOnly start, DateOnly end, UsageAnalyticsMode mode)
    {
        if (mode == UsageAnalyticsMode.Empty)
            return [];
        if (end.DayNumber - start.DayNumber >= MaxFixtureDays)
            start = end.AddDays(-(MaxFixtureDays - 1));
        var rows = new List<Row>();
        for (var i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            var days = new List<Day>();
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var h = Hash(spec.Key, iso);
                // Weekends are quieter except for cron jobs.
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var chance = weekend && spec.Channel != "cron" ? spec.ActivityPercent / 3 : spec.ActivityPercent;
                if (h % 100 >= (uint)chance) continue;
                var scale = 0.45 + (h >> 8) % 1000 / 1000.0;
                var tokens = (long)(52_000 * spec.Weight * scale);
                var price = Price(spec.Model);
                var day = new Day
                {
                    Date = iso,
                    Input = tokens * 52 / 100,
                    Output = tokens * 14 / 100,
                    CacheRead = tokens * 29 / 100,
                    CacheWrite = tokens * 5 / 100,
                };
                day.InputCost = day.Input * price.In / 1_000_000;
                day.OutputCost = day.Output * price.Out / 1_000_000;
                day.CacheReadCost = day.CacheRead * price.Read / 1_000_000;
                day.CacheWriteCost = day.CacheWrite * price.Write / 1_000_000;
                day.User = 2 + (int)(tokens / 9_000);
                day.Assistant = day.User;
                day.ToolCalls = spec.Tools.Length == 0 ? 0 : day.User + (int)(h >> 16) % 4;
                day.ToolResults = day.ToolCalls;
                day.Errors = spec.Errors && (h >> 20) % 3 == 0 ? 1 + (int)(h >> 24) % 2 : 0;
                days.Add(day);
            }
            var computing = false;
            if (mode == UsageAnalyticsMode.Partial)
            {
                // The newest day of two busy sessions ended before final usage arrived.
                if (i is 0 or 3 && days.Count > 0) days[^1].LowerBound = true;
                computing = i == Specs.Length - 1;
            }
            if (days.Count > 0)
                rows.Add(new Row(spec, i, days, computing));
        }
        return rows;
    }

    // ── sessions.usage ──

    private object SessionsUsage(JsonElement p, UsageAnalyticsMode mode)
    {
        if (mode == UsageAnalyticsMode.Released)
            return ReleasedSessionsUsage(p);
        GatewayScenario.ValidateProperties(p, SessionsUsageParameters);
        var (start, end) = Range(p);
        var family = Text(p, "groupBy") == "family";
        var key = Text(p, "key");
        var includeContext = p.TryGetProperty("includeContextWeight", out var ic) && ic.ValueKind == JsonValueKind.True;
        var rows = Population(start, end, mode);

        var creator = Text(p, "creatorKey");
        var creatorOptions = rows.Select(r => r.Spec).DistinctBy(s => s.CreatorKey)
            .Select(s => Creator(s)).ToArray();
        if (creator is not null) rows = rows.Where(r => r.Spec.CreatorKey == creator).ToList();

        if (key is not null)
        {
            var single = rows.Where(r => r.Spec.Key == key).ToList();
            return Report(start, end, single, single, rows, family, includeContext, mode, creatorOptions, overview: null);
        }

        var overviewRequested = Text(p, "projection") == "overview";
        var warnings = new List<string>();
        var calendarRows = ApplyQuery(rows, Text(p, "query"), warnings);
        var hours = Ints(p, "selectedHours");
        if (hours.Count > 0) calendarRows = calendarRows.Where(r => hours.Contains(r.Spec.Hour)).ToList();
        var days = Strings(p, "selectedDays");
        var population = days.Count == 0
            ? calendarRows
            : calendarRows
                .Select(r => r with { Days = r.Days.Where(d => days.Contains(d.Date)).ToList() })
                .Where(r => r.Days.Count > 0).ToList();

        var recent = p.TryGetProperty("recentKeys", out var rk) && rk.ValueKind == JsonValueKind.Array
            ? Strings(p, "recentKeys") : null;
        var table = recent is null ? population : population.Where(r => recent.Contains(r.Spec.Key)).ToList();
        table = Sort(table, Text(p, "sort") ?? "cost", Text(p, "sortDirection") != "asc");
        var offset = p.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov) ? Math.Max(0, ov) : 0;
        var limit = p.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 1000) : 50;
        if (overviewRequested) limit = Math.Min(limit, SessionsUsageQueryLimit);
        var page = table.Skip(offset).Take(limit).ToList();

        JsonObject? overview = null;
        if (overviewRequested)
        {
            var selected = Strings(p, "selectedSessions");
            var selectedRows = selected.Select(k => population.FirstOrDefault(r => r.Spec.Key == k))
                .OfType<Row>().ToList();
            overview = Overview(population, rows, table, selectedRows, offset, limit, warnings, family, mode);
        }
        return Report(start, end, page, population, calendarRows, family, includeContext, mode, creatorOptions, overview);
    }

    private const int SessionsUsageQueryLimit = 50;

    /// <summary>Full rows and aggregates with no server-side overview, like the released Gateway.</summary>
    private object ReleasedSessionsUsage(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in p.EnumerateObject())
            {
                if (!ReleasedParameters.Contains(property.Name, StringComparer.Ordinal))
                {
                    // Same wording as the released Gateway's schema validation error.
                    throw new FixtureRequestException("INVALID_REQUEST",
                        $"invalid sessions.usage params: at root: unexpected property '{property.Name}'");
                }
            }
        }
        var (start, end) = Range(p);
        var family = Text(p, "groupBy") == "family";
        var key = Text(p, "key");
        var includeContext = p.TryGetProperty("includeContextWeight", out var ic) && ic.ValueKind == JsonValueKind.True;
        var rows = Population(start, end, UsageAnalyticsMode.Populated);
        var creatorOptions = rows.Select(r => r.Spec).DistinctBy(s => s.CreatorKey).Select(s => Creator(s)).ToArray();
        var creator = Text(p, "creatorKey");
        if (creator is not null) rows = rows.Where(r => r.Spec.CreatorKey == creator).ToList();
        if (key is not null) rows = rows.Where(r => r.Spec.Key == key).ToList();
        var limit = p.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 1000) : 50;
        var page = Sort(rows, "recent", descending: true).Take(limit).ToList();
        return Report(start, end, page, rows, rows, family, includeContext, UsageAnalyticsMode.Populated, creatorOptions, overview: null);
    }

    private static JsonObject Report(
        DateOnly start, DateOnly end, List<Row> page, List<Row> population, List<Row> calendar, bool family,
        bool includeContext, UsageAnalyticsMode mode, JsonNode[] creatorOptions, JsonObject? overview)
    {
        var result = new JsonObject
        {
            ["updatedAt"] = GatewayScenarioClock.UpdatedAt,
            ["startDate"] = Iso(start),
            ["endDate"] = Iso(end),
            ["sessions"] = new JsonArray(page.Select(r => SessionRow(r, family, includeContext)).ToArray<JsonNode?>()),
            ["totals"] = Totals(population.SelectMany(r => r.Days)),
            ["aggregates"] = Aggregates(population, calendar),
            ["creatorOptions"] = new JsonArray(creatorOptions.Select(c => c.DeepClone()).ToArray()),
            ["cacheStatus"] = mode == UsageAnalyticsMode.Partial
                ? new JsonObject { ["status"] = "partial", ["cachedFiles"] = 9, ["pendingFiles"] = 3, ["staleFiles"] = 0 }
                : new JsonObject { ["status"] = "fresh", ["cachedFiles"] = population.Count, ["pendingFiles"] = 0, ["staleFiles"] = 0 },
        };
        if (overview is not null) result["overview"] = overview;
        if (mode == UsageAnalyticsMode.Partial)
            result["partial"] = new JsonObject
            {
                ["reason"] = "time-budget",
                ["message"] = "Usage scanning reached its time budget. Totals include the transcripts read so far.",
            };
        return result;
    }

    private static JsonObject Overview(
        List<Row> population, List<Row> unfiltered, List<Row> table, List<Row> selected, int offset, int limit,
        List<string> warnings, bool family, UsageAnalyticsMode mode)
    {
        var hourTokens = new long[24];
        var hourlyMessages = new long[24];
        var hourlyErrors = new long[24];
        var weekdayTokens = new long[7];
        foreach (var row in population)
        {
            foreach (var day in row.Days)
            {
                // Spread each day's tokens over a three-hour working window.
                for (var offsetHour = 0; offsetHour < 3; offsetHour++)
                {
                    var hour = (row.Spec.Hour + offsetHour) % 24;
                    var share = offsetHour == 0 ? 0.5 : 0.25;
                    hourTokens[hour] += (long)(day.Tokens * share);
                    hourlyMessages[hour] += (long)((day.User + day.Assistant) * share);
                    hourlyErrors[hour] += offsetHour == 0 ? day.Errors : 0;
                }
                weekdayTokens[(int)DateOnly.ParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek] += day.Tokens;
            }
        }
        var durations = population.Where(r => r.Days.Count > 0).Select(Duration).ToArray();
        return new JsonObject
        {
            ["usageReport"] = Report(population.SelectMany(r => r.Days)),
            ["total"] = table.Count,
            ["unfilteredSessionCount"] = unfiltered.Count,
            ["selectedSessionCount"] = selected.Count,
            ["selectedRowCount"] = selected.Count,
            ["selectedUsageComplete"] = mode != UsageAnalyticsMode.Partial,
            ["selectedRows"] = new JsonArray(selected.Select(r => SessionRow(r, family, false)).ToArray<JsonNode?>()),
            // Upstream: total counts the visible roster (recent sessions only when asked); the table totals and their
            // session count cover every matching session.
            ["tableSessionCount"] = population.Count,
            ["tableTotals"] = new JsonObject
            {
                ["tokens"] = population.Sum(r => r.Days.Sum(d => d.Tokens)),
                ["cost"] = Math.Round(population.Sum(r => r.Days.Sum(d => d.Cost)), 6),
                ["errors"] = population.Sum(r => r.Days.Sum(d => d.Errors)),
            },
            ["offset"] = offset,
            ["limit"] = limit,
            ["queryWarnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray()),
            ["hourTokens"] = new JsonArray(hourTokens.Select(v => (JsonNode?)v).ToArray()),
            ["weekdayTokens"] = new JsonArray(weekdayTokens.Select(v => (JsonNode?)v).ToArray()),
            ["hasTimelineData"] = population.Count > 0,
            ["durationMs"] = durations.Sum(),
            ["durationCount"] = durations.Length,
            ["hourlyMessages"] = new JsonArray(hourlyMessages.Select(v => (JsonNode?)v).ToArray()),
            ["hourlyErrors"] = new JsonArray(hourlyErrors.Select(v => (JsonNode?)v).ToArray()),
            ["filterOptions"] = new JsonObject
            {
                ["agent"] = Options(unfiltered.Select(r => r.Spec.Agent)),
                ["channel"] = Options(unfiltered.Select(r => r.Spec.Channel)),
                ["provider"] = Options(unfiltered.Select(r => r.Spec.Provider)),
                ["model"] = Options(unfiltered.Select(r => r.Spec.Model)),
                ["tool"] = Options(unfiltered.SelectMany(r => r.Spec.Tools)),
            },
        };
    }

    private static JsonArray Options(IEnumerable<string> values) =>
        new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(v => (JsonNode?)v).ToArray());

    private static JsonObject SessionRow(Row row, bool family, bool includeContext)
    {
        var spec = row.Spec;
        var sessionId = $"00000000-0000-4000-9000-{row.Index + 1:D12}";
        var json = new JsonObject
        {
            ["key"] = spec.Key,
            ["label"] = spec.Label,
            ["sessionId"] = sessionId,
            ["scope"] = family ? "family" : "instance",
            ["updatedAt"] = LastActivity(row),
            ["agentId"] = spec.Agent,
            ["createdActor"] = Actor(spec),
            ["creatorKey"] = spec.CreatorKey,
            ["channel"] = spec.Channel,
            ["chatType"] = spec.Channel == "cron" ? "cron" : "direct",
            ["modelProvider"] = spec.Provider,
            ["model"] = spec.Model,
            ["hasContextWeight"] = true,
        };
        if (family && row.Index % 3 == 0)
        {
            json["sessionFamilyKey"] = spec.Key;
            json["currentSessionId"] = sessionId;
            json["includedSessionIds"] = new JsonArray(sessionId, $"00000000-0000-4000-9100-{row.Index + 1:D12}");
            json["historicalInstanceCount"] = 2;
        }
        if (row.Computing)
        {
            json["usage"] = null;
            json["computing"] = true;
            return json;
        }
        var usage = Totals(row.Days);
        usage["firstActivity"] = FirstActivity(row);
        usage["lastActivity"] = LastActivity(row);
        usage["durationMs"] = Duration(row);
        usage["activityDates"] = new JsonArray(row.Days.Select(d => (JsonNode?)d.Date).ToArray());
        usage["dailyBreakdown"] = new JsonArray(row.Days.Select(d =>
        {
            var daily = Totals([d]);
            daily["date"] = d.Date;
            daily["tokens"] = d.Tokens;
            daily["cost"] = Math.Round(d.Cost, 6);
            return (JsonNode?)daily;
        }).ToArray());
        usage["messageCounts"] = Messages(row.Days);
        usage["toolUsage"] = Tools([row]);
        usage["modelUsage"] = new JsonArray(new JsonObject
        {
            ["provider"] = spec.Provider, ["model"] = spec.Model,
            ["count"] = row.Days.Sum(d => d.Assistant), ["totals"] = Totals(row.Days),
        });
        usage["latency"] = Latency(row.Days.Sum(d => d.Assistant), spec.Weight);
        json["usage"] = usage;
        if (includeContext) json["contextWeight"] = ContextWeight(spec);
        return json;
    }

    private static JsonObject Aggregates(List<Row> population, List<Row> calendar)
    {
        var days = population.SelectMany(r => r.Days).ToList();
        JsonObject Bucket(IEnumerable<Row> rows, string? provider, string? model)
        {
            var list = rows.ToList();
            var bucket = new JsonObject
            {
                ["count"] = list.Sum(r => r.Days.Sum(d => d.Assistant)),
                ["totals"] = Totals(list.SelectMany(r => r.Days)),
            };
            if (provider is not null) bucket["provider"] = provider;
            if (model is not null) bucket["model"] = model;
            return bucket;
        }
        var byModel = population.GroupBy(r => (r.Spec.Provider, r.Spec.Model))
            .Select(g => Bucket(g, g.Key.Provider, g.Key.Model))
            .OrderByDescending(b => b["totals"]!["totalCost"]!.GetValue<double>());
        var byProvider = population.GroupBy(r => r.Spec.Provider)
            .Select(g => Bucket(g, g.Key, null))
            .OrderByDescending(b => b["totals"]!["totalCost"]!.GetValue<double>());
        var byAgent = population.GroupBy(r => r.Spec.Agent).Select(g => new JsonObject
        {
            ["agentId"] = g.Key, ["totals"] = Totals(g.SelectMany(r => r.Days)),
        }).OrderByDescending(b => b["totals"]!["totalCost"]!.GetValue<double>());
        var byChannel = population.GroupBy(r => r.Spec.Channel).Select(g => new JsonObject
        {
            ["channel"] = g.Key, ["totals"] = Totals(g.SelectMany(r => r.Days)),
        }).OrderByDescending(b => b["totals"]!["totalCost"]!.GetValue<double>());
        var byCreator = population.GroupBy(r => r.Spec.CreatorKey).Select(g =>
        {
            var creator = Creator(g.First().Spec).AsObject();
            creator["totals"] = Totals(g.SelectMany(r => r.Days));
            creator["sessionCount"] = g.Count();
            creator["daily"] = DailyTotals(g.SelectMany(r => r.Days));
            creator["sessionActivity"] = new JsonArray(g.Select(r => (JsonNode?)new JsonObject
            {
                ["dates"] = new JsonArray(r.Days.Select(d => (JsonNode?)d.Date).ToArray()),
                ["sessionCount"] = 1,
            }).ToArray());
            return creator;
        }).OrderByDescending(b => b["totals"]!["totalCost"]!.GetValue<double>());
        var calendarDays = calendar.SelectMany(r => r.Days).ToList();
        var modelDaily = calendar
            .SelectMany(r => r.Days.Select(d => (r.Spec.Provider, r.Spec.Model, Day: d)))
            .GroupBy(x => (x.Day.Date, x.Provider, x.Model))
            .OrderBy(g => g.Key.Date, StringComparer.Ordinal)
            .Select(g =>
            {
                var item = new JsonObject
                {
                    ["date"] = g.Key.Date, ["provider"] = g.Key.Provider, ["model"] = g.Key.Model,
                    ["tokens"] = g.Sum(x => x.Day.Tokens), ["cost"] = Math.Round(g.Sum(x => x.Day.Cost), 6),
                    ["count"] = g.Sum(x => x.Day.Assistant),
                };
                if (g.Any(x => x.Day.LowerBound)) item["usageReport"] = Unavailable();
                return (JsonNode?)item;
            });
        var daily = days.GroupBy(d => d.Date).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var item = new JsonObject
            {
                ["date"] = g.Key,
                ["tokens"] = g.Sum(d => d.Tokens),
                ["cost"] = Math.Round(g.Sum(d => d.Cost), 6),
                ["messages"] = g.Sum(d => d.User + d.Assistant),
                ["toolCalls"] = g.Sum(d => d.ToolCalls),
                ["errors"] = g.Sum(d => d.Errors),
            };
            if (g.Any(d => d.LowerBound)) item["usageReport"] = Unavailable();
            return (JsonNode?)item;
        });
        var assistantTurns = days.Sum(d => d.Assistant);
        return new JsonObject
        {
            ["sessionCount"] = population.Count,
            ["longestSessionDurationMs"] = population.Count == 0 ? 0 : population.Max(Duration),
            ["messages"] = Messages(days),
            ["tools"] = Tools(population),
            ["byModel"] = new JsonArray(byModel.ToArray<JsonNode?>()),
            ["byProvider"] = new JsonArray(byProvider.ToArray<JsonNode?>()),
            ["byAgent"] = new JsonArray(byAgent.ToArray<JsonNode?>()),
            ["byChannel"] = new JsonArray(byChannel.ToArray<JsonNode?>()),
            ["byCreator"] = new JsonArray(byCreator.ToArray<JsonNode?>()),
            ["costDaily"] = DailyTotals(calendarDays),
            ["latency"] = Latency(assistantTurns, 1),
            ["modelDaily"] = new JsonArray(modelDaily.ToArray()),
            ["daily"] = new JsonArray(daily.ToArray()),
        };
    }

    private static JsonArray DailyTotals(IEnumerable<Day> days) =>
        new(days.GroupBy(d => d.Date).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var totals = Totals(g);
            totals["date"] = g.Key;
            return (JsonNode?)totals;
        }).ToArray());

    private static JsonObject Totals(IEnumerable<Day> source)
    {
        var days = source.ToList();
        var totals = new JsonObject
        {
            ["input"] = days.Sum(d => d.Input),
            ["output"] = days.Sum(d => d.Output),
            ["cacheRead"] = days.Sum(d => d.CacheRead),
            ["cacheWrite"] = days.Sum(d => d.CacheWrite),
            ["totalTokens"] = days.Sum(d => d.Tokens),
            ["totalCost"] = Math.Round(days.Sum(d => d.Cost), 6),
            ["inputCost"] = Math.Round(days.Sum(d => d.InputCost), 6),
            ["outputCost"] = Math.Round(days.Sum(d => d.OutputCost), 6),
            ["cacheReadCost"] = Math.Round(days.Sum(d => d.CacheReadCost), 6),
            ["cacheWriteCost"] = Math.Round(days.Sum(d => d.CacheWriteCost), 6),
            ["missingCostEntries"] = 0,
        };
        if (days.Any(d => d.LowerBound)) totals["usageReport"] = Unavailable();
        return totals;
    }

    private static JsonObject Report(IEnumerable<Day> days) =>
        days.Any(d => d.LowerBound) ? Unavailable() : new JsonObject { ["state"] = "available" };

    private static JsonObject Unavailable() => new() { ["state"] = "unavailable" };

    private static JsonObject Messages(IEnumerable<Day> source)
    {
        var days = source.ToList();
        return new JsonObject
        {
            ["total"] = days.Sum(d => d.User + d.Assistant),
            ["user"] = days.Sum(d => d.User),
            ["assistant"] = days.Sum(d => d.Assistant),
            ["toolCalls"] = days.Sum(d => d.ToolCalls),
            ["toolResults"] = days.Sum(d => d.ToolResults),
            ["errors"] = days.Sum(d => d.Errors),
        };
    }

    private static JsonObject Tools(IEnumerable<Row> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var calls = row.Days.Sum(d => d.ToolCalls);
            for (var i = 0; i < row.Spec.Tools.Length; i++)
            {
                // Earlier tools in a session's list are used more often.
                var share = calls * (row.Spec.Tools.Length - i) / Enumerable.Range(1, row.Spec.Tools.Length).Sum();
                counts[row.Spec.Tools[i]] = counts.GetValueOrDefault(row.Spec.Tools[i]) + share;
            }
        }
        var tools = counts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        return new JsonObject
        {
            ["totalCalls"] = tools.Sum(t => t.Value),
            ["uniqueTools"] = tools.Count,
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Key, ["count"] = t.Value }).ToArray()),
        };
    }

    private static JsonObject? Latency(int count, double weight) => count == 0
        ? null
        : new JsonObject
        {
            ["count"] = count,
            ["avgMs"] = Math.Round(2_400 + 1_800 * weight),
            ["p95Ms"] = Math.Round(6_500 + 4_000 * weight),
            ["minMs"] = 640,
            ["maxMs"] = Math.Round(14_000 + 6_000 * weight),
        };

    private static JsonNode Creator(Spec spec) => new JsonObject { ["key"] = spec.CreatorKey, ["actor"] = Actor(spec) };

    private static JsonObject Actor(Spec spec)
    {
        var actor = new JsonObject { ["type"] = spec.CreatorType, ["label"] = spec.CreatorLabel };
        if (spec.CreatorType != "system") actor["id"] = spec.CreatorKey.Split(':')[1];
        return actor;
    }

    private static JsonObject ContextWeight(Spec spec) => new()
    {
        ["source"] = "run",
        ["generatedAt"] = GatewayScenarioClock.UpdatedAt,
        ["sessionKey"] = spec.Key,
        ["provider"] = spec.Provider,
        ["model"] = spec.Model,
        ["systemPrompt"] = new JsonObject
        {
            ["chars"] = 38_400, ["projectContextChars"] = 14_200, ["nonProjectContextChars"] = 24_200,
        },
        ["currentTurn"] = new JsonObject { ["promptChars"] = 1_840, ["runtimeContextChars"] = 620 },
        ["injectedWorkspaceFiles"] = new JsonArray(
            ContextFile("AGENTS.md", "/fixture/workspace/AGENTS.md", 6_200, 6_200, false),
            ContextFile("SOUL.md", "/fixture/workspace/SOUL.md", 2_100, 2_100, false),
            ContextFile("TOOLS.md", "/fixture/workspace/TOOLS.md", 9_400, 5_900, true),
            ContextFile("MEMORY.md", "/fixture/workspace/MEMORY.md", 0, 0, false, missing: true)),
        ["skills"] = new JsonObject
        {
            ["promptChars"] = 4_800,
            ["entries"] = new JsonArray(
                new JsonObject { ["name"] = "weather", ["blockChars"] = 1_900 },
                new JsonObject { ["name"] = "calendar", ["blockChars"] = 1_600 },
                new JsonObject { ["name"] = "github", ["blockChars"] = 1_300 }),
        },
        ["tools"] = new JsonObject
        {
            ["listChars"] = 3_100,
            ["schemaChars"] = 11_700,
            ["entries"] = new JsonArray(spec.Tools.Concat(["message"]).Distinct().Select((name, i) => (JsonNode?)new JsonObject
            {
                ["name"] = name, ["source"] = "openclaw", ["summaryChars"] = 420 + i * 60, ["schemaChars"] = 1_900 + i * 340,
            }).ToArray()),
        },
    };

    private static JsonObject ContextFile(string name, string path, long raw, long injected, bool truncated, bool missing = false) =>
        new()
        {
            ["name"] = name, ["path"] = path, ["missing"] = missing, ["rawChars"] = raw,
            ["injectedChars"] = injected, ["truncated"] = truncated,
        };

    private static long Start(Row row, Day day) =>
        new DateTimeOffset(DateOnly.ParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .ToDateTime(new TimeOnly(row.Spec.Hour, 5)), TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static long FirstActivity(Row row) => Start(row, row.Days[0]);

    private static long LastActivity(Row row) => Start(row, row.Days[^1]) + (long)(40 + 80 * row.Spec.Weight) * 60_000;

    private static long Duration(Row row) => row.Days.Count * (long)(40 + 80 * row.Spec.Weight) * 60_000;

    private static List<Row> Sort(List<Row> rows, string sort, bool descending)
    {
        Func<Row, double> key = sort switch
        {
            "recent" => r => LastActivity(r),
            "tokens" => r => r.Days.Sum(d => d.Tokens),
            "messages" => r => r.Days.Sum(d => d.User + d.Assistant),
            "errors" => r => r.Days.Sum(d => d.Errors),
            _ => r => r.Days.Sum(d => d.Cost),
        };
        var ordered = descending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenBy(r => r.Spec.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Supports <c>agent:</c>, <c>channel:</c>, <c>provider:</c>, <c>model:</c>, <c>tool:</c>,
    /// <c>key:</c> (trailing <c>*</c>), <c>has:errors|tools</c>, <c>minTokens:</c> and free text.</summary>
    private static List<Row> ApplyQuery(List<Row> rows, string? query, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(query))
            return rows;
        IEnumerable<Row> result = rows;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = term.IndexOf(':');
            var name = colon > 0 ? term[..colon].ToLowerInvariant() : "";
            var value = colon > 0 ? term[(colon + 1)..] : term;
            result = name switch
            {
                "agent" => result.Where(r => Eq(r.Spec.Agent, value)),
                "channel" => result.Where(r => Eq(r.Spec.Channel, value)),
                "provider" => result.Where(r => Eq(r.Spec.Provider, value)),
                "model" => result.Where(r => Eq(r.Spec.Model, value)),
                "tool" => result.Where(r => r.Spec.Tools.Any(t => Eq(t, value))),
                "key" => value.EndsWith('*')
                    ? result.Where(r => r.Spec.Key.StartsWith(value[..^1], StringComparison.OrdinalIgnoreCase))
                    : result.Where(r => Eq(r.Spec.Key, value)),
                "has" when Eq(value, "errors") => result.Where(r => r.Days.Any(d => d.Errors > 0)),
                "has" when Eq(value, "tools") => result.Where(r => r.Spec.Tools.Length > 0),
                "mintokens" when long.TryParse(value, out var min) => result.Where(r => r.Days.Sum(d => d.Tokens) >= min),
                "" => result.Where(r =>
                    r.Spec.Key.Contains(value, StringComparison.OrdinalIgnoreCase)
                    || r.Spec.Label.Contains(value, StringComparison.OrdinalIgnoreCase)),
                _ => Warn(result, warnings, $"Unsupported filter: {term}"),
            };
        }
        return result.ToList();
    }

    private static IEnumerable<Row> Warn(IEnumerable<Row> rows, List<string> warnings, string warning)
    {
        warnings.Add(warning);
        return rows;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── sessions.usage.timeseries / logs ──

    private object Timeseries(JsonElement p, UsageAnalyticsMode mode)
    {
        GatewayScenario.ValidateProperties(p, "key", "agentId");
        var row = FindRow(GatewayScenario.RequiredString(p, "key"), mode);
        var points = new JsonArray();
        long cumulativeTokens = 0;
        double cumulativeCost = 0;
        foreach (var day in row.Days.TakeLast(25))
        {
            // Four turns per active day, front-loaded like a real conversation.
            int[] shares = [40, 25, 20, 15];
            for (var turn = 0; turn < shares.Length; turn++)
            {
                var share = shares[turn] / 100.0;
                var input = (long)(day.Input * share);
                var output = (long)(day.Output * share);
                var read = (long)(day.CacheRead * share);
                var write = (long)(day.CacheWrite * share);
                var total = input + output + read + write;
                var cost = day.Cost * share;
                cumulativeTokens += total;
                cumulativeCost += cost;
                points.Add(new JsonObject
                {
                    ["timestamp"] = Start(row, day) + turn * 7 * 60_000,
                    ["input"] = input, ["output"] = output, ["cacheRead"] = read, ["cacheWrite"] = write,
                    ["totalTokens"] = total, ["cost"] = Math.Round(cost, 6),
                    ["cumulativeTokens"] = cumulativeTokens, ["cumulativeCost"] = Math.Round(cumulativeCost, 6),
                });
            }
        }
        var result = new JsonObject
        {
            ["sessionId"] = $"00000000-0000-4000-9000-{row.Index + 1:D12}",
            ["points"] = points,
        };
        if (row.Days.Any(d => d.LowerBound)) result["usageReport"] = Unavailable();
        return result;
    }

    private object Logs(JsonElement p, UsageAnalyticsMode mode)
    {
        GatewayScenario.ValidateProperties(p, "key", "agentId", "limit");
        var row = FindRow(GatewayScenario.RequiredString(p, "key"), mode);
        var limit = p.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 1000) : 200;
        var logs = new JsonArray();
        foreach (var day in row.Days.TakeLast(6))
        {
            var at = Start(row, day);
            var tool = row.Spec.Tools.FirstOrDefault();
            logs.Add(Log(at, "user", $"Synthetic request for {row.Spec.Label.ToLowerInvariant()} on {day.Date}.", day.Input / 4, null));
            if (tool is not null)
            {
                logs.Add(Log(at + 30_000, "assistant", $"Checking the fixture data first.\n[Tool: {tool}]", day.Output / 5, day.Cost / 5));
                logs.Add(Log(at + 45_000, "toolResult", $"[Tool: {tool}]\n[Tool Result]\nSynthetic result. No command executed.", null, null));
            }
            logs.Add(Log(at + 90_000, "assistant",
                $"Here is the synthetic summary for {day.Date}. All names and values are fictional.", day.Output / 3, day.Cost / 3));
        }
        return new JsonObject { ["logs"] = new JsonArray(logs.TakeLast(limit).Select(n => n!.DeepClone()).ToArray()) };
    }

    private static JsonObject Log(long timestamp, string role, string content, long? tokens, double? cost)
    {
        var log = new JsonObject { ["timestamp"] = timestamp, ["role"] = role, ["content"] = content };
        if (tokens is { } t) log["tokens"] = t;
        if (cost is { } c) log["cost"] = Math.Round(c, 6);
        return log;
    }

    private static Row FindRow(string key, UsageAnalyticsMode mode)
    {
        var end = DateOnly.FromDateTime(DateTime.UtcNow);
        return Population(end.AddDays(-(MaxFixtureDays - 1)), end, mode).FirstOrDefault(r => r.Spec.Key == key)
            ?? throw new FixtureRequestException("INVALID_REQUEST", $"No transcript found for session: {key}");
    }

    // ── usage.status ──

    internal static object ProviderUsage() => new JsonObject
    {
        ["updatedAt"] = GatewayScenarioClock.UpdatedAt,
        ["providers"] = new JsonArray(
            new JsonObject
            {
                ["provider"] = "anthropic", ["displayName"] = "Claude", ["plan"] = "Max 5x",
                ["accountEmail"] = "ada@example.test",
                ["windows"] = new JsonArray(
                    Window("5h", null, 42, 2.5), Window("Week", "All models", 61, 3 * 24), Window("Week", "Opus", 18, 3 * 24)),
            },
            new JsonObject
            {
                ["provider"] = "openai", ["displayName"] = "OpenAI", ["plan"] = "Pay as you go",
                ["windows"] = new JsonArray(),
                ["billing"] = new JsonArray(
                    new JsonObject { ["type"] = "balance", ["label"] = "Credit balance", ["amount"] = 37.42, ["unit"] = "USD" },
                    new JsonObject { ["type"] = "budget", ["label"] = "Monthly budget", ["used"] = 62.18, ["limit"] = 150, ["unit"] = "USD", ["period"] = "month" }),
            },
            new JsonObject
            {
                ["provider"] = "google", ["displayName"] = "Gemini", ["plan"] = "Free tier",
                ["windows"] = new JsonArray(Window("Daily requests", null, 88, 9)),
            },
            new JsonObject
            {
                ["provider"] = "zai", ["displayName"] = "Z.ai", ["windows"] = new JsonArray(),
                ["error"] = "Usage endpoint returned 401. Sign in again to refresh plan usage.",
            }),
    };

    private static JsonObject Window(string label, string? group, double used, double resetHours)
    {
        var window = new JsonObject
        {
            ["label"] = label, ["usedPercent"] = used,
            ["resetAt"] = GatewayScenarioClock.UpdatedAt + (long)(resetHours * 3_600_000),
        };
        if (group is not null) window["groupLabel"] = group;
        return window;
    }

    // ── parameter helpers ──

    private static (DateOnly Start, DateOnly End) Range(JsonElement p)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = Date(p, "startDate");
        var end = Date(p, "endDate");
        if ((start is null) != (end is null))
            throw new FixtureRequestException("INVALID_REQUEST", "startDate and endDate must be provided together");
        var resolvedEnd = end ?? today;
        var resolvedStart = start ?? resolvedEnd.AddDays(-29);
        if (resolvedStart > resolvedEnd)
            throw new FixtureRequestException("INVALID_REQUEST", "startDate must not be after endDate");
        return (resolvedStart, resolvedEnd);
    }

    private static DateOnly? Date(JsonElement p, string name)
    {
        if (Text(p, name) is not { } text)
            return null;
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new FixtureRequestException("INVALID_REQUEST", $"invalid {name}");
    }

    private static string? Text(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static List<string> Strings(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];

    private static List<int> Ints(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.TryGetInt32(out _)).Select(v => v.GetInt32()).ToList()
            : [];

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Fixed report timestamp so fixture replies stay deterministic between requests.</summary>
internal static class GatewayScenarioClock
{
    public static readonly long UpdatedAt =
        new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero).ToUnixTimeMilliseconds();
}
