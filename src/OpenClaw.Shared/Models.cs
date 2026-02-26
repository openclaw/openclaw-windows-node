namespace OpenClaw.Shared;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public enum PairingStatus
{
    Unknown,
    Pending,    // Connected but awaiting approval
    Paired,     // Approved with device token
    Rejected    // Pairing was rejected
}

public class PairingStatusEventArgs : EventArgs
{
    public PairingStatus Status { get; }
    public string DeviceId { get; }
    public string? Message { get; }
    
    public PairingStatusEventArgs(PairingStatus status, string deviceId, string? message = null)
    {
        Status = status;
        DeviceId = deviceId;
        Message = message;
    }
}

public enum ActivityKind
{
    Idle,
    Job,
    Exec,
    Read,
    Write,
    Edit,
    Search,
    Browser,
    Message,
    Tool
}

public class AgentActivity
{
    public string SessionKey { get; set; } = "";
    public bool IsMain { get; set; }
    public ActivityKind Kind { get; set; } = ActivityKind.Idle;
    public string State { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Label { get; set; } = "";

    public string Glyph => Kind switch
    {
        ActivityKind.Exec => "💻",
        ActivityKind.Read => "📄",
        ActivityKind.Write => "✍️",
        ActivityKind.Edit => "📝",
        ActivityKind.Search => "🔍",
        ActivityKind.Browser => "🌐",
        ActivityKind.Message => "💬",
        ActivityKind.Tool => "🛠️",
        ActivityKind.Job => "⚡",
        _ => ""
    };

    public string DisplayText => Kind == ActivityKind.Idle
        ? ""
        : $"{(IsMain ? "Main" : "Sub")} · {Glyph} {Label}";
}

public class OpenClawNotification
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsChat { get; set; } = false; // True if from chat response

    // Structured metadata (populated by gateway when available)
    public string? Channel { get; set; }   // e.g. telegram, email, chat
    public string? Agent { get; set; }     // agent name/identifier
    public string? Intent { get; set; }    // normalized intent (reminder, build, alert)
    public string[]? Tags { get; set; }    // free-form routing tags
}

/// <summary>
/// A user-defined notification categorization rule.
/// </summary>
public class UserNotificationRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public string Category { get; set; } = "info";
    public bool Enabled { get; set; } = true;
}

public class ChannelHealth
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public bool IsLinked { get; set; }
    public string? Error { get; set; }
    public string? AuthAge { get; set; }
    public string? Type { get; set; }

    /// <summary>
    /// Returns true if the given status string represents a healthy/running channel.
    /// Use this instead of inline status checks to keep the healthy-status set consistent.
    /// </summary>
    public static bool IsHealthyStatus(string? status) =>
        status?.ToLowerInvariant() is "ok" or "connected" or "running" or "active" or "ready";

    /// <summary>
    /// Returns true if the given status string represents an intermediate (not yet healthy, not error) state.
    /// </summary>
    public static bool IsIntermediateStatus(string? status) =>
        status?.ToLowerInvariant() is "stopped" or "idle" or "paused" or "configured" or "pending"
            or "connecting" or "reconnecting";

    public string DisplayText
    {
        get
        {
            var label = Status.ToLowerInvariant() switch
            {
                "ok" or "connected" or "running" => "[ON]",
                "linked" => "[LINKED]",
                "ready" => "[READY]",
                "connecting" or "reconnecting" => "[...]",
                "error" or "disconnected" => "[ERR]",
                "stale" => "[STALE]",
                "configured" or "stopped" => "[OFF]",
                "not configured" => "[N/A]",
                _ => "[OFF]"
            };
            var detail = IsLinked && AuthAge != null ? $"linked · {AuthAge}" : Status;
            if (Error != null) detail += $" ({Error})";
            return $"{label} {Capitalize(Name)}: {detail}";
        }
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

public class SessionInfo
{
    public string Key { get; set; } = "";
    public bool IsMain { get; set; }
    public string Status { get; set; } = "unknown";
    public string? Model { get; set; }
    public string? Channel { get; set; }
    public string? DisplayName { get; set; }
    public string? Provider { get; set; }
    public string? Subject { get; set; }
    public string? Room { get; set; }
    public string? Space { get; set; }
    public string? SessionId { get; set; }
    public string? ThinkingLevel { get; set; }
    public string? VerboseLevel { get; set; }
    public bool SystemSent { get; set; }
    public bool AbortedLastRun { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long ContextTokens { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? CurrentActivity { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string DisplayText
    {
        get
        {
            var prefix = IsMain ? "Main" : "Sub";
            var parts = new List<string> { prefix };

            if (!string.IsNullOrEmpty(Channel))
                parts.Add(Channel);

            if (!string.IsNullOrEmpty(CurrentActivity))
                parts.Add(CurrentActivity);
            else if (!string.IsNullOrEmpty(Status) && Status != "unknown" && Status != "active")
                parts.Add(Status);

            return string.Join(" · ", parts);
        }
    }

    public string RichDisplayText
    {
        get
        {
            var title = !string.IsNullOrWhiteSpace(DisplayName)
                ? DisplayName!
                : (IsMain ? "Main session" : "Session");

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(Channel))
                details.Add(Channel!);
            if (!string.IsNullOrWhiteSpace(Model))
                details.Add(Model!);
            if (!string.IsNullOrWhiteSpace(ContextSummaryShort))
                details.Add($"{ContextSummaryShort} ctx");
            if (!string.IsNullOrWhiteSpace(ThinkingLevel))
                details.Add($"think {ThinkingLevel}");
            if (!string.IsNullOrWhiteSpace(VerboseLevel))
                details.Add($"verbose {VerboseLevel}");
            if (SystemSent)
                details.Add("system");
            if (AbortedLastRun)
                details.Add("aborted");
            if (!string.IsNullOrWhiteSpace(CurrentActivity))
                details.Add(CurrentActivity!);
            else if (!string.IsNullOrWhiteSpace(Status) && Status != "unknown" && Status != "active")
                details.Add(Status);

            return details.Count == 0 ? title : $"{title} · {string.Join(" · ", details)}";
        }
    }

    public string AgeText
    {
        get
        {
            var stamp = UpdatedAt ?? LastSeen;
            var delta = DateTime.UtcNow - stamp;
            if (delta.TotalSeconds < 60) return "just now";
            if (delta.TotalMinutes < 60) return $"{(int)Math.Round(delta.TotalMinutes)}m ago";
            if (delta.TotalHours < 48) return $"{(int)Math.Round(delta.TotalHours)}h ago";
            return $"{(int)Math.Round(delta.TotalDays)}d ago";
        }
    }

    public string ContextSummaryShort
    {
        get
        {
            if (TotalTokens <= 0 || ContextTokens <= 0) return "";
            return $"{FormatTokenCount(TotalTokens)}/{FormatTokenCount(ContextTokens)}";
        }
    }
    
    /// <summary>Gets a shortened, user-friendly version of the session key.</summary>
    public string ShortKey
    {
        get
        {
            if (string.IsNullOrEmpty(Key)) return "unknown";
            
            // Extract meaningful part from session keys like "agent:main:subagent:uuid"
            var parts = Key.Split(':');
            if (parts.Length >= 3)
            {
                // Return something like "subagent" or "cron" 
                return parts[^2]; // Second to last part
            }
            
            // For file paths, just return filename
            if (Key.Contains('/') || Key.Contains('\\'))
            {
                return Path.GetFileName(Key);
            }
            
            return Key.Length > 20 ? Key[..17] + "..." : Key;
        }
    }

    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString();
    }
}

public class GatewayUsageInfo
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public double CostUsd { get; set; }
    public int RequestCount { get; set; }
    public string? Model { get; set; }
    public string? ProviderSummary { get; set; }

    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (TotalTokens > 0)
                parts.Add($"Tokens: {FormatCount(TotalTokens)}");
            if (CostUsd > 0)
                parts.Add($"${CostUsd:F2}");
            if (RequestCount > 0)
                parts.Add($"{RequestCount} requests");
            if (!string.IsNullOrEmpty(Model))
                parts.Add(Model);
            if (parts.Count == 0 && !string.IsNullOrEmpty(ProviderSummary))
                parts.Add(ProviderSummary);
            return parts.Count > 0
                ? string.Join(" · ", parts)
                : "No usage data";
        }
    }

    private static string FormatCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString();
    }
}

