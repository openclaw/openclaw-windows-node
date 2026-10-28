namespace OpenClaw.Shared.Systems;

/// <summary>Gateway runtime state for one environment (<c>environments.list</c> <c>status</c>).</summary>
public enum SystemStatus
{
    Online,
    Offline,
    Starting,
    Stopping,
    Error,
    Unknown,
}

/// <summary>Inventory group. Clients are presence-only connections (apps, browsers) without an environment.</summary>
public enum SystemKind
{
    Host,
    Worker,
    Node,
    Client,
}

public enum DesktopSetupState
{
    Ready,
    NeedsServer,
    Unsupported,
    Managed,
    Unknown,
}

public enum DesktopLockState
{
    Locked,
    Unlocked,
    Unknown,
}

public sealed record DesktopSetup(DesktopSetupState State, string? Detail);

public sealed record WorkerSlots(int Total, int Available);

public sealed record RuntimeTargetIssue(string Code, string? Message);

public sealed record WorkerMetadata(
    string ProviderId,
    string? ProfileId,
    string State,
    IReadOnlyList<string> AttachedSessionIds,
    string? TunnelStatus,
    string? Error,
    bool? Desktop);

/// <summary>One machine as reported by <c>environments.list</c>.</summary>
public sealed record SystemEnvironment
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Label { get; init; }
    public SystemStatus Status { get; init; }
    public string? Platform { get; init; }
    public bool SessionHost { get; init; }
    public WorkerSlots? WorkerSlots { get; init; }
    public DateTimeOffset? LastConnectedAt { get; init; }
    public DateTimeOffset? LastDisconnectedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public string? LastSeenReason { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InvocableCommands { get; init; } = Array.Empty<string>();
    public bool Desktop { get; init; }
    public DesktopLockState? DesktopAvailability { get; init; }
    public IReadOnlyList<RuntimeTargetIssue> Issues { get; init; } = Array.Empty<RuntimeTargetIssue>();
    public WorkerMetadata? Worker { get; init; }
    public DesktopSetup? DesktopSetup { get; init; }

    /// <summary>Paired-node id for <c>node:&lt;id&gt;</c> environments; null otherwise.</summary>
    public string? NodeId => Id.StartsWith(SystemsProjection.NodeIdPrefix, StringComparison.Ordinal)
        ? Id[SystemsProjection.NodeIdPrefix.Length..]
        : null;
}

public sealed record DiskStats(string Path, long TotalBytes, long AvailableBytes);

/// <summary>Resource measurements shared by <c>system.info</c> and node <c>hostStats</c>.</summary>
public sealed record HostMeasurements
{
    public int CpuCount { get; init; }
    public double? Load1 { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long MemoryFreeBytes { get; init; }
    public long? DiskTotalBytes { get; init; }
    public long? DiskAvailableBytes { get; init; }
    public IReadOnlyList<DiskStats>? Disks { get; init; }

    public long MemoryUsedBytes => Math.Max(0, MemoryTotalBytes - MemoryFreeBytes);
}

/// <summary>Node-reported host statistics and the Gateway receipt time.</summary>
public sealed record NodeHostStats(HostMeasurements Measurements, DateTimeOffset UpdatedAt);

/// <summary>Gateway host identity and resource snapshot (<c>system.info</c>).</summary>
public sealed record GatewaySystemInfo
{
    public string MachineName { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Release { get; init; } = "";
    public string Arch { get; init; } = "";
    public string OsLabel { get; init; } = "";
    public string? LanAddress { get; init; }
    public int? Port { get; init; }
    public string NodeVersion { get; init; } = "";
    public long UptimeMs { get; init; }
    public string? CpuModel { get; init; }
    public required HostMeasurements Measurements { get; init; }
}

public enum SessionPlacementState
{
    Local,
    Requested,
    Provisioning,
    Syncing,
    Starting,
    Active,
    Draining,
    Reconciling,
    Reclaimed,
    Failed,
    Unknown,
}

/// <summary>Session execution placement projected by <c>sessions.list</c>.</summary>
public sealed record SessionPlacementInfo(
    SessionPlacementState State,
    string? EnvironmentId,
    string? RunnerDeviceId);

/// <summary>Minimal session identity used to relate sessions to machines.</summary>
public sealed record SystemsSessionRef(
    string Key,
    string Title,
    SessionPlacementInfo? Placement,
    string? ExecNode);

/// <summary>Recorded bindings, not proof that a model turn ran on the machine.</summary>
public enum SessionRelationKind
{
    Placement,
    RetainedPlacement,
    Runner,
    ExecBinding,
    Gateway,
}

public sealed record SessionRelation(SessionRelationKind Kind, SystemsSessionRef Session);

public enum BackupKind
{
    Archive,
    SqliteSnapshot,
    Git,
    External,
    Unknown,
}

public sealed record BackupRunLocation(string Name, string Provider, long StoredBytes);

public sealed record BackupRunRecord(
    DateTimeOffset CreatedAt,
    bool Ok,
    string? Error,
    bool PushFailed,
    long? Bytes,
    BackupRunLocation? Location);

public sealed record BackupTarget(
    BackupKind Kind,
    string Target,
    string? Namespace,
    BackupRunRecord Latest,
    BackupRunRecord? LatestOk);

public sealed record BackupSchedule(
    string Id,
    string Mode,
    string Target,
    string? Namespace,
    bool Enabled,
    DateTimeOffset? NextRunAt);

public sealed record StorageLocation(string Name, string Provider, string? DisplayTarget, bool Encrypted);

public sealed record BackupStatus(
    IReadOnlyList<BackupTarget> Targets,
    IReadOnlyList<BackupSchedule> Schedules,
    IReadOnlyList<StorageLocation> Locations);

public enum StorageProbeState
{
    Ok,
    Unavailable,
    Uninitialized,
    WrongKey,
    Error,
}

public sealed record StorageProbeResult(StorageProbeState State, long? FreeBytes, long? TotalBytes, string? Message);
