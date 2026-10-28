using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Cron;

public enum CronEnabledFilter { All, Enabled, Disabled }

public enum CronSortBy { NextRunAtMs, UpdatedAtMs, Name }

/// <summary>One page of the agent-scoped automation inventory (<c>cron.list</c>).</summary>
public sealed record CronListQuery
{
    /// <summary>Owning agent. Pages opened from an agent route must always set it.</summary>
    public string? AgentId { get; init; }
    public string Query { get; init; } = "";
    public CronEnabledFilter Enabled { get; init; } = CronEnabledFilter.All;
    /// <summary>"all", "at", "every", "cron", "on-exit", "stream" or "event".</summary>
    public string ScheduleKind { get; init; } = "all";
    /// <summary>"all", "ok", "error", "skipped" or "unknown".</summary>
    public string LastRunStatus { get; init; } = "all";
    /// <summary>"all", "conditional" or "unconditional".</summary>
    public string Trigger { get; init; } = "all";
    public CronSortBy SortBy { get; init; } = CronSortBy.NextRunAtMs;
    public bool Descending { get; init; }
    public int Limit { get; init; } = CronProtocol.DefaultPageSize;
    public int Offset { get; init; }

    public bool HasAdvancedFilters => ScheduleKind != "all" || LastRunStatus != "all" || Trigger != "all";

    public JsonObject ToParameters()
    {
        var p = new JsonObject();
        if (!string.IsNullOrWhiteSpace(AgentId)) p["agentId"] = AgentId.Trim();
        p["includeDisabled"] = Enabled == CronEnabledFilter.All;
        p["includeDeliveryPreviews"] = false;
        p["limit"] = Math.Clamp(Limit, 1, 200);
        p["offset"] = Math.Max(0, Offset);
        if (Query.Trim().Length > 0) p["query"] = Query.Trim();
        p["enabled"] = Enabled switch { CronEnabledFilter.Enabled => "enabled", CronEnabledFilter.Disabled => "disabled", _ => "all" };
        if (HasAdvancedFilters)
        {
            p["scheduleKind"] = ScheduleKind;
            p["lastRunStatus"] = LastRunStatus;
            p["trigger"] = Trigger;
        }
        p["sortBy"] = SortBy switch { CronSortBy.UpdatedAtMs => "updatedAtMs", CronSortBy.Name => "name", _ => "nextRunAtMs" };
        p["sortDir"] = Descending ? "desc" : "asc";
        return p;
    }
}

/// <summary>Run history for one job or for every job of an agent (<c>cron.runs</c>).</summary>
public sealed record CronRunsQuery
{
    public string? JobId { get; init; }
    /// <summary>Applied only to all-jobs history; an exact job owns its own history.</summary>
    public string? AgentId { get; init; }
    public string? RunId { get; init; }
    public IReadOnlyList<string> Statuses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DeliveryStatuses { get; init; } = Array.Empty<string>();
    public string Query { get; init; } = "";
    public bool OldestFirst { get; init; }
    public int Limit { get; init; } = CronProtocol.DefaultPageSize;
    public int Offset { get; init; }

    public bool IsJobScope => !string.IsNullOrWhiteSpace(JobId);
    public bool HasFilters => Statuses.Count > 0 || DeliveryStatuses.Count > 0 || Query.Trim().Length > 0;

    public JsonObject ToParameters()
    {
        var p = new JsonObject { ["view"] = "summary", ["scope"] = IsJobScope ? "job" : "all" };
        if (IsJobScope) p["id"] = JobId;
        else if (!string.IsNullOrWhiteSpace(AgentId)) p["agentId"] = AgentId.Trim();
        if (!string.IsNullOrWhiteSpace(RunId)) p["runId"] = RunId;
        p["limit"] = Math.Clamp(Limit, 1, 200);
        p["offset"] = Math.Max(0, Offset);
        if (Statuses.Count > 0) p["statuses"] = new JsonArray(Statuses.Select(s => (JsonNode?)s).ToArray());
        if (DeliveryStatuses.Count > 0) p["deliveryStatuses"] = new JsonArray(DeliveryStatuses.Select(s => (JsonNode?)s).ToArray());
        if (Query.Trim().Length > 0) p["query"] = Query.Trim();
        p["sortDir"] = OldestFirst ? "asc" : "desc";
        return p;
    }
}

/// <summary>Run modes accepted by <c>cron.run</c>.</summary>
public enum CronRunMode { Force, Due }
