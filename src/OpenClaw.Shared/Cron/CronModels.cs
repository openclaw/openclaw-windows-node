using System.Text.Json;

namespace OpenClaw.Shared.Cron;

/// <summary>Schedule kinds understood by the Gateway. Unknown kinds round-trip through <see cref="CronSchedule.Raw"/>.</summary>
public enum CronScheduleKind { At, Every, Cron, Event, OnExit, Stream, Unknown }

public enum CronPayloadKind { SystemEvent, AgentTurn, Command, Script, Unknown }

public sealed record CronSchedule(
    CronScheduleKind Kind,
    string RawKind,
    string? At = null,
    long? EveryMs = null,
    long? AnchorMs = null,
    string? Expr = null,
    string? Tz = null,
    long? StaggerMs = null,
    string? EventSource = null,
    string? EventServer = null,
    string? EventName = null,
    string? EventArgumentsJson = null,
    string? Command = null,
    string? Cwd = null)
{
    /// <summary>The original wire value; preserved so read-only kinds survive unrelated edits.</summary>
    public JsonElement? Raw { get; init; }

    public bool IsMcpEvent => Kind == CronScheduleKind.Event && EventSource == CronProtocol.McpEventsSource;

    /// <summary>
    /// The native editor authors only one-time, interval and calendar schedules. Event, process
    /// and unknown schedules stay readable here, are preserved on save and are changed on the web.
    /// </summary>
    public bool IsWebManaged => Kind is not (CronScheduleKind.At or CronScheduleKind.Every or CronScheduleKind.Cron);
}

public sealed record CronPayload(
    CronPayloadKind Kind,
    string RawKind,
    string Text,
    string? Model = null,
    string? Thinking = null,
    double? TimeoutSeconds = null,
    bool? LightContext = null,
    bool? IncludeReasoning = null,
    bool? SkipIfScratchEmpty = null,
    IReadOnlyList<string>? ToolsAllow = null,
    IReadOnlyList<string>? Fallbacks = null)
{
    public bool IsReadOnly => Kind is CronPayloadKind.Command or CronPayloadKind.Script or CronPayloadKind.Unknown;
}

public sealed record CronDelivery(
    string Mode,
    string? Channel = null,
    string? To = null,
    string? AccountId = null,
    string? ThreadId = null,
    bool? BestEffort = null,
    string? Target = null,
    string? DirectPolicy = null)
{
    public JsonElement? CompletionDestination { get; init; }
    public JsonElement? FailureDestination { get; init; }
}

public sealed record CronFailureAlert(
    bool Disabled,
    int? After = null,
    long? CooldownMs = null,
    string? Channel = null,
    string? To = null,
    string? Mode = null,
    string? AccountId = null,
    bool? IncludeSkipped = null)
{
    public static CronFailureAlert Off { get; } = new(true);
}

public sealed record CronActiveHours(string Start, string End, string? Timezone);

public sealed record CronTrigger(string Script, bool Once);

/// <summary>Values the Gateway omitted stay null rather than being filled with defaults.</summary>
public sealed record CronAutoDisabled(string? Reason, long? AtMs, int? ConsecutiveErrors)
{
    public bool IsScheduleErrors => Reason == "schedule-errors";
}

public sealed record CronJobState
{
    public long? NextRunAtMs { get; init; }
    public long? RunningAtMs { get; init; }
    public long? LastRunAtMs { get; init; }
    public string? LastRunStatus { get; init; }
    public string? LastError { get; init; }
    public long? LastDurationMs { get; init; }
    public int? ConsecutiveErrors { get; init; }
    public int? ScheduleErrorCount { get; init; }
    public CronAutoDisabled? AutoDisabled { get; init; }
    public string? LastDeliveryStatus { get; init; }
    public string? LastDeliveryError { get; init; }
    public string? DeliverySuppressionReason { get; init; }
    public int? TriggerEvalCount { get; init; }
    public long? LastTriggerEvalAtMs { get; init; }
    public long? LastTriggerFireAtMs { get; init; }
    public string? SourceIdentity { get; init; }
    public string? StreamStatus { get; init; }
    public string? StreamError { get; init; }

    public static CronJobState Empty { get; } = new();
}

public sealed record CronDeliveryPreview(string Label, string? Detail);

