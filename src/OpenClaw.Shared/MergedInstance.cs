namespace OpenClaw.Shared;

/// <summary>
/// Status bucket for a <see cref="MergedInstance"/>, drives the colored presence dot
/// in the Instances page. Mirrors thresholds in macOS InstancesSettings.swift.
/// </summary>
public enum PresenceStatus
{
    /// <summary>Online and seen within the active window (default ≤120s).</summary>
    Active,

    /// <summary>Online but past the active window (default ≤300s).</summary>
    Idle,

    /// <summary>Presence beacon exists but is past the idle window.</summary>
    Stale,

    /// <summary>Node is paired with the gateway but no presence beacon was received.</summary>
    Offline,

    /// <summary>Row represents the gateway itself — status dot is suppressed in UI.</summary>
    Gateway,
}

/// <summary>
/// A unified row for the Instances page. Combines a <see cref="PresenceEntry"/>
/// (broad: gateway + every connected platform) with an optional <see cref="GatewayNodeInfo"/>
/// (rich: paired Windows nodes only).
/// </summary>
/// <remarks>
/// Either <see cref="Presence"/> or <see cref="Node"/> is always non-null. When both are
/// set, the row represents a paired Windows node that is currently online — the page may
/// expose Rename/Forget and capability/command/permission expanders. When only
/// <see cref="Node"/> is set, the node is paired-but-offline and we still render a row
/// so the user can manage (rename/forget) it.
/// </remarks>
public sealed class MergedInstance
{
    /// <summary>
    /// Stable identity used as dictionary key for state preservation across re-renders
    /// (e.g. remembering which Manage expanders are open). Falls back through
    /// NodeId → DeviceId → InstanceId → Host|Ip so something is always non-empty.
    /// </summary>
    public required string Key { get; init; }

    public PresenceEntry? Presence { get; init; }

    public GatewayNodeInfo? Node { get; init; }

    public PresenceStatus Status { get; init; }

    /// <summary>True when this row represents the gateway itself (mode == "gateway").</summary>
    public bool IsGateway { get; init; }

    /// <summary>True when this row matches the local OpenClaw tray's own node identity.</summary>
    public bool IsThisInstance { get; init; }

    /// <summary>True when a Manage expander (rename/forget/caps/etc.) should be available.</summary>
    public bool IsManaged => CanManageNode;

    /// <summary>
    /// True when node management actions are safe for this row. Weak host/display-name
    /// matches may carry node data for display enrichment, but must not expose Rename/Forget.
    /// </summary>
    public bool CanManageNode { get; init; }

    public string DisplayName { get; init; } = "";
    public string? Ip { get; init; }
    public string? Version { get; init; }
    public string? Platform { get; init; }
    public string? DeviceFamily { get; init; }
    public string? ModelIdentifier { get; init; }
    public string? Mode { get; init; }
    public int? LastInputSeconds { get; init; }
    public string? Reason { get; init; }
    public DateTime? Timestamp { get; init; }

    /// <summary>Capability count for paired Windows nodes (from node.list). 0 when unknown.</summary>
    public int CapabilityCount { get; init; }

    /// <summary>Command count for paired Windows nodes (from node.list). 0 when unknown.</summary>
    public int CommandCount { get; init; }

    /// <summary>
    /// Roles asserted by the connected client (e.g. ["operator","node"]). Sourced
    /// from <see cref="PresenceEntry.Roles"/>. Empty when not provided.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Best-effort identifier for the row, shown as the small monospace caption
    /// under the metadata line. Prefers <see cref="PresenceEntry.InstanceId"/>,
    /// then <see cref="PresenceEntry.DeviceId"/>, then <see cref="GatewayNodeInfo.NodeId"/>.
    /// </summary>
    public string? IdentityCaption { get; init; }

    /// <summary>
    /// Raw protocol status string from <see cref="GatewayNodeInfo.Status"/> (e.g.
    /// "online", "pairing"). Surfaced as a supplementary caption when meaningful
    /// (i.e. non-empty and not redundant with the computed PresenceStatus).
    /// </summary>
    public string? NodeStatusRaw { get; init; }

    /// <summary>Original debug text from the presence beacon (for copy-debug context menu).</summary>
    public string? DebugText { get; init; }
}

/// <summary>
/// Options controlling <see cref="InstanceMerger.Merge"/> behavior.
/// </summary>
public sealed class InstanceMergeOptions
{
    /// <summary>
    /// Stable local node identity (e.g. from the registered gateway record). Preferred
    /// over <see cref="LocalHost"/> when set — avoids hostname collisions in multi-machine
    /// scenarios with the same Windows hostname.
    /// </summary>
    public string? LocalNodeId { get; init; }

    /// <summary>Local machine hostname; fallback when <see cref="LocalNodeId"/> is null.</summary>
    public string? LocalHost { get; init; }

    /// <summary>Presence ≤ this age is Active. Default 120s (matches macOS).</summary>
    public TimeSpan ActiveThreshold { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Presence ≤ this age is Idle. Default 300s (matches macOS).</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Optional debug hook invoked once per <see cref="GatewayNodeInfo"/> that fails to
    /// match any presence row. The string is a one-line summary safe to log.
    /// Useful for surfacing ID-shape drift between presence beacons and node.list payloads.
    /// </summary>
    public Action<string>? OnUnmatchedNode { get; init; }

    /// <summary>Reference clock; overridable in tests. Defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime>? NowUtc { get; init; }
}
