using System.Globalization;

namespace OpenClaw.Shared.Usage;

public enum UsageTimeZoneMode
{
    Local,
    Utc,
}

public enum UsageScope
{
    /// <summary>Only the active session id for each logical session.</summary>
    Instance,
    /// <summary>Roll up rotated transcript-backed session ids.</summary>
    Family,
}

public enum UsageSort
{
    Recent,
    Tokens,
    Cost,
    Messages,
    Errors,
}

/// <summary>
/// <c>sessions.usage</c> request. <see cref="ToParameters"/> mirrors the upstream web client's
/// <c>requestSessionUsage</c> (openclaw/openclaw@5089aa71 ui/src/lib/sessions/usage.ts) so Windows
/// receives the same projection the web Usage page renders.
/// </summary>
public sealed record SessionsUsageQuery
{
    public const int OverviewRowLimit = 50;
    /// <summary>Rows per full read. Gateways without the server overview return at most this many sessions.</summary>
    public const int FullRowLimit = 1000;

    public required string StartDate { get; init; }
    public required string EndDate { get; init; }
    public UsageScope Scope { get; init; } = UsageScope.Instance;
    public UsageTimeZoneMode TimeZone { get; init; } = UsageTimeZoneMode.Local;
    public string? AgentId { get; init; }
    public string? Query { get; init; }
    public int Offset { get; init; }
    public UsageSort? Sort { get; init; }
    public bool? SortDescending { get; init; }

    /// <summary>Reads one session (detail view). Overrides paging and filters.</summary>
    public string? Key { get; init; }
    public bool OverviewProjection { get; init; }
    public int? Limit { get; init; }

    /// <summary>True when the query uses parameters only Gateways with the server-side overview accept.</summary>
    public bool UsesServerOverview =>
        Key is null && (OverviewProjection || !string.IsNullOrWhiteSpace(Query) || Offset > 0
            || Sort is not null || SortDescending is not null);

    /// <summary>
    /// The same range, scope and time zone as a request released Gateways (2026.10.7 through 2026.10.9) accept:
    /// full rows and aggregates, without the overview projection, query, paging or sort.
    /// </summary>
    public SessionsUsageQuery WithoutServerOverview() => this with
    {
        OverviewProjection = false,
        Query = null,
        Offset = 0,
        Sort = null,
        SortDescending = null,
        Limit = Key is null ? FullRowLimit : Limit,
    };

    public Dictionary<string, object?> ToParameters(TimeZoneInfo? localZone = null, DateTimeOffset? now = null)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["startDate"] = StartDate,
            ["endDate"] = EndDate,
        };

        if (!string.IsNullOrWhiteSpace(AgentId))
            parameters["agentId"] = AgentId;
        else if (Key is null)
            parameters["agentScope"] = "all";

        if (TimeZone == UsageTimeZoneMode.Utc)
        {
            parameters["mode"] = "utc";
        }
        else
        {
            var zone = localZone ?? TimeZoneInfo.Local;
            parameters["mode"] = "specific";
            if (TryGetIanaId(zone) is { } iana)
                parameters["timeZone"] = iana;
            parameters["utcOffset"] = FormatUtcOffset(zone.GetUtcOffset(now ?? DateTimeOffset.UtcNow));
        }

        parameters["groupBy"] = Scope == UsageScope.Family ? "family" : "instance";

        if (Key is not null)
        {
            parameters["key"] = Key;
            parameters["limit"] = 1;
        }
        else
        {
            parameters["limit"] = Limit ?? (OverviewProjection ? OverviewRowLimit : FullRowLimit);
            if (OverviewProjection) parameters["projection"] = "overview";
            if (!string.IsNullOrWhiteSpace(Query)) parameters["query"] = Query;
            if (Offset > 0) parameters["offset"] = Offset;
            if (Sort is { } sort) parameters["sort"] = sort.ToString().ToLowerInvariant();
            if (SortDescending is { } descending) parameters["sortDirection"] = descending ? "desc" : "asc";
        }

        return parameters;
    }

    /// <summary>Formats an offset as the Gateway's <c>UTC+5:30</c> / <c>UTC-4</c> form.</summary>
    public static string FormatUtcOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        var hours = (int)absolute.TotalHours;
        return absolute.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{hours}:{absolute.Minutes:00}");
    }

    private static string? TryGetIanaId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
            return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }
}

public enum UsageReadStatus
{
    Ok,
    /// <summary>The Gateway does not implement the method (older Gateway).</summary>
    Unsupported,
    /// <summary>The connection lacks the required scope (operator.read).</summary>
    Forbidden,
    /// <summary>No authenticated connection was available.</summary>
    Disconnected,
    Failed,
}

/// <summary>Typed outcome of a usage read. <see cref="Error"/> is Gateway text and is never usage content.</summary>
public sealed record UsageReadResult<T>(UsageReadStatus Status, T? Value, string? Error = null)
    where T : class
{
    public bool IsOk => Status == UsageReadStatus.Ok && Value is not null;

    public static UsageReadResult<T> Ok(T value) => new(UsageReadStatus.Ok, value);
    public static UsageReadResult<T> Unsupported() => new(UsageReadStatus.Unsupported, null);
    public static UsageReadResult<T> Forbidden(string? error) => new(UsageReadStatus.Forbidden, null, error);
    public static UsageReadResult<T> Disconnected() => new(UsageReadStatus.Disconnected, null);
    public static UsageReadResult<T> Failed(string? error) => new(UsageReadStatus.Failed, null, error);
}