public sealed record CronJob
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    /// <summary>Explicit owner as stored; null means the Gateway default agent. Written back on save.</summary>
    public string? AgentId { get; init; }
    /// <summary>Owner the Gateway resolved for display (<c>effectiveAgentId</c> on list rows).</summary>
    public string? EffectiveAgentId { get; init; }
    public string? SessionKey { get; init; }
    public bool Enabled { get; init; }
    public bool? DeleteAfterRun { get; init; }
    public long? CreatedAtMs { get; init; }
    public long? UpdatedAtMs { get; init; }
    public string? ConfigRevision { get; init; }
    public required CronSchedule Schedule { get; init; }
    public CronActiveHours? ActiveHours { get; init; }
    public bool? IdleOnly { get; init; }
    public CronTrigger? Trigger { get; init; }
    /// <summary>As sent by the Gateway; null when omitted. The editor derives a default but writes it back only when changed.</summary>
    public string? SessionTarget { get; init; }
    public string? WakeMode { get; init; }
    public required CronPayload Payload { get; init; }
    public CronDelivery? Delivery { get; init; }
    /// <summary>Null inherits the global setting; <see cref="CronFailureAlert.Disabled"/> means explicitly off.</summary>
    public CronFailureAlert? FailureAlert { get; init; }
    public CronJobState State { get; init; } = CronJobState.Empty;
    public JsonElement? Pacing { get; init; }
    public CronDeliveryPreview? DeliveryPreview { get; init; }

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;
    /// <summary>Agent that runs this job: explicit owner, else the Gateway-resolved default.</summary>
    public string? Owner => string.IsNullOrWhiteSpace(AgentId) ? EffectiveAgentId : AgentId;
    public bool IsRunning => State.RunningAtMs is > 0;
    public bool IsAutoDisabled => State.AutoDisabled is not null;
    public bool HasTrigger => Trigger is not null;

    /// <summary>Matches upstream "active failure": auto-disabled, or enabled with an error as its last outcome.</summary>
    public bool IsFailing => IsAutoDisabled || (Enabled && string.Equals(State.LastRunStatus, "error", StringComparison.Ordinal));

    /// <summary>A one-shot job that already ran and is no longer scheduled.</summary>
    public bool IsCompletedOneShot => Schedule.Kind == CronScheduleKind.At && !Enabled && State.LastRunAtMs is > 0;
}

public sealed record CronListPage(
    IReadOnlyList<CronJob> Jobs,
    int? Total,
    int Offset,
    int Limit,
    bool HasMore,
    int? NextOffset);

public sealed record CronSchedulerStatus(bool? Enabled, long? NextWakeAtMs, int? JobCount, string? StorePath);

public sealed record CronRunEntry
{
    public long? Ts { get; init; }
    public required string JobId { get; init; }
    public string? JobName { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? Summary { get; init; }
    public string? DiagnosticsSummary { get; init; }
    public string? DeliveryStatus { get; init; }
    public bool? Delivered { get; init; }
    public string? DeliveryError { get; init; }
    public string? DeliverySuppressionReason { get; init; }
    public string? SessionId { get; init; }
    public string? SessionKey { get; init; }
    public string? RunId { get; init; }
    public long? RunAtMs { get; init; }
    public long? DurationMs { get; init; }
    public long? NextRunAtMs { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public bool? TriggerFired { get; init; }
    public JsonElement Raw { get; init; }

    public long? EffectiveRunAtMs => RunAtMs ?? Ts;
    public string Key => !string.IsNullOrWhiteSpace(RunId) ? RunId! : $"{JobId}:{EffectiveRunAtMs}";
}

public sealed record CronRunsPage(
    IReadOnlyList<CronRunEntry> Entries,
    int? Total,
    int Offset,
    int Limit,
    bool HasMore,
    int? NextOffset);

public static class CronProtocol
{
    public const string McpEventsSource = "mcp-events";
    public const string ChannelLast = "last";
    public const string AdminScope = "operator.admin";
    /// <summary>Gateway detail code for an <c>expectedConfigRevision</c> mismatch.</summary>
    public const string JobChangedCode = "CRON_JOB_CHANGED";
    /// <summary>Gateway detail code when a job id no longer exists.</summary>
    public const string JobNotFoundCode = "CRON_JOB_NOT_FOUND";
    public const int DefaultPageSize = 50;
    public const long TriggerMinIntervalMs = 30_000;
    public const long DefaultTopOfHourStaggerMs = 5 * 60 * 1000;

    /// <summary>
    /// Gateway web dashboard route for Automations, relative to the dashboard base.
    /// <c>/cron</c> is the documented, long-lived alias of <c>/automations</c>, and
    /// <c>?job=</c> opens one exact job. Gateways without job links open the list.
    /// The web page has no agent-filter route, so agent context is not encoded.
    /// </summary>
    public static string WebPath(string? jobId) =>
        string.IsNullOrWhiteSpace(jobId) ? "cron" : "cron?job=" + Uri.EscapeDataString(jobId);
}