public class GatewayUsageWindowInfo
{
    public string Label { get; set; } = "";
    public double UsedPercent { get; set; }
    public DateTime? ResetAt { get; set; }
}

public class GatewayUsageProviderInfo
{
    public string Provider { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Plan { get; set; }
    public string? Error { get; set; }
    public List<GatewayUsageWindowInfo> Windows { get; set; } = new();
}

public class GatewayUsageStatusInfo
{
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<GatewayUsageProviderInfo> Providers { get; set; } = new();
}

public class GatewayCostUsageTotalsInfo
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long TotalTokens { get; set; }
    public double TotalCost { get; set; }
    public int MissingCostEntries { get; set; }
}

public class GatewayCostUsageDayInfo
{
    public string Date { get; set; } = "";
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long TotalTokens { get; set; }
    public double TotalCost { get; set; }
    public int MissingCostEntries { get; set; }
}

public class GatewayCostUsageInfo
{
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Days { get; set; }
    public GatewayCostUsageTotalsInfo Totals { get; set; } = new();
    public List<GatewayCostUsageDayInfo> Daily { get; set; } = new();
}

public class SessionPreviewItemInfo
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
}

public class SessionPreviewInfo
{
    public string Key { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public List<SessionPreviewItemInfo> Items { get; set; } = new();
}

public class SessionsPreviewPayloadInfo
{
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<SessionPreviewInfo> Previews { get; set; } = new();
}

public class SessionCommandResult
{
    public string Method { get; set; } = "";
    public bool Ok { get; set; }
    public string? Key { get; set; }
    public bool? Deleted { get; set; }
    public bool? Compacted { get; set; }
    public int? Kept { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
}

public class GatewayNodeInfo
{
    public string NodeId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public string? Platform { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool IsOnline { get; set; }
    public int CapabilityCount { get; set; }
    public int CommandCount { get; set; }

    public string ShortId => NodeId.Length <= 12 ? NodeId : NodeId[..12] + "…";

    public string DisplayText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DisplayName) ? ShortId : DisplayName;
            var status = IsOnline ? "online" : (string.IsNullOrWhiteSpace(Status) ? "offline" : Status);
            return $"{name} · {status}";
        }
    }

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Mode))
                parts.Add(Mode!);
            if (!string.IsNullOrWhiteSpace(Platform))
                parts.Add(Platform!);
            if (CommandCount > 0)
                parts.Add($"{CommandCount} cmd");
            if (CapabilityCount > 0)
                parts.Add($"{CapabilityCount} cap");
            if (LastSeen.HasValue)
                parts.Add($"seen {FormatAge(LastSeen.Value)}");
            return parts.Count == 0 ? "no details" : string.Join(" · ", parts);
        }
    }

    private static string FormatAge(DateTime timestampUtc)
    {
        var delta = DateTime.UtcNow - timestampUtc;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)Math.Round(delta.TotalMinutes)}m ago";
        if (delta.TotalHours < 48) return $"{(int)Math.Round(delta.TotalHours)}h ago";
        return $"{(int)Math.Round(delta.TotalDays)}d ago";
    }
}

