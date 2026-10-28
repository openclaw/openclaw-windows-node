using System.Text.Json;

namespace OpenClaw.Shared.Systems;

/// <summary>
/// Tolerant readers for the Systems Gateway payloads. Malformed entries are
/// skipped so one bad row cannot hide the rest of the inventory; a missing
/// top-level collection is a protocol error and throws.
/// </summary>
public static class SystemsParser
{
    public static IReadOnlyList<SystemEnvironment> ParseEnvironments(JsonElement payload)
    {
        var items = RequiredArray(payload, "environments");
        var result = new List<SystemEnvironment>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (ParseEnvironment(item) is { } environment)
                result.Add(environment);
        }
        return result;
    }

    public static SystemEnvironment? ParseEnvironment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            String(item, "id") is not { } id ||
            String(item, "type") is not { } type)
        {
            return null;
        }

        return new SystemEnvironment
        {
            Id = id,
            Type = type,
            Label = String(item, "label"),
            Status = ParseStatus(String(item, "status")),
            Platform = String(item, "platform"),
            SessionHost = Bool(item, "sessionHost") == true,
            WorkerSlots = Object(item, "workerSlots") is { } slots && Int(slots, "total") is { } total && Int(slots, "available") is { } available
                ? new WorkerSlots(total, available)
                : null,
            LastConnectedAt = Time(item, "lastConnectedAtMs"),
            LastDisconnectedAt = Time(item, "lastDisconnectedAtMs"),
            LastSeenAt = Time(item, "lastSeenAtMs"),
            LastSeenReason = String(item, "lastSeenReason"),
            Capabilities = Strings(item, "capabilities"),
            InvocableCommands = Strings(item, "invocableCommands"),
            Desktop = Bool(item, "desktop") == true,
            DesktopAvailability = Object(item, "desktopAvailability") is { } desktop
                ? String(desktop, "state") switch
                {
                    "locked" => DesktopLockState.Locked,
                    "unlocked" => DesktopLockState.Unlocked,
                    _ => DesktopLockState.Unknown,
                }
                : null,
            Issues = Array(item, "issues")?.EnumerateArray()
                .Where(issue => issue.ValueKind == JsonValueKind.Object && String(issue, "code") is not null)
                .Select(issue => new RuntimeTargetIssue(String(issue, "code")!, String(issue, "message")))
                .ToArray() ?? System.Array.Empty<RuntimeTargetIssue>(),
            Worker = Object(item, "worker") is { } worker && String(worker, "providerId") is { } provider
                ? new WorkerMetadata(
                    provider,
                    String(worker, "profileId"),
                    String(worker, "state") ?? "unknown",
                    Strings(worker, "attachedSessionIds"),
                    String(worker, "tunnelStatus"),
                    String(worker, "error"),
                    Bool(worker, "desktop"))
                : null,
            DesktopSetup = Object(item, "desktopSetup") is { } setup
                ? new DesktopSetup(String(setup, "state") switch
                {
                    "ready" => DesktopSetupState.Ready,
                    "needs-server" => DesktopSetupState.NeedsServer,
                    "unsupported" => DesktopSetupState.Unsupported,
                    "managed" => DesktopSetupState.Managed,
                    _ => DesktopSetupState.Unknown,
                }, String(setup, "detail"))
                : null,
        };
    }

    public static SystemStatus ParseStatus(string? status) => status switch
    {
        "available" => SystemStatus.Online,
        "unavailable" => SystemStatus.Offline,
        "starting" => SystemStatus.Starting,
        "stopping" => SystemStatus.Stopping,
        "error" => SystemStatus.Error,
        _ => SystemStatus.Unknown,
    };

    public static GatewaySystemInfo? ParseSystemInfo(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || ParseMeasurements(payload) is not { } measurements)
            return null;
        return new GatewaySystemInfo
        {
            MachineName = String(payload, "machineName") ?? "",
            Hostname = String(payload, "hostname") ?? "",
            Platform = String(payload, "platform") ?? "",
            Release = String(payload, "release") ?? "",
            Arch = String(payload, "arch") ?? "",
            OsLabel = String(payload, "osLabel") ?? "",
            LanAddress = String(payload, "lanAddress"),
            Port = Int(payload, "port"),
            NodeVersion = String(payload, "nodeVersion") ?? "",
            UptimeMs = Long(payload, "uptimeMs") ?? 0,
            CpuModel = String(payload, "cpuModel"),
            Measurements = measurements,
        };
    }

    /// <summary>Reads a <c>hostStats</c> object from <c>node.list</c> or a <c>node.hostStats</c> event.</summary>
    public static NodeHostStats? ParseNodeHostStats(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Time(value, "updatedAtMs") is not { } updatedAt ||
            ParseMeasurements(value) is not { } measurements)
        {
            return null;
        }
        return new NodeHostStats(measurements, updatedAt);
    }

    public static HostMeasurements? ParseMeasurements(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Int(value, "cpuCount") is not { } cpuCount ||
            Long(value, "memoryTotalBytes") is not { } memoryTotal ||
            Long(value, "memoryFreeBytes") is not { } memoryFree)
        {
            return null;
        }

        // loadAverage is [1, 5, 15] minutes; only the one-minute value is shown.
        double? load1 = null;
        if (Array(value, "loadAverage") is { } load && load.GetArrayLength() == 3 &&
            load[0] is { ValueKind: JsonValueKind.Number } first && first.TryGetDouble(out var one) && double.IsFinite(one))
        {
            load1 = one;
        }

        IReadOnlyList<DiskStats>? disks = null;
        if (Array(value, "disks") is { } diskArray)
        {
            disks = diskArray.EnumerateArray()
                .Where(disk => disk.ValueKind == JsonValueKind.Object)
                .Select(disk => String(disk, "path") is { } path && Long(disk, "totalBytes") is { } total && Long(disk, "availableBytes") is { } available
                    ? new DiskStats(path, total, available)
                    : null)
                .OfType<DiskStats>()
                .ToArray();
        }

        return new HostMeasurements
        {
            CpuCount = cpuCount,
            Load1 = load1,
            MemoryTotalBytes = memoryTotal,
            MemoryFreeBytes = memoryFree,
            DiskTotalBytes = Long(value, "diskTotalBytes"),
            DiskAvailableBytes = Long(value, "diskAvailableBytes"),
            Disks = disks,
        };
    }

    /// <summary>Reads the <c>placement</c> projection of one <c>sessions.list</c> row.</summary>
    public static SessionPlacementInfo? ParseSessionPlacement(JsonElement row)
    {
        if (Object(row, "placement") is not { } placement)
            return null;
        var runner = Object(placement, "runner");
        return new SessionPlacementInfo(
            String(placement, "state") switch
            {
                "local" => SessionPlacementState.Local,
                "requested" => SessionPlacementState.Requested,
                "provisioning" => SessionPlacementState.Provisioning,
                "syncing" => SessionPlacementState.Syncing,
                "starting" => SessionPlacementState.Starting,
                "active" => SessionPlacementState.Active,
                "draining" => SessionPlacementState.Draining,
                "reconciling" => SessionPlacementState.Reconciling,
                "reclaimed" => SessionPlacementState.Reclaimed,
                "failed" => SessionPlacementState.Failed,
                _ => SessionPlacementState.Unknown,
            },
            String(placement, "environmentId"),
            runner is { } r ? String(r, "deviceId") : null);
    }

    public static BackupStatus ParseBackupStatus(JsonElement payload)
    {
        var targets = RequiredArray(payload, "targets").EnumerateArray()
            .Select(ParseBackupTarget).OfType<BackupTarget>().ToArray();
        var schedules = Items(payload, "schedules")
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => String(entry, "id") is { } id && String(entry, "target") is { } target
                ? new BackupSchedule(id, String(entry, "mode") ?? "", target, String(entry, "namespace"),
                    Bool(entry, "enabled") == true, Time(entry, "nextRunAtMs"))
                : null)
            .OfType<BackupSchedule>().ToArray();
        return new BackupStatus(targets, schedules, ParseStorageLocations(payload));
    }

    public static IReadOnlyList<StorageLocation> ParseStorageLocations(JsonElement payload) =>
        Items(payload, "locations")
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => String(entry, "name") is { } name
                ? new StorageLocation(name, String(entry, "provider") ?? "", String(entry, "displayTarget"),
                    Bool(entry, "encrypted") == true)
                : null)
            .OfType<StorageLocation>().ToArray();

    private static BackupTarget? ParseBackupTarget(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            String(entry, "target") is not { } target ||
            Object(entry, "latest") is not { } latestElement ||
            ParseBackupRun(latestElement) is not { } latest)
        {
            return null;
        }
        return new BackupTarget(
            ParseBackupKind(String(entry, "kind")),
            target,
            String(entry, "namespace"),
            latest,
            Object(entry, "latestOk") is { } ok ? ParseBackupRun(ok) : null);
    }

    public static BackupKind ParseBackupKind(string? kind) => kind switch
    {
        "archive" => BackupKind.Archive,
        "sqlite-snapshot" => BackupKind.SqliteSnapshot,
        "git" => BackupKind.Git,
        "external" => BackupKind.External,
        _ => BackupKind.Unknown,
    };

    private static BackupRunRecord? ParseBackupRun(JsonElement run)
    {
        if (Time(run, "createdAt") is not { } createdAt)
            return null;
        var location = Object(run, "location") is { } l && String(l, "name") is { } name
            ? new BackupRunLocation(name, String(l, "provider") ?? "", Long(l, "storedBytes") ?? 0)
            : null;
        return new BackupRunRecord(
            createdAt,
            String(run, "status") == "ok",
            String(run, "error"),
            Bool(run, "pushFailed") == true,
            Long(run, "bytes"),
            location);
    }

    public static StorageProbeResult ParseStorageProbe(JsonElement payload) =>
        new(String(payload, "state") switch
        {
            "ok" => StorageProbeState.Ok,
            "unavailable" => StorageProbeState.Unavailable,
            "uninitialized" => StorageProbeState.Uninitialized,
            "wrong-key" => StorageProbeState.WrongKey,
            _ => StorageProbeState.Error,
        },
        Long(payload, "freeBytes"),
        Long(payload, "totalBytes"),
        String(payload, "message"));

    /// <summary>True when a config snapshot has <c>desktop.host.enabled === true</c>.</summary>
    public static bool IsHostDesktopEnabled(JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root)
            return false;
        // config.get returns { config: {...} } while some callers keep the parsed object directly.
        var body = Object(root, "config") ?? Object(root, "parsed") ?? root;
        return Object(body, "desktop") is { } desktop &&
            Object(desktop, "host") is { } host &&
            Bool(host, "enabled") == true;
    }

    private static JsonElement RequiredArray(JsonElement value, string name) =>
        Array(value, name) ?? throw new InvalidOperationException($"The Gateway returned an invalid {name} list.");

    private static IEnumerable<JsonElement> Items(JsonElement value, string name) =>
        Array(value, name) is { } array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();

    internal static JsonElement? Array(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) &&
        array.ValueKind == JsonValueKind.Array ? array : null;

    internal static JsonElement? Object(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var obj) &&
        obj.ValueKind == JsonValueKind.Object ? obj : null;

    internal static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static bool? Bool(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static long? Long(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        if (property.TryGetInt64(out var result))
            return result >= 0 ? result : null;
        return property.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d <= long.MaxValue ? (long)d : null;
    }

    private static int? Int(JsonElement value, string name) =>
        Long(value, name) is { } l && l <= int.MaxValue ? (int)l : null;

    private static DateTimeOffset? Time(JsonElement value, string name) =>
        Long(value, name) is { } ms && ms <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static IReadOnlyList<string> Strings(JsonElement value, string name) =>
        Array(value, name)?.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
            .Select(entry => entry.GetString()!)
            .ToArray() ?? System.Array.Empty<string>();
}
